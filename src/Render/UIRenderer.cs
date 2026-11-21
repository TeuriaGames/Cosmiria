using System.Numerics;
using System.Reflection;
using Riateu;
using Riateu.Graphics;

namespace Cosmiria;

/// <summary>
/// Turns a laid-out node tree into instanced quads (rounded rects, glyphs, icons, images) and
/// draws them with a single pipeline. Glyphs of every font and size share one atlas (slot 0);
/// slot 1 holds the icon atlas or a raster image, so a new draw call starts only when the
/// scissor rect changes or a raster image is drawn.
/// <para>
/// Work is reused between frames. Idle frames (no rebuild) just replay the draw calls. After a
/// rebuild, a text element whose text, style, box, clip and transform are unchanged copies its
/// glyph quads from the previous frame instead of placing glyphs again, and only the range of
/// quads that differs from what the GPU holds is uploaded (nothing when the rebuild changed nothing
/// visible).
/// </para>
/// </summary>
internal sealed class UIRenderer : IDisposable
{
    private struct DrawCall
    {
        /// <summary>Slot 1: a raster image, or null for the icon atlas.</summary>
        public Texture Image;
        public Rectangle Scissor;
        public uint Offset;
        public uint Count;
        /// <summary>Slot 1 is decided: the call has icon or image quads.</summary>
        public bool SlotBound;
        /// <summary>Slot 1 is sampled with nearest filtering (only with an Image).</summary>
        public bool Nearest;
        /// <summary>
        /// A user texture (a game's render target): its pixels change without a rebuild, so it's
        /// drawn every frame instead of being cached in a layer. Always a call of its own.
        /// </summary>
        public bool Live;
        public EffectKind Effect;
        /// <summary>Pixels the effect reads (and, for a blur group, covers), in the target.</summary>
        public Rectangle Region;
        /// <summary>Blur standard deviation in pixels.</summary>
        public float Sigma;
    }

    /// <summary>
    /// Calls that need passes of their own. <see cref="Backdrop"/> blurs what's been drawn so far
    /// under its region and draws it with its one quad. The calls between <see cref="GroupBegin"/>
    /// and <see cref="GroupEnd"/> are drawn into a texture of their own, which is blurred and drawn
    /// with the end call's quad.
    /// </summary>
    private enum EffectKind : byte { None, Backdrop, GroupBegin, GroupEnd }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BlurUniforms
    {
        public Vector4 SourceRect;
        public Vector2 Step;
        public float Sigma;
        public float Taps;
    }

    private readonly GraphicsDevice device;
    private readonly TextureFormat targetFormat;
    private readonly GraphicsPipeline pipeline;
    private readonly GraphicsPipeline blurPipeline;
    private readonly Sampler sampler;
    private readonly Sampler nearestSampler;

    private UIQuad[] quads = new UIQuad[1024];
    private int quadCount;
    // The previous frame's quads (text elements copy theirs from here), and what the GPU buffer holds.
    private UIQuad[] previousQuads = new UIQuad[1024];
    private UIQuad[] gpuQuads = new UIQuad[1024];
    private int gpuQuadCount;
    private int paintFrame;

    /// <summary>
    /// A text element's glyph quads from the frame it was painted in, with everything they depend
    /// on: when all of it matches, they're copied instead of placed again.
    /// </summary>
    private struct TextQuads
    {
        public int Frame, Start, Count;
        public ShapedText Shaped;
        public float X, Y, W, H, ClipY0, ClipY1;
        public Rectangle Scissor;
        public Matrix3x2 Transform;
        public float ScaleX, ScaleY;
        public int GlyphEpoch;
        public Color TextColor;
        public int FontId;
        public float TextSize, BorderWidth;
        public Edges Padding;
        public TextWrapMode Wrapping;
        public TextAlign Align;
    }

    private readonly Dictionary<int, TextQuads> textCache = new();
    private DrawCall[] calls = new DrawCall[32];
    private int callCount;

    private StructuredBuffer<UIQuad> gpuBuffer;
    private TransferBuffer transfer;
    private uint gpuCapacity;
    private bool needsUpload;

    private readonly IconAtlas icons;
    private readonly GlyphAtlas glyphs;
    private readonly ImageCache images;

    // Current transform while painting (window -> window), and the clip it started under.
    private Matrix3x2 transform = Matrix3x2.Identity;
    private bool transformed;
    private float transformScale = 1f;
    private Rectangle outerClip;
    private readonly FocusState focus;
    private readonly Theme theme;
    private UIStack ui;
    private FontRegistry fonts;
    private float scissorScaleX = 1, scissorScaleY = 1;
    private int targetW, targetH;

    public UIRenderer(GraphicsDevice device, TextureFormat targetFormat, FocusState focus, Theme theme)
    {
        this.focus = focus;
        this.theme = theme;
        this.device = device;
        this.targetFormat = targetFormat;

        using var vs = LoadShader("UI.vert");
        using var fs = LoadShader("UI.frag");
        var vert = new Shader(device, vs, "main", new ShaderCreateInfo
        {
            ShaderStage = ShaderStage.Vertex,
            ShaderFormat = GraphicsDevice.BackendShaderFormat,
            UniformBufferCount = 1,
            StorageBufferCount = 1,
        });
        var frag = new Shader(device, fs, "main", new ShaderCreateInfo
        {
            ShaderStage = ShaderStage.Fragment,
            ShaderFormat = GraphicsDevice.BackendShaderFormat,
            SamplerCount = 2,
        });

        pipeline = GraphicsPipeline.CreateBuilder(vert, frag)
            .SetAttachmentInfo(new GraphicsPipelineAttachmentInfo(
                new ColorAttachmentDescription(targetFormat, ColorTargetBlendState.AlphaBlend)))
            .SetDepthStenctilState(DepthStencilState.Disable)
            .SetMultisampleState(MultisampleState.None)
            .SetPrimitiveType(PrimitiveType.TriangleStrip)
            .SetRasterizerState(RasterizerState.CCW_CullNone)
            .Build(device);

        using var blurVs = LoadShader("Blur.vert");
        using var blurFs = LoadShader("Blur.frag");
        var blurVert = new Shader(device, blurVs, "main", new ShaderCreateInfo
        {
            ShaderStage = ShaderStage.Vertex,
            ShaderFormat = GraphicsDevice.BackendShaderFormat,
        });
        var blurFrag = new Shader(device, blurFs, "main", new ShaderCreateInfo
        {
            ShaderStage = ShaderStage.Fragment,
            ShaderFormat = GraphicsDevice.BackendShaderFormat,
            SamplerCount = 1,
            UniformBufferCount = 1,
        });
        blurPipeline = GraphicsPipeline.CreateBuilder(blurVert, blurFrag)
            .SetAttachmentInfo(new GraphicsPipelineAttachmentInfo(
                new ColorAttachmentDescription(targetFormat, ColorTargetBlendState.None)))
            .SetDepthStenctilState(DepthStencilState.Disable)
            .SetMultisampleState(MultisampleState.None)
            .SetPrimitiveType(PrimitiveType.TriangleList)
            .SetRasterizerState(RasterizerState.CCW_CullNone)
            .Build(device);

        sampler = new Sampler(device, SamplerCreateInfo.LinearClamp);
        var nearest = SamplerCreateInfo.LinearClamp;
        nearest.MinFilter = Filter.Nearest;
        nearest.MagFilter = Filter.Nearest;
        nearest.MipmapMode = SamplerMipmapMode.Nearest;
        nearestSampler = new Sampler(device, nearest);
        icons = new IconAtlas(device);
        glyphs = new GlyphAtlas(device);
        images = new ImageCache(device);
        EnsureGpuCapacity(1024);
    }

    private static Stream LoadShader(string name)
    {
        string ext = GraphicsDevice.BackendShaderFormat == ShaderFormat.DXIL ? ".dxil" : ".spv";
        string resource = "Cosmiria.Shaders." + name + ext;
        return typeof(UIRenderer).Assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"Embedded shader '{resource}' not found. Run src/scripts/compile_shaders.py.");
    }

    // ------------------------------------------------------------------ paint (CPU)

    private Func<int, float> getScroll;
    private Action<int, float> setScroll;

    public void Paint(UIStack ui, FontRegistry fonts, int root, float logicalW, float logicalH, int targetW, int targetH,
        Func<int, float> getScroll, Action<int, float> setScroll)
    {
        this.getScroll = getScroll;
        this.setScroll = setScroll;
        this.ui = ui;
        this.fonts = fonts;
        this.targetW = targetW;
        this.targetH = targetH;
        scissorScaleX = logicalW > 0 ? targetW / logicalW : 1;
        scissorScaleY = logicalH > 0 ? targetH / logicalH : 1;
        // This frame's quads go into the other array; the last frame's stay readable for reuse.
        (previousQuads, quads) = (quads, previousQuads);
        if (quads.Length < previousQuads.Length) quads = new UIQuad[previousQuads.Length];
        paintFrame++;
        ReusedTextQuads = 0;
        if (textCache.Count > 4096) PruneTextCache();
        quadCount = 0;
        callCount = 0;
        effectCount = 0;
        deferredCount = 0;
        paintingOverlays = false;
        images.BeginPaint();
        SetTransform(Matrix3x2.Identity);
        PaintNode(root);
        // Overlays (popups) are painted last, in the order they were met, under the transform
        // of the parent they were declared in.
        paintingOverlays = true;
        // Overlays nested in an overlay are deferred again, so they paint after their ancestor.
        for (int i = 0; i < deferredCount; i++)
        {
            SetTransform(deferred[i].transform);
            outerClip = deferred[i].clip;
            overlayRoot = deferred[i].index;
            PaintNode(deferred[i].index);
        }
        overlayRoot = -1;
        paintingOverlays = false;
        SetTransform(Matrix3x2.Identity);
        PaintTooltip(logicalW, logicalH);
        needsUpload = true;
        layersValid = false;
        drawPending = true;
        Stats.Paints++;
        Stats.QuadsPainted += quadCount;
        Stats.TextQuadsReused += ReusedTextQuads;
    }

    /// <summary>The tooltip (text from <see cref="FocusState.TooltipText"/>) near the pointer, kept inside the window.</summary>
    private void PaintTooltip(float logicalW, float logicalH)
    {
        string text = focus.TooltipText;
        if (string.IsNullOrEmpty(text)) return;
        UIFont font = fonts[0];
        float scale = theme.TooltipTextSize / font.Size;
        float wrap = theme.TooltipMaxWidth / scale;
        var (width, lines) = LineBreaker.Measure(font, text, wrap);
        const float padX = 8, padY = 5;
        float w = width * scale + padX * 2, h = lines * font.LineHeight * scale + padY * 2;
        float x = Math.Clamp(focus.TooltipX + 12, 4, MathF.Max(4, logicalW - w - 4));
        float y = focus.TooltipY + 20;
        if (y + h > logicalH - 4) y = focus.TooltipY - h - 8; // above the pointer near the bottom
        Rectangle all = ToScissor(0, 0, logicalW, logicalH);
        ref UIQuad q = ref Push(all, null);
        q.Rect = new Vector4(SnapX(x), SnapY(y), MathF.Round(w), MathF.Round(h));
        q.Color = new Riateu.Graphics.Color(theme.TooltipBackground).ToVector4();
        q.BorderColor = new Riateu.Graphics.Color(theme.TooltipBorder).ToVector4();
        q.BorderWidth = 1;
        q.Radius = new Vector4(5);
        q.UV = default;
        q.Mode = UIQuad.ModeRect;
        var breaker = new LineBreaker(font, text, wrap);
        float line = 0;
        Vector4 color = new Riateu.Graphics.Color(theme.TooltipText).ToVector4();
        while (breaker.Next(out int start, out int length, out _))
        {
            PaintLine(font, breaker.Shaped, start, length, SnapX(x) + padX, SnapY(y) + padY, line, scale, color, all);
            line += font.LineHeight;
        }
    }

    private void SetTransform(in Matrix3x2 m)
    {
        transform = m;
        transformed = !m.IsIdentity;
        // Uniform scale factor of the transform: glyphs and icons are rasterized at it.
        transformScale = transformed ? MathF.Sqrt(MathF.Abs(m.GetDeterminant())) : 1f;
        if (transformScale <= 0) transformScale = 1f;
    }

    /// <summary>Only a translation (glyphs and boxes can still be snapped to whole pixels).</summary>
    private bool TranslationOnly => !transformed || (transform.M11 == 1 && transform.M22 == 1 && transform.M12 == 0 && transform.M21 == 0);

    private (int index, Matrix3x2 transform, Rectangle clip)[] deferred = new (int, Matrix3x2, Rectangle)[8];
    private int deferredCount;
    private bool paintingOverlays;
    private int overlayRoot = -1;

    private void PaintNode(int index)
    {
        ref NodeData n = ref ui.Nodes[index];
        ref Style s = ref ui.Styles[index];
        if (s.Display == Display.None) return;
        if (s.IsOverlay && (!paintingOverlays || index != overlayRoot))
        {
            if (deferredCount == deferred.Length) Array.Resize(ref deferred, deferredCount * 2);
            deferred[deferredCount++] = (index, transform, transformed ? outerClip : default);
            return;
        }

        // Transforms compose down the tree: this node's (around its origin) after its parents'.
        Matrix3x2 saved = transform;
        bool hasOwn = s.Has(StyleFlags.Transform) && s.TransformMask != TransformParts.None;
        if (hasOwn)
        {
            if (!transformed) outerClip = ToScissor(n.ClipX0, n.ClipY0, n.ClipX1, n.ClipY1);
            SetTransform(s.TransformFor(n.X, n.Y, n.W, n.H) * transform);
        }
        if (transformed && Matrix3x2.Invert(transform, out Matrix3x2 inverse))
        {
            n.Inverse = inverse;
            n.Transformed = true;
        }
        float opacity = s.EffectiveOpacity;
        if (opacity <= 0) { if (hasOwn) SetTransform(saved); return; }
        int firstQuad = quadCount;
        float backdrop = s.EffectiveBackdropBlur;
        if (backdrop > 0) PushBackdrop(ref n, ref s, backdrop);
        float blur = s.EffectiveBlur;
        int group = blur > 0 ? BeginBlur(ref n, blur) : -1;
        PaintNodeContent(index, ref n, ref s);
        PaintDecorations(ref n, ref s);
        if (opacity < 1)
        {
            // Fade the element and its subtree (per quad: overlapping children don't group like CSS).
            for (int i = firstQuad; i < quadCount; i++)
            {
                quads[i].Color.W *= opacity;
                quads[i].BorderColor.W *= opacity;
            }
        }
        if (group >= 0) EndBlur(group, ref n);
        if (hasOwn) SetTransform(saved);
    }

    private int effectCount;

    /// <summary>Screen bounding box of the node grown by 3 sigma, in target pixels.</summary>
    private Rectangle EffectRegion(ref NodeData n, float sigma)
    {
        Vector2 a = Vector2.Transform(new Vector2(n.X, n.Y), transform), b = Vector2.Transform(new Vector2(n.X + n.W, n.Y), transform);
        Vector2 c = Vector2.Transform(new Vector2(n.X, n.Y + n.H), transform), d = Vector2.Transform(new Vector2(n.X + n.W, n.Y + n.H), transform);
        Vector2 min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)), max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
        float pad = MathF.Ceiling(sigma * 3);
        int x0 = Math.Clamp((int)MathF.Floor(min.X * scissorScaleX - pad), 0, targetW);
        int y0 = Math.Clamp((int)MathF.Floor(min.Y * scissorScaleY - pad), 0, targetH);
        int x1 = Math.Clamp((int)MathF.Ceiling(max.X * scissorScaleX + pad), 0, targetW);
        int y1 = Math.Clamp((int)MathF.Ceiling(max.Y * scissorScaleY + pad), 0, targetH);
        return new Rectangle(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>An effect call; Backdrop and GroupEnd own the quad pushed right after it.</summary>
    private void AddEffect(EffectKind kind, Rectangle scissor, Rectangle region, float sigma)
    {
        if (callCount == calls.Length) Array.Resize(ref calls, callCount * 2);
        calls[callCount++] = new DrawCall
        {
            Effect = kind, Scissor = scissor, Region = region, Sigma = sigma,
            Offset = (uint)quadCount, Count = kind == EffectKind.GroupBegin ? 0u : 1u,
        };
        effectCount++;
    }

    /// <summary>The blurred backdrop, drawn inside the node's rounded box before its background.</summary>
    private void PushBackdrop(ref NodeData n, ref Style s, float radius)
    {
        Rectangle scissor = ClipScissor(n.ClipX0, n.ClipY0, n.ClipX1, n.ClipY1);
        if (scissor.Width <= 0 || scissor.Height <= 0 || n.W <= 0 || n.H <= 0) return;
        float sigma = radius * scissorScaleY * transformScale;
        Rectangle region = EffectRegion(ref n, sigma);
        if (region.Width <= 0 || region.Height <= 0) return;

        AddEffect(EffectKind.Backdrop, scissor, region, sigma);
        ref UIQuad q = ref NextQuad();
        Vector2 p0 = Vector2.Transform(new Vector2(n.X, n.Y), transform);
        Vector2 p1 = Vector2.Transform(new Vector2(n.X + n.W, n.Y + n.H), transform);
        q.Rect = new Vector4(n.X, n.Y, n.W, n.H);
        q.UV = new Vector4(
            (p0.X * scissorScaleX - region.X) / region.Width, (p0.Y * scissorScaleY - region.Y) / region.Height,
            (p1.X * scissorScaleX - region.X) / region.Width, (p1.Y * scissorScaleY - region.Y) / region.Height);
        q.Color = Vector4.One;
        q.BorderColor = default;
        q.BorderWidth = 0;
        q.Radius = ClampRadius(s.Radius, n.W, n.H);
        q.Mode = UIQuad.ModeImage;
    }

    /// <summary>Start drawing the node's content into a blur group; -1 when nothing of it can show.</summary>
    private int BeginBlur(ref NodeData n, float radius)
    {
        float sigma = radius * scissorScaleY * transformScale;
        Rectangle region = EffectRegion(ref n, sigma);
        if (region.Width <= 0 || region.Height <= 0) return -1;
        int index = callCount;
        AddEffect(EffectKind.GroupBegin, default, region, sigma);
        return index;
    }

    /// <summary>Close the group: its blurred texture is drawn over the region, clipped like the node.</summary>
    private void EndBlur(int begin, ref NodeData n)
    {
        bool any = false;
        for (int i = begin + 1; i < callCount && !any; i++) any = calls[i].Count > 0;
        Rectangle scissor = ClipScissor(n.ClipX0, n.ClipY0, n.ClipX1, n.ClipY1);
        if (!any || scissor.Width <= 0 || scissor.Height <= 0)
        {
            for (int i = begin; i < callCount; i++) if (calls[i].Effect != EffectKind.None) effectCount--;
            callCount = begin;
            return;
        }

        Rectangle region = calls[begin].Region;
        AddEffect(EffectKind.GroupEnd, scissor, region, calls[begin].Sigma);
        ref UIQuad q = ref NextQuad();
        q.Rect = new Vector4(region.X / scissorScaleX, region.Y / scissorScaleY, region.Width / scissorScaleX, region.Height / scissorScaleY);
        q.Matrix = new Vector4(1, 0, 0, 1);
        q.Translate = Vector2.Zero;
        q.UV = new Vector4(0, 0, 1, 1);
        q.Color = Vector4.One;
        q.BorderColor = default;
        q.BorderWidth = 0;
        q.Radius = default;
        q.Mode = UIQuad.ModeImage;
    }

    /// <summary>Overlay scrollbar thumb, and the keyboard focus ring (CSS :focus-visible).</summary>
    private void PaintDecorations(ref NodeData n, ref Style s)
    {
        if ((s.Overflow == Overflow.Scroll || n.Multiline) && n.ScrollMax > 0 &&
            Scrollbar.Compute(ref n, ref s, getScroll(n.StableId), out var g))
        {
            uint color = focus.ScrollbarActive == n.StableId ? theme.ScrollThumbActive
                : focus.ScrollbarHot == n.StableId ? theme.ScrollThumbHover : theme.ScrollThumb;
            Rectangle clip = ClipScissor(n.ClipX0, n.ClipY0, n.ClipX1, n.ClipY1);
            if (clip.Width > 0 && clip.Height > 0)
            {
                ref UIQuad q = ref Push(clip, null);
                q.Rect = g.Vertical
                    ? new Vector4(g.TrackX, g.ThumbStart, g.TrackW, g.ThumbLength)
                    : new Vector4(g.ThumbStart, g.TrackY, g.ThumbLength, g.TrackH);
                q.Color = new Riateu.Graphics.Color(color).ToVector4();
                q.BorderColor = default;
                q.BorderWidth = 0;
                q.Radius = new Vector4(Scrollbar.Thickness * 0.5f);
                q.UV = default;
                q.Mode = UIQuad.ModeRect;
            }
        }

        if (focus.Visible && focus.FocusedId != 0 && n.StableId == focus.FocusedId)
        {
            const float gap = 2, width = 2;
            Rectangle clip = ClipScissor(n.ClipX0 - gap - width, n.ClipY0 - gap - width, n.ClipX1 + gap + width, n.ClipY1 + gap + width);
            if (clip.Width <= 0 || clip.Height <= 0) return;
            float e = gap + width;
            ref UIQuad q = ref Push(clip, null);
            q.Rect = new Vector4(n.X - e, n.Y - e, n.W + e * 2, n.H + e * 2);
            q.Color = Vector4.Zero;
            q.BorderColor = new Riateu.Graphics.Color(theme.FocusRing).ToVector4();
            q.BorderWidth = width;
            Vector4 r = ClampRadius(s.Radius, n.W, n.H);
            q.Radius = new Vector4(r.X + e, r.Y + e, r.Z + e, r.W + e);
            q.UV = default;
            q.Mode = UIQuad.ModeRect;
        }
    }

    /// <summary>
    /// The box shadow: one quad covering the blurred shape (the box, offset and spread, plus 3 sigma
    /// of blur on each side). The shader draws a Gaussian falloff and leaves out the box itself.
    /// </summary>
    private void PushShadow(ref NodeData n, ref Style s, Rectangle scissor)
    {
        float sigma = s.ShadowBlur * 0.5f; // CSS: the blur radius is twice the standard deviation
        float grow = s.ShadowSpread + sigma * 3;
        float w = n.W + grow * 2, h = n.H + grow * 2;
        if (w <= 0 || h <= 0) return;
        ref UIQuad q = ref Push(scissor, null);
        q.Rect = new Vector4(n.X + s.ShadowX - grow, n.Y + s.ShadowY - grow, w, h);
        q.Color = s.ShadowColor.ToVector4();
        q.BorderColor = default;
        q.BorderWidth = sigma;
        q.Radius = ClampRadius(s.Radius, n.W, n.H);
        q.UV = default;
        q.Mode = UIQuad.ModeShadow;
        q.Gradient = new Vector4(s.ShadowX, s.ShadowY, s.ShadowSpread, 0);
    }

    /// <summary>Defer the overlays under a culled node; nothing else in its subtree is visible.</summary>
    private void DeferOverlays(int index)
    {
        for (int c = ui.Nodes[index].FirstChild; c >= 0; c = ui.Nodes[c].NextSibling)
        {
            ref Style cs = ref ui.Styles[c];
            if (cs.Display == Display.None) continue;
            if (cs.IsOverlay) PaintNode(c);
            else DeferOverlays(c);
        }
    }

    private void PaintNodeContent(int index, ref NodeData n, ref Style s)
    {
        // Cull nodes entirely outside their clip. Overlays inside them (a modal under an empty
        // or off-screen parent) are positioned on their own, so they are still deferred.
        if (n.X >= n.ClipX1 || n.Y >= n.ClipY1 || n.X + n.W <= n.ClipX0 || n.Y + n.H <= n.ClipY0)
        {
            DeferOverlays(index);
            return;
        }

        Rectangle scissor = ClipScissor(n.ClipX0, n.ClipY0, n.ClipX1, n.ClipY1);
        if (scissor.Width <= 0 || scissor.Height <= 0)
        {
            DeferOverlays(index);
            return;
        }

        if (s.HasShadow) PushShadow(ref n, ref s, scissor);

        bool hasGradient = s.Has(StyleFlags.Gradient) && s.Gradient != GradientKind.None;
        bool hasBg = s.Background.A > 0 || hasGradient;
        bool hasBorder = s.BorderWidth > 0 && s.BorderColor.A > 0;
        if (hasBg || hasBorder)
        {
            // Snap edges to whole pixels so boxes are crisp and nested boxes stay evenly inset
            // (not under scale/rotation, where the edges aren't pixel-aligned anyway).
            float x0 = n.X, y0 = n.Y, x1 = n.X + n.W, y1 = n.Y + n.H;
            if (TranslationOnly)
            {
                float tx = transform.M31, ty = transform.M32;
                x0 = SnapX(x0 + tx) - tx; y0 = SnapY(y0 + ty) - ty;
                x1 = SnapX(x1 + tx) - tx; y1 = SnapY(y1 + ty) - ty;
            }
            float maxR = MathF.Min(x1 - x0, y1 - y0) * 0.5f;
            ref UIQuad q = ref Push(scissor, null);
            q.Rect = new Vector4(x0, y0, x1 - x0, y1 - y0);
            q.Color = s.Background.A > 0 ? s.Background.ToVector4() : Vector4.Zero;
            if (hasGradient) SetGradient(ref q, ref s);
            q.BorderColor = hasBorder ? s.BorderColor.ToVector4() : Vector4.Zero;
            q.BorderWidth = hasBorder ? s.BorderWidth : 0;
            q.Radius = new Vector4(
                MathF.Min(s.Radius.TopLeft, maxR), MathF.Min(s.Radius.TopRight, maxR),
                MathF.Min(s.Radius.BottomRight, maxR), MathF.Min(s.Radius.BottomLeft, maxR));
            q.UV = default;
            q.Mode = UIQuad.ModeRect;
        }

        if (n.Kind == NodeKind.Text)
        {
            PaintCachedText(ref n, ref s, scissor);
            return;
        }
        if (n.Kind == NodeKind.Icon)
        {
            PaintIcon(ref n, ref s, scissor);
            return;
        }
        if (n.Kind == NodeKind.Image)
        {
            PaintImage(ref n, ref s, scissor);
            return;
        }
        if (n.Kind == NodeKind.TextInput)
        {
            PaintTextInput(ref n, ref s);
            return;
        }

        for (int c = n.FirstChild; c >= 0; c = ui.Nodes[c].NextSibling)
        {
            PaintNode(c);
        }
    }

    private void PaintText(ref NodeData n, ref Style s, Rectangle scissor)
    {
        ReadOnlySpan<char> text = ui.GetText(n);
        if (text.IsEmpty || s.TextColor.A == 0) return;

        UIFont font = fonts[s.FontId];
        float scale = s.TextSize > 0 ? s.TextSize / font.Size : 1f;
        Vector4 color = s.TextColor.ToVector4();

        float originX = n.X + s.PaddingEdges.Left + s.BorderWidth;
        float originY = n.Y + s.PaddingEdges.Top + s.BorderWidth;

        float contentW = n.W - s.PaddingEdges.Horizontal - s.BorderWidth * 2;
        float wrapWidth = s.TextWrapping == TextWrapMode.Wrap ? MathF.Max(0, contentW) / scale : float.PositiveInfinity;

        // Skip lines entirely outside the clip (long wrapped text in scroll views).
        float clipTop = n.ClipY0, clipBottom = n.ClipY1;
        float lineHeight = font.LineHeight;

        var breaker = new LineBreaker(font, text, wrapWidth);
        float penY = 0;
        float alignFactor = s.TextAlignment switch { TextAlign.Center => 0.5f, TextAlign.Right => 1f, _ => 0f };
        while (breaker.Next(out int start, out int length, out float lineWidth))
        {
            float lineTop = originY + penY * scale;
            if (lineTop > clipBottom) break;
            if (lineTop + lineHeight * scale >= clipTop)
            {
                float x = originX + MathF.Max(0, contentW - lineWidth * scale) * alignFactor;
                PaintLine(font, breaker.Shaped, start, length, x, originY, penY, scale, color, scissor);
            }
            penY += lineHeight;
        }
    }

    /// <summary>Text quads placed by the last paint, reused this frame (for diagnostics).</summary>
    public int ReusedTextQuads { get; private set; }

    /// <summary>
    /// A text element: its glyph quads from the previous frame when nothing they depend on changed
    /// (same shaped text, style, box, clip, transform, display scale and glyph atlas), else placed
    /// again with <see cref="PaintText"/>.
    /// </summary>
    private void PaintCachedText(ref NodeData n, ref Style s, Rectangle scissor)
    {
        ReadOnlySpan<char> text = ui.GetText(n);
        if (text.IsEmpty || s.TextColor.A == 0) return;
        ShapedText shaped = fonts[s.FontId].Shape(text);

        var key = new TextQuads
        {
            Shaped = shaped,
            X = n.X, Y = n.Y, W = n.W, H = n.H, ClipY0 = n.ClipY0, ClipY1 = n.ClipY1,
            Scissor = scissor, Transform = transform, ScaleX = scissorScaleX, ScaleY = scissorScaleY,
            GlyphEpoch = glyphs.Epoch, TextColor = s.TextColor, FontId = s.FontId, TextSize = s.TextSize,
            BorderWidth = s.BorderWidth, Padding = s.PaddingEdges, Wrapping = s.TextWrapping, Align = s.TextAlignment,
        };

        ref TextQuads cached = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(textCache, n.StableId, out bool exists);
        if (exists && cached.Frame == paintFrame - 1 && SameText(cached, key))
        {
            // Copy last frame's quads (opacity is applied after painting, so restore the color).
            Vector4 color = s.TextColor.ToVector4();
            int start = quadCount;
            for (int i = 0; i < cached.Count; i++)
            {
                Push(scissor, null);
                ref UIQuad q = ref quads[quadCount - 1];
                q = previousQuads[cached.Start + i];
                q.Color = color;
            }
            ReusedTextQuads += cached.Count;
            cached.Frame = paintFrame;
            cached.Start = start;
            return;
        }

        int first = quadCount;
        PaintText(ref n, ref s, scissor);
        key.Frame = paintFrame;
        key.Start = first;
        key.Count = quadCount - first;
        cached = key;
    }

    private static bool SameText(in TextQuads a, in TextQuads b) =>
        ReferenceEquals(a.Shaped, b.Shaped) && a.X == b.X && a.Y == b.Y && a.W == b.W && a.H == b.H &&
        a.ClipY0 == b.ClipY0 && a.ClipY1 == b.ClipY1 && a.Scissor.Equals(b.Scissor) && a.Transform == b.Transform &&
        a.ScaleX == b.ScaleX && a.ScaleY == b.ScaleY && a.GlyphEpoch == b.GlyphEpoch && a.TextColor == b.TextColor &&
        a.FontId == b.FontId && a.TextSize == b.TextSize && a.BorderWidth == b.BorderWidth &&
        a.Padding.Left == b.Padding.Left && a.Padding.Top == b.Padding.Top &&
        a.Padding.Right == b.Padding.Right && a.Padding.Bottom == b.Padding.Bottom &&
        a.Wrapping == b.Wrapping && a.Align == b.Align;

    /// <summary>Forget text elements that weren't painted recently.</summary>
    private void PruneTextCache()
    {
        foreach (var pair in textCache)
            if (pair.Value.Frame < paintFrame - 2) textCache.Remove(pair.Key);
    }

    /// <summary>
    /// One line of shaped text: the glyphs of characters [<paramref name="start"/>, +<paramref name="length"/>).
    /// Pen positions come from the shaper at the font's base size (scaled); the bitmaps are
    /// rasterized at the size the line ends up on screen (text size × display scale × transform
    /// scale) and drawn 1:1, so text is sharp at any size.
    /// </summary>
    private void PaintLine(UIFont font, ShapedText shaped, int start, int length, float originX, float originY, float penY,
        float scale, Vector4 color, Rectangle scissor)
    {
        if (shaped == null || length <= 0) return;
        // Screen pixels per layout unit, and the pixel size glyphs are rasterized at.
        float density = scissorScaleY * transformScale;
        float pixelSize = GlyphAtlas.Quantize(font.Size * scale * density);
        float toLayout = 1f / density;
        bool snap = TranslationOnly;
        float tx = transform.M31, ty = transform.M32;
        // Baseline-relative offsets are measured at pixelSize; pen positions at the layout scale.
        float rasterScale = font.Size * scale / (pixelSize * toLayout);

        int end = start + length;
        float penX = 0;
        ShapedGlyph[] all = shaped.Glyphs;
        for (int i = shaped.FirstGlyphAt(start); i < shaped.GlyphCount; i++)
        {
            ref readonly ShapedGlyph g = ref all[i];
            if (g.Cluster < start || g.Cluster >= end)
            {
                if (shaped.Monotonic && g.Cluster >= end) break; // past the line
                continue;
            }
            if (g.GlyphId >= 0)
            {
                ref readonly RasterGlyph r = ref glyphs.Get(font, g.GlyphId, pixelSize);
                if (r.Visible)
                {
                    float x = originX + (penX + g.OffsetX) * scale + r.OffsetX * toLayout * rasterScale;
                    float y = originY + (penY + g.OffsetY) * scale + r.OffsetY * toLayout * rasterScale;
                    if (snap) { x = SnapX(x + tx) - tx; y = SnapY(y + ty) - ty; }
                    ref UIQuad q = ref Push(scissor, null);
                    q.Rect = new Vector4(x, y, r.Width * toLayout, r.Height * toLayout);
                    q.UV = r.UV;
                    q.Color = color;
                    q.BorderColor = default;
                    q.Radius = default;
                    q.BorderWidth = 0;
                    q.Mode = UIQuad.ModeGlyph;
                }
            }
            penX += g.Advance;
        }
    }

    private void PaintTextInput(ref NodeData n, ref Style s)
    {
        if (n.Multiline) { PaintTextArea(ref n, ref s); return; }
        UIFont font = fonts[s.FontId];
        float scale = s.TextSize > 0 ? s.TextSize / font.Size : 1f;
        float cx = n.X + s.PaddingEdges.Left + s.BorderWidth;
        float cy = n.Y + s.PaddingEdges.Top + s.BorderWidth;
        float cw = MathF.Max(0, n.W - s.PaddingEdges.Horizontal - s.BorderWidth * 2);
        float lineH = font.LineHeight * scale;

        // Clip text to the content box (1px slack for the caret at the edges).
        Rectangle inner = ClipScissor(
            MathF.Max(n.ClipX0, cx - 1), MathF.Max(n.ClipY0, n.Y),
            MathF.Min(n.ClipX1, cx + cw + 1), MathF.Min(n.ClipY1, n.Y + n.H));
        if (inner.Width <= 0 || inner.Height <= 0) return;

        TextBuffer buffer = n.Buffer;
        ReadOnlySpan<char> text = buffer != null ? buffer.Text : default;
        bool focused = buffer != null && focus.FocusedId != 0 && focus.FocusedId == n.StableId;

        float scroll = 0;
        float caretX = 0;
        ShapedText shaped = text.IsEmpty ? null : font.Shape(text);
        if (focused)
        {
            caretX = (shaped?.Width(0, buffer.Caret) ?? 0) * scale;
            float total = (shaped?.Width(0, text.Length) ?? 0) * scale;
            scroll = focus.ScrollX;
            if (caretX - scroll > cw - 1) scroll = caretX - cw + 1; // keep the caret in view
            if (caretX - scroll < 0) scroll = caretX;
            scroll = Math.Clamp(scroll, 0, MathF.Max(0, total - cw + 1));
            focus.ScrollX = scroll;

            if (buffer.HasSelection)
            {
                float x0 = shaped.Width(0, buffer.SelectionStart) * scale;
                float x1 = shaped.Width(0, buffer.SelectionEnd) * scale;
                PushRect(inner, cx + x0 - scroll, cy, x1 - x0, lineH, theme.Selection);
            }
        }

        float originX = cx - scroll, originY = cy;

        if (text.IsEmpty)
        {
            ReadOnlySpan<char> placeholder = ui.GetText(n);
            if (!placeholder.IsEmpty)
                PaintLine(font, font.Shape(placeholder), 0, placeholder.Length, originX, originY, 0, scale, new Riateu.Graphics.Color(theme.Placeholder).ToVector4(), inner);
        }
        else
        {
            PaintLine(font, shaped, 0, text.Length, originX, originY, 0, scale, s.TextColor.ToVector4(), inner);
        }

        if (focused && focus.CaretVisible)
        {
            float caret = cx + caretX - scroll;
            if (TranslationOnly) caret = SnapX(caret + transform.M31) - transform.M31;
            PushRect(inner, caret, cy, 1.5f, lineH, theme.Caret);
        }
    }

    /// <summary>
    /// A multi-line input: wrapped lines from <see cref="TextAreaLayout"/>, a selection band per
    /// line, and vertical scroll (stored per element) that follows the caret while focused.
    /// </summary>
    private void PaintTextArea(ref NodeData n, ref Style s)
    {
        UIFont font = fonts[s.FontId];
        float scale = s.TextSize > 0 ? s.TextSize / font.Size : 1f;
        float cx = n.X + s.PaddingEdges.Left + s.BorderWidth;
        float cy = n.Y + s.PaddingEdges.Top + s.BorderWidth;
        float cw = MathF.Max(0, n.W - s.PaddingEdges.Horizontal - s.BorderWidth * 2);
        float chh = MathF.Max(0, n.H - s.PaddingEdges.Vertical - s.BorderWidth * 2);
        float lineH = font.LineHeight * scale;
        float wrap = MathF.Max(1, cw - 2) / scale; // 2px room for the caret at the right edge

        Rectangle inner = ClipScissor(
            MathF.Max(n.ClipX0, cx - 1), MathF.Max(n.ClipY0, cy),
            MathF.Min(n.ClipX1, cx + cw + 1), MathF.Min(n.ClipY1, cy + chh));
        if (inner.Width <= 0 || inner.Height <= 0) return;

        TextBuffer buffer = n.Buffer;
        ReadOnlySpan<char> text = buffer != null ? buffer.Text : default;
        bool focused = buffer != null && focus.FocusedId != 0 && focus.FocusedId == n.StableId;

        int lines = TextAreaLayout.LineCount(font, text, wrap);
        n.ScrollMax = MathF.Max(0, lines * lineH - chh);
        float scroll = Math.Clamp(getScroll(n.StableId), 0, n.ScrollMax);
        int caretLine = 0;
        float caretX = 0;
        if (focused)
        {
            TextAreaLayout.Locate(font, text, wrap, buffer.Caret, out caretLine, out float x, out _, out _);
            caretX = x * scale;
            float top = caretLine * lineH;
            if (top < scroll) scroll = top;
            if (top + lineH > scroll + chh) scroll = top + lineH - chh;
            scroll = Math.Clamp(scroll, 0, n.ScrollMax);
        }
        setScroll(n.StableId, scroll);

        Vector4 color = s.TextColor.ToVector4();
        if (text.IsEmpty)
        {
            ReadOnlySpan<char> placeholder = ui.GetText(n);
            if (!placeholder.IsEmpty)
                PaintLine(font, font.Shape(placeholder), 0, placeholder.Length, cx, cy, 0, scale, new Riateu.Graphics.Color(theme.Placeholder).ToVector4(), inner);
        }
        else
        {
            int selStart = focused && buffer.HasSelection ? buffer.SelectionStart : -1;
            int selEnd = focused && buffer.HasSelection ? buffer.SelectionEnd : -1;
            float extra = font.SpaceAdvance * scale; // selected line breaks show as a space
            var breaker = new LineBreaker(font, text, wrap);
            int line = 0;
            while (breaker.Next(out int start, out int length, out _))
            {
                float top = cy + line * lineH - scroll;
                line++;
                if (top + lineH < cy) continue;
                if (top > cy + chh) break;
                if (selStart >= 0 && selEnd > start && selStart <= start + length)
                {
                    int a = Math.Max(selStart, start), b = Math.Min(selEnd, start + length);
                    float x0 = breaker.Shaped.Width(start, a) * scale;
                    float x1 = breaker.Shaped.Width(start, b) * scale;
                    if (selEnd > start + length) x1 += extra;
                    PushRect(inner, cx + x0, top, MathF.Max(0, x1 - x0), lineH, theme.Selection);
                }
                PaintLine(font, breaker.Shaped, start, length, cx, top, 0, scale, color, inner);
            }
        }

        if (focused && focus.CaretVisible)
        {
            float x = cx + caretX;
            if (TranslationOnly) x = SnapX(x + transform.M31) - transform.M31;
            PushRect(inner, x, cy + caretLine * lineH - scroll, 1.5f, lineH, theme.Caret);
        }
    }

    private void PushRect(Rectangle scissor, float x, float y, float w, float h, uint rgba)
    {
        ref UIQuad q = ref Push(scissor, null);
        q.Rect = new Vector4(x, y, w, h);
        q.Color = new Riateu.Graphics.Color(rgba).ToVector4();
        q.BorderColor = default;
        q.Radius = default;
        q.BorderWidth = 0;
        q.UV = default;
        q.Mode = UIQuad.ModeRect;
    }

    private void PaintIcon(ref NodeData n, ref Style s, Rectangle scissor)
    {
        SvgIcon icon = n.Icon;
        if (icon == null) return;
        float x = n.X + s.PaddingEdges.Left + s.BorderWidth;
        float y = n.Y + s.PaddingEdges.Top + s.BorderWidth;
        float w = n.W - s.PaddingEdges.Horizontal - s.BorderWidth * 2;
        float h = n.H - s.PaddingEdges.Vertical - s.BorderWidth * 2;
        PushIcon(icon, x, y, w, h, s.TextColor, default, default, scissor);
    }

    /// <summary>
    /// An SVG drawn into (x, y, w, h). It's rasterized at the size it covers on screen (display
    /// scale × transform scale), so it's crisp at any size. <paramref name="crop"/> (u0, v0, u1, v1
    /// in 0..1) shows part of it, for <see cref="ImageFit.Cover"/>.
    /// </summary>
    private void PushIcon(SvgIcon icon, float x, float y, float w, float h, Color textColor, Corners radius, Vector4 crop, Rectangle scissor)
    {
        bool cropped = crop != default;
        float fullW = cropped ? w / (crop.Z - crop.X) : w, fullH = cropped ? h / (crop.W - crop.Y) : h;
        float sx = scissorScaleX * transformScale, sy = scissorScaleY * transformScale;
        int pw = (int)MathF.Round(fullW * sx), ph = (int)MathF.Round(fullH * sy);
        if (pw <= 0 || ph <= 0 || !icons.TryGet(icon, pw, ph, out Vector4 uv)) return;
        if (cropped)
        {
            float du = uv.Z - uv.X, dv = uv.W - uv.Y;
            uv = new Vector4(uv.X + du * crop.X, uv.Y + dv * crop.Y, uv.X + du * crop.Z, uv.Y + dv * crop.W);
        }
        else
        {
            // Draw at the rasterized pixel size (1:1), snapped when that's possible.
            w = pw / sx; h = ph / sy;
            if (TranslationOnly) { x = SnapX(x + transform.M31) - transform.M31; y = SnapY(y + transform.M32) - transform.M32; }
        }

        ref UIQuad q = ref Push(scissor, null); // icon atlas is slot 1's default
        MarkSlot1(null);
        q.Rect = new Vector4(x, y, w, h);
        q.UV = uv;
        q.Color = icon.UsesCurrentColor ? textColor.ToVector4() : Vector4.One;
        q.BorderColor = default;
        q.Radius = ClampRadius(radius, w, h);
        q.BorderWidth = 0;
        q.Mode = UIQuad.ModeImage;
    }

    private void PaintImage(ref NodeData n, ref Style s, Rectangle scissor)
    {
        float x = n.X + s.PaddingEdges.Left + s.BorderWidth;
        float y = n.Y + s.PaddingEdges.Top + s.BorderWidth;
        float w = n.W - s.PaddingEdges.Horizontal - s.BorderWidth * 2;
        float h = n.H - s.PaddingEdges.Vertical - s.BorderWidth * 2;
        if (w <= 0 || h <= 0) return;

        float natW, natH;
        if (n.Icon != null) { natW = n.Icon.ViewBox.Z; natH = n.Icon.ViewBox.W; }
        else if (n.Image != null && n.Image.IsValid) { natW = n.Image.Width; natH = n.Image.Height; }
        else if (n.Texture != null && !n.Texture.IsDisposed) { natW = n.Texture.Width; natH = n.Texture.Height; }
        else return;

        // object-fit: the drawn rect and the visible part of the image.
        Vector4 crop = default;
        if (n.Fit == ImageFit.Contain)
        {
            float k = MathF.Min(w / natW, h / natH);
            float dw = natW * k, dh = natH * k;
            x += (w - dw) * 0.5f; y += (h - dh) * 0.5f;
            w = dw; h = dh;
        }
        else if (n.Fit == ImageFit.Cover)
        {
            float k = MathF.Max(w / natW, h / natH);
            float u = w / (natW * k), v = h / (natH * k); // visible fraction
            crop = new Vector4((1 - u) * 0.5f, (1 - v) * 0.5f, (1 + u) * 0.5f, (1 + v) * 0.5f);
        }

        // Rounded corners clip the image (inside the border).
        Corners radius = s.Radius;
        if (s.BorderWidth > 0)
            radius = new Corners(MathF.Max(0, radius.TopLeft - s.BorderWidth), MathF.Max(0, radius.TopRight - s.BorderWidth),
                MathF.Max(0, radius.BottomRight - s.BorderWidth), MathF.Max(0, radius.BottomLeft - s.BorderWidth));

        if (n.Icon != null)
        {
            PushIcon(n.Icon, x, y, w, h, s.TextColor, radius, crop, scissor);
            return;
        }

        Texture texture = n.Texture ?? images.Get(n.Image);
        if (texture == null) return;
        ref UIQuad q = ref Push(scissor, texture, n.Sampling == ImageSampling.Nearest, live: n.Texture != null);
        q.Rect = new Vector4(x, y, w, h);
        q.UV = crop == default ? new Vector4(0, 0, 1, 1) : crop;
        q.Color = Vector4.One;
        q.BorderColor = default;
        q.Radius = ClampRadius(radius, w, h);
        q.BorderWidth = 0;
        q.Mode = UIQuad.ModeImage;
    }

    /// <summary>Gradient colors and direction; a missing end is the other end, transparent.</summary>
    private static void SetGradient(ref UIQuad q, ref Style s)
    {
        bool from = (s.GradientMask & 2) != 0, to = (s.GradientMask & 8) != 0, via = (s.GradientMask & 4) != 0;
        Vector4 a = from ? s.GradientFrom.ToVector4() : Vector4.Zero;
        Vector4 b = to ? s.GradientTo.ToVector4() : Vector4.Zero;
        if (!from && to) a = new Vector4(b.X, b.Y, b.Z, 0);
        if (!to && from) b = new Vector4(a.X, a.Y, a.Z, 0);
        q.Color = a;
        q.Color2 = b;
        q.Color3 = via ? s.GradientVia.ToVector4() : Vector4.Zero;
        float rad = s.GradientAngle * (MathF.PI / 180f);
        // CSS angles: 0deg points up; y grows down in layout space.
        q.Gradient = new Vector4(MathF.Sin(rad), -MathF.Cos(rad), s.Gradient == GradientKind.Radial ? 2 : 1, via ? 1 : 0);
    }

    private static Vector4 ClampRadius(Corners r, float w, float h)
    {
        float max = MathF.Min(w, h) * 0.5f;
        return new Vector4(MathF.Min(r.TopLeft, max), MathF.Min(r.TopRight, max), MathF.Min(r.BottomRight, max), MathF.Min(r.BottomLeft, max));
    }

    private float SnapX(float v) => MathF.Round(v * scissorScaleX) / scissorScaleX;
    private float SnapY(float v) => MathF.Round(v * scissorScaleY) / scissorScaleY;

    /// <summary>True if the icon atlas was cleared during the last paint (repaint needed).</summary>
    public bool TakeAtlasReset()
    {
        bool reset = icons.WasReset || glyphs.WasReset;
        icons.WasReset = false;
        glyphs.WasReset = false;
        return reset;
    }

    /// <summary>
    /// Reserve the next quad, starting a new draw call if the scissor changes or a raster
    /// <paramref name="image"/> needs slot 1 while the current call already uses it for
    /// something else. Rects, glyphs and icons pass null. The quad gets the current transform.
    /// </summary>
    private ref UIQuad Push(Rectangle scissor, Texture image, bool nearest = false, bool live = false)
    {
        bool newCall = callCount == 0;
        if (!newCall)
        {
            ref DrawCall last = ref calls[callCount - 1];
            if (last.Effect != EffectKind.None || !last.Scissor.Equals(scissor)) newCall = true;
            else if (live || last.Live) newCall = true; // live textures get calls of their own
            else if (image != null && last.SlotBound && (last.Image != image || last.Nearest != nearest)) newCall = true;
        }

        if (newCall)
        {
            if (callCount == calls.Length) Array.Resize(ref calls, callCount * 2);
            calls[callCount++] = new DrawCall { Scissor = scissor, Offset = (uint)quadCount, Live = live };
        }

        ref DrawCall call = ref calls[callCount - 1];
        call.Count++;
        if (image != null) { call.Image = image; call.SlotBound = true; call.Nearest = nearest; }
        return ref NextQuad();
    }

    /// <summary>Append a quad with the current transform (the caller counts it in its call).</summary>
    private ref UIQuad NextQuad()
    {
        if (quadCount == quads.Length) Array.Resize(ref quads, quadCount * 2);
        ref UIQuad q = ref quads[quadCount++];
        q.Gradient = default; // slots are reused: only quads that set a gradient have one
        q.Matrix = new Vector4(transform.M11, transform.M12, transform.M21, transform.M22);
        q.Translate = new Vector2(transform.M31, transform.M32);
        return ref q;
    }

    /// <summary>
    /// Icons need slot 1 to be the icon atlas: if the current call already bound a raster image,
    /// move the quad just pushed into a new call.
    /// </summary>
    private void MarkSlot1(Texture image)
    {
        ref DrawCall call = ref calls[callCount - 1];
        if (!call.SlotBound) { call.SlotBound = true; call.Image = image; return; }
        if (call.Image == image && !call.Nearest) return;
        call.Count--;
        if (callCount == calls.Length) Array.Resize(ref calls, callCount * 2);
        calls[callCount++] = new DrawCall { Scissor = call.Scissor, Offset = (uint)(quadCount - 1), Count = 1, Image = image, SlotBound = true };
    }

    /// <summary>Scissor for a clip rect in layout space. Under a transform it's the screen-space
    /// bounding box of the transformed rect, limited by the clip the transform started under.</summary>
    private Rectangle ClipScissor(float x0, float y0, float x1, float y1)
    {
        if (!transformed) return ToScissor(x0, y0, x1, y1);
        Vector2 a = Vector2.Transform(new Vector2(x0, y0), transform), b = Vector2.Transform(new Vector2(x1, y0), transform);
        Vector2 c = Vector2.Transform(new Vector2(x0, y1), transform), d = Vector2.Transform(new Vector2(x1, y1), transform);
        Vector2 min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)), max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
        Rectangle r = ToScissor(min.X, min.Y, max.X, max.Y);
        int rx0 = Math.Max(r.X, outerClip.X), ry0 = Math.Max(r.Y, outerClip.Y);
        int rx1 = Math.Min(r.X + r.Width, outerClip.X + outerClip.Width), ry1 = Math.Min(r.Y + r.Height, outerClip.Y + outerClip.Height);
        return new Rectangle(rx0, ry0, Math.Max(0, rx1 - rx0), Math.Max(0, ry1 - ry0));
    }

    private Rectangle ToScissor(float x0, float y0, float x1, float y1)
    {
        int ix0 = Math.Clamp((int)MathF.Floor(x0 * scissorScaleX), 0, targetW);
        int iy0 = Math.Clamp((int)MathF.Floor(y0 * scissorScaleY), 0, targetH);
        int ix1 = Math.Clamp((int)MathF.Ceiling(x1 * scissorScaleX), 0, targetW);
        int iy1 = Math.Clamp((int)MathF.Ceiling(y1 * scissorScaleY), 0, targetH);
        return new Rectangle(ix0, iy0, ix1 - ix0, iy1 - iy0);
    }

    // ------------------------------------------------------------------ submit (GPU)

    private void EnsureGpuCapacity(int count)
    {
        if (count <= gpuCapacity) return;
        uint cap = Math.Max(1024u, gpuCapacity);
        while (cap < count) cap *= 2;

        gpuBuffer?.Dispose();
        transfer?.Dispose();
        gpuBuffer = new StructuredBuffer<UIQuad>(device, BufferUsageFlags.GraphicsStorage, cap);
        transfer = TransferBuffer.Create<UIQuad>(device, TransferBufferUsage.Upload, cap);
        gpuCapacity = cap;
    }

    // ------------------------------------------------------------------ cached layers

    /// <summary>
    /// Keep the UI's pixels between rebuilds: the draw calls are rendered into layer textures only
    /// after a paint, and every frame just composites them (one draw call each). Draw calls of
    /// live textures (<see cref="DrawCall.Live"/>, e.g. a game screen) split the UI into layers and
    /// are drawn between them every frame, so they stay current and keep their place in the
    /// stacking order. Off: every frame replays all draw calls.
    /// </summary>
    public bool CacheLayers = true;

    /// <summary>More live textures than this and the layers would cost more than they save: draw directly.</summary>
    public int MaxLiveTextures = 4;

    private Texture[] layers = [];
    private bool[] layerUsed = [];
    private int layerCount;
    private uint layerW, layerH;
    private bool layersValid;

    // The quad that composites a layer: the whole window, sampling the layer 1:1.
    private StructuredBuffer<UIQuad> compositeBuffer;
    private TransferBuffer compositeTransfer;
    private float compositeW = -1, compositeH = -1;

    /// <summary>A paint that hasn't been uploaded and drawn yet.</summary>
    public bool HasPendingWork => drawPending;
    private bool drawPending;

    /// <summary>Draw calls issued for the last frame (composites and live textures when layers are cached).</summary>
    public int FrameDrawCalls { get; private set; }

    /// <summary>Upload (if a rebuild happened) and draw into <paramref name="target"/>.</summary>
    public void Render(CommandBuffer cmd, Texture target, float logicalW, float logicalH, LoadOp loadOp, Color clearColor)
    {
        icons.Upload(cmd);
        glyphs.Upload(cmd);
        images.Upload(cmd);
        if (needsUpload && quadCount > 0) Upload(cmd);
        needsUpload = false;
        drawPending = false;

        var uniforms = new UIUniforms { ViewProjection = Matrix4x4.CreateOrthographicOffCenter(0, logicalW, logicalH, 0, -1, 1) };
        if (effectCount > 0 && quadCount > 0)
        {
            ReleaseLayers();
            RenderEffects(cmd, target, uniforms, logicalW, logicalH, loadOp, clearColor);
            return;
        }
        ReleaseEffectTextures();

        int live = 0;
        for (int i = 0; i < callCount; i++) if (calls[i].Live && calls[i].Count > 0) live++;

        if (!CacheLayers || quadCount == 0 || live > MaxLiveTextures || target.Format != targetFormat)
        {
            RenderPass direct = cmd.BeginRenderPass(new ColorTargetInfo(target, clearColor, false, loadOp));
            FrameDrawCalls = quadCount > 0 ? DrawRange(cmd, direct, uniforms, 0, callCount) : 0;
            cmd.EndRenderPass(direct);
            ReleaseLayers();
            return;
        }

        EnsureLayers(live + 1, target.Width, target.Height);
        if (!layersValid) RenderLayers(cmd, uniforms);
        EnsureCompositeQuad(cmd, logicalW, logicalH);

        // Layer 0, live texture, layer 1, live texture, ... in the order they were painted.
        RenderPass pass = cmd.BeginRenderPass(new ColorTargetInfo(target, clearColor, false, loadOp));
        pass.BindGraphicsPipeline(pipeline);
        var full = new Rectangle(0, 0, (int)target.Width, (int)target.Height);
        int drawn = 0, layer = 0;
        for (int i = 0; i <= callCount; i++)
        {
            if (i < callCount && !(calls[i].Live && calls[i].Count > 0)) continue;
            // The static calls before this live one were rendered into this layer.
            if (layerUsed[layer])
            {
                pass.BindStorageVertexBuffer(compositeBuffer);
                pass.BindFragmentSamplers(new TextureSamplerBinding(glyphs.Texture, nearestSampler),
                    new TextureSamplerBinding(layers[layer], nearestSampler));
                pass.SetScissor(full);
                uniforms.QuadOffset = 0;
                cmd.PushVertexUniformData(uniforms, 0);
                pass.DrawPrimitives(4, 1, 0, 0);
                drawn++;
            }
            layer++;
            if (i < callCount)
            {
                pass.BindStorageVertexBuffer(gpuBuffer);
                drawn += DrawRange(cmd, pass, uniforms, i, i + 1);
            }
        }
        cmd.EndRenderPass(pass);
        FrameDrawCalls = drawn;
        Stats.Composites++;
    }

    /// <summary>Render the static calls between live ones into their layers (after a paint).</summary>
    private void RenderLayers(CommandBuffer cmd, UIUniforms uniforms)
    {
        int layer = 0, from = 0;
        for (int i = 0; i <= callCount; i++)
        {
            if (i < callCount && !(calls[i].Live && calls[i].Count > 0)) continue;
            bool any = false;
            for (int c = from; c < i; c++) if (calls[c].Count > 0) { any = true; break; }
            layerUsed[layer] = any;
            if (any)
            {
                // Transparent, then the calls with the same premultiplied blending as the window:
                // the layer holds premultiplied pixels, and drawing it over the window later gives
                // the same result as drawing the calls there.
                // Cycled: last frame may still be reading this layer, and it's cleared anyway.
                RenderPass pass = cmd.BeginRenderPass(new ColorTargetInfo(layers[layer], new Color(0, 0, 0, 0), true, LoadOp.Clear));
                DrawRange(cmd, pass, uniforms, from, i);
                cmd.EndRenderPass(pass);
                Stats.LayerRenders++;
            }
            layer++;
            from = i + 1;
        }
        layersValid = true;
    }

    // ------------------------------------------------------------------ blur effects

    // The frame is drawn into `scene` (the target can't be read back), blur groups into a
    // texture per nesting depth, and blurs go through two downscaled textures per size.
    private Texture scene;
    private readonly List<Texture> groupTargets = new();
    private readonly Dictionary<(uint W, uint H, int Pass), (Texture Texture, int Frame)> blurTextures = new();
    private int effectFrame;

    /// <summary>A frame with blurs: drawn every frame (no cached layers), then composited onto the target.</summary>
    private void RenderEffects(CommandBuffer cmd, Texture target, UIUniforms uniforms, float logicalW, float logicalH, LoadOp loadOp, Color clearColor)
    {
        effectFrame++;
        scene = EnsureTarget(scene, target.Width, target.Height);
        EnsureCompositeQuad(cmd, logicalW, logicalH);
        int drawn = Execute(cmd, scene, 0, callCount, 0, loadOp == LoadOp.Clear ? clearColor : new Color(0, 0, 0, 0), uniforms);

        RenderPass pass = cmd.BeginRenderPass(new ColorTargetInfo(target, clearColor, false, loadOp));
        pass.BindGraphicsPipeline(pipeline);
        pass.BindStorageVertexBuffer(compositeBuffer);
        pass.BindFragmentSamplers(new TextureSamplerBinding(glyphs.Texture, nearestSampler), new TextureSamplerBinding(scene, nearestSampler));
        pass.SetScissor(new Rectangle(0, 0, (int)target.Width, (int)target.Height));
        uniforms.QuadOffset = 0;
        cmd.PushVertexUniformData(uniforms, 0);
        pass.DrawPrimitives(4, 1, 0, 0);
        cmd.EndRenderPass(pass);
        FrameDrawCalls = drawn + 1;
        PruneBlurTextures();
    }

    /// <summary>Calls [from, to) into <paramref name="dest"/>, cleared first; effects end the pass to read what's there.</summary>
    private int Execute(CommandBuffer cmd, Texture dest, int from, int to, int depth, Color clear, UIUniforms uniforms)
    {
        RenderPass pass = cmd.BeginRenderPass(new ColorTargetInfo(dest, clear, true, LoadOp.Clear));
        int drawn = 0;
        for (int i = from; i < to; i++)
        {
            if (calls[i].Effect == EffectKind.None)
            {
                int end = i + 1;
                while (end < to && calls[end].Effect == EffectKind.None) end++;
                drawn += DrawRange(cmd, pass, uniforms, i, end);
                i = end - 1;
                continue;
            }

            cmd.EndRenderPass(pass);
            Texture blurred;
            int composite = i;
            if (calls[i].Effect == EffectKind.Backdrop)
            {
                blurred = Blur(cmd, dest, calls[i].Region, calls[i].Sigma);
            }
            else
            {
                composite = GroupEnd(i);
                if (depth == groupTargets.Count) groupTargets.Add(null);
                Texture group = groupTargets[depth] = EnsureTarget(groupTargets[depth], dest.Width, dest.Height);
                drawn += Execute(cmd, group, i + 1, composite, depth + 1, new Color(0, 0, 0, 0), uniforms);
                blurred = Blur(cmd, group, calls[i].Region, calls[i].Sigma);
                i = composite;
            }

            pass = cmd.BeginRenderPass(new ColorTargetInfo(dest, default, false, LoadOp.Load));
            ref DrawCall call = ref calls[composite];
            pass.BindGraphicsPipeline(pipeline);
            pass.BindStorageVertexBuffer(gpuBuffer);
            pass.BindFragmentSamplers(new TextureSamplerBinding(glyphs.Texture, sampler), new TextureSamplerBinding(blurred, sampler));
            pass.SetScissor(call.Scissor);
            uniforms.QuadOffset = call.Offset;
            cmd.PushVertexUniformData(uniforms, 0);
            pass.DrawPrimitives(4, 1, 0, 0);
            drawn++;
        }
        cmd.EndRenderPass(pass);
        return drawn;
    }

    private int GroupEnd(int begin)
    {
        int depth = 0;
        for (int i = begin + 1; i < callCount; i++)
        {
            if (calls[i].Effect == EffectKind.GroupBegin) depth++;
            else if (calls[i].Effect == EffectKind.GroupEnd && depth-- == 0) return i;
        }
        return callCount;
    }

    /// <summary>
    /// Separable Gaussian of <paramref name="region"/> of <paramref name="source"/>: horizontal into
    /// a texture narrowed by the downscale factor, then vertical into one shortened by it. Larger
    /// blurs downscale more, so the tap count stays small.
    /// </summary>
    private Texture Blur(CommandBuffer cmd, Texture source, Rectangle region, float sigma)
    {
        int scale = Math.Clamp((int)(sigma / 3), 1, 8);
        uint w = (uint)Math.Max(1, (region.Width + scale - 1) / scale);
        uint h = (uint)Math.Max(1, (region.Height + scale - 1) / scale);
        Texture horizontal = BlurTexture(w, (uint)region.Height, 0);
        Texture vertical = BlurTexture(w, h, 1);

        float step = MathF.Max(1, sigma / 4);
        float taps = MathF.Min(MathF.Ceiling(sigma * 3 / step), 32);
        float sw = source.Width, sh = source.Height;
        BlurPass(cmd, source, horizontal, new BlurUniforms
        {
            SourceRect = new Vector4(region.X / sw, region.Y / sh, region.Width / sw, region.Height / sh),
            Step = new Vector2(step / sw, 0),
            Sigma = sigma / step,
            Taps = taps,
        });
        BlurPass(cmd, horizontal, vertical, new BlurUniforms
        {
            SourceRect = new Vector4(0, 0, 1, 1),
            Step = new Vector2(0, step / region.Height),
            Sigma = sigma / step,
            Taps = taps,
        });
        return vertical;
    }

    private void BlurPass(CommandBuffer cmd, Texture source, Texture dest, in BlurUniforms uniforms)
    {
        RenderPass pass = cmd.BeginRenderPass(new ColorTargetInfo(dest, default, true, LoadOp.DontCare));
        pass.BindGraphicsPipeline(blurPipeline);
        pass.BindFragmentSampler(new TextureSamplerBinding(source, sampler));
        cmd.PushFragmentUniformData(uniforms, 0);
        pass.DrawPrimitives(3, 1, 0, 0);
        cmd.EndRenderPass(pass);
    }

    private Texture EnsureTarget(Texture texture, uint w, uint h)
    {
        if (texture != null && texture.Width == w && texture.Height == h) return texture;
        texture?.Dispose();
        return new Texture(device, w, h, targetFormat, TextureUsageFlags.ColorTarget | TextureUsageFlags.Sampler);
    }

    private Texture BlurTexture(uint w, uint h, int pass)
    {
        var key = (w, h, pass);
        if (!blurTextures.TryGetValue(key, out var entry))
            entry.Texture = new Texture(device, w, h, targetFormat, TextureUsageFlags.ColorTarget | TextureUsageFlags.Sampler);
        blurTextures[key] = (entry.Texture, effectFrame);
        return entry.Texture;
    }

    /// <summary>Drop blur textures of sizes that haven't been used for a while (resizes, animations).</summary>
    private void PruneBlurTextures()
    {
        if (blurTextures.Count <= 8) return;
        foreach (var (key, entry) in blurTextures)
        {
            if (entry.Frame >= effectFrame - 60) continue;
            entry.Texture.Dispose();
            blurTextures.Remove(key);
        }
    }

    private void ReleaseEffectTextures()
    {
        if (scene == null && blurTextures.Count == 0) return;
        scene?.Dispose();
        scene = null;
        foreach (Texture t in groupTargets) t?.Dispose();
        groupTargets.Clear();
        foreach (var entry in blurTextures.Values) entry.Texture.Dispose();
        blurTextures.Clear();
    }

    /// <summary>Draw calls [<paramref name="from"/>, <paramref name="to"/>) of the quad buffer; returns how many were issued.</summary>
    private int DrawRange(CommandBuffer cmd, RenderPass pass, UIUniforms uniforms, int from, int to)
    {
        pass.BindGraphicsPipeline(pipeline);
        pass.BindStorageVertexBuffer(gpuBuffer);
        Texture bound = null;
        bool boundNearest = false;
        int drawn = 0;
        for (int i = from; i < to; i++)
        {
            ref DrawCall call = ref calls[i];
            if (call.Count == 0) continue;
            // A user texture (game screen) may have been disposed since this was painted.
            if (call.Image != null && call.Image.IsDisposed) continue;
            Texture slot1 = call.Image ?? icons.Texture;
            if (slot1 != bound || call.Nearest != boundNearest)
            {
                pass.BindFragmentSamplers(new TextureSamplerBinding(glyphs.Texture, sampler),
                    new TextureSamplerBinding(slot1, call.Nearest ? nearestSampler : sampler));
                bound = slot1;
                boundNearest = call.Nearest;
            }
            pass.SetScissor(call.Scissor);
            uniforms.QuadOffset = call.Offset;
            cmd.PushVertexUniformData(uniforms, 0);
            pass.DrawPrimitives(4, call.Count, 0, 0);
            drawn++;
        }
        return drawn;
    }

    private void EnsureLayers(int count, uint w, uint h)
    {
        if (w != layerW || h != layerH) ReleaseLayers();
        if (count > layers.Length)
        {
            int old = layers.Length;
            Array.Resize(ref layers, count);
            Array.Resize(ref layerUsed, count);
            for (int i = old; i < count; i++)
                layers[i] = new Texture(device, w, h, targetFormat, TextureUsageFlags.ColorTarget | TextureUsageFlags.Sampler);
            layersValid = false;
        }
        if (count != layerCount) layersValid = false;
        layerCount = count;
        layerW = w;
        layerH = h;
    }

    private void ReleaseLayers()
    {
        foreach (Texture t in layers) t.Dispose();
        layers = [];
        layerUsed = [];
        layerCount = 0;
        layerW = layerH = 0;
        layersValid = false;
    }

    private void EnsureCompositeQuad(CommandBuffer cmd, float logicalW, float logicalH)
    {
        if (compositeBuffer != null && compositeW == logicalW && compositeH == logicalH) return;
        compositeBuffer ??= new StructuredBuffer<UIQuad>(device, BufferUsageFlags.GraphicsStorage, 1);
        compositeTransfer ??= TransferBuffer.Create<UIQuad>(device, TransferBufferUsage.Upload, 1);
        Span<UIQuad> mapped = compositeTransfer.Map<UIQuad>(true);
        mapped[0] = new UIQuad
        {
            Rect = new Vector4(0, 0, logicalW, logicalH),
            UV = new Vector4(0, 0, 1, 1),
            Color = Vector4.One,
            Mode = UIQuad.ModeImage,
            Matrix = new Vector4(1, 0, 0, 1),
        };
        compositeTransfer.Unmap();
        CopyPass copy = cmd.BeginCopyPass();
        copy.UploadToBuffer(new TransferBufferLocation(compositeTransfer), new BufferRegion(compositeBuffer, 0, UIQuad.SizeInBytes), true);
        cmd.EndCopyPass(copy);
        compositeW = logicalW;
        compositeH = logicalH;
    }

    /// <summary>
    /// Send the quads that differ from what the GPU buffer holds: from the first changed quad to
    /// the last (everything after an insertion shifts, so that's the rest). Nothing when the
    /// rebuild changed nothing visible.
    /// </summary>
    private void Upload(CommandBuffer cmd)
    {
        if (quadCount > gpuCapacity)
        {
            EnsureGpuCapacity(quadCount);
            gpuQuadCount = 0; // a new buffer: everything goes up
        }
        ReadOnlySpan<UIQuad> now = quads.AsSpan(0, quadCount);
        int shared = Math.Min(quadCount, gpuQuadCount);
        ReadOnlySpan<byte> a = System.Runtime.InteropServices.MemoryMarshal.AsBytes(now[..shared]);
        ReadOnlySpan<byte> b = System.Runtime.InteropServices.MemoryMarshal.AsBytes(gpuQuads.AsSpan(0, shared));
        int first = a.CommonPrefixLength(b) / UIQuad.SizeInBytes;
        int last = quadCount; // exclusive
        if (quadCount == gpuQuadCount)
        {
            // Same count: trailing quads that match can stay too.
            while (last > first && SameQuad(now[last - 1], gpuQuads[last - 1])) last--;
        }
        LastUploadQuads = last - first;
        Stats.Uploads++;
        Stats.QuadsUploaded += last - first;
        if (last > first)
        {
            int count = last - first;
            Span<UIQuad> mapped = transfer.Map<UIQuad>(true);
            now.Slice(first, count).CopyTo(mapped);
            transfer.Unmap();

            CopyPass copy = cmd.BeginCopyPass();
            // No cycling: the rest of the buffer must keep its contents.
            copy.UploadToBuffer(new TransferBufferLocation(transfer),
                new BufferRegion(gpuBuffer, (uint)(first * UIQuad.SizeInBytes), (uint)(count * UIQuad.SizeInBytes)), false);
            cmd.EndCopyPass(copy);
        }
        if (gpuQuads.Length < quadCount) Array.Resize(ref gpuQuads, quads.Length);
        now.Slice(first, last - first).CopyTo(gpuQuads.AsSpan(first));
        gpuQuadCount = quadCount;
    }

    private static bool SameQuad(in UIQuad a, in UIQuad b) =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<UIQuad>(in a))
            .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<UIQuad>(in b)));

    /// <summary>Running totals of paint and upload work (diagnostics).</summary>
    public RenderStats Stats;

    /// <summary>Quads sent to the GPU by the last upload (for diagnostics).</summary>
    public int LastUploadQuads { get; private set; }

    /// <summary>Number of draw calls in the current frame (for diagnostics).</summary>
    public int Batches => callCount;
    public int QuadCount => quadCount;

    public void Dispose()
    {
        ReleaseLayers();
        ReleaseEffectTextures();
        blurPipeline.Dispose();
        compositeBuffer?.Dispose();
        compositeTransfer?.Dispose();
        gpuBuffer?.Dispose();
        transfer?.Dispose();
        icons.Dispose();
        glyphs.Dispose();
        images.Dispose();
        pipeline.Dispose();
        sampler.Dispose();
        nearestSampler.Dispose();
    }
}

/// <summary>Running totals of the renderer's work since startup (see <see cref="UIContext.RenderStats"/>).</summary>
public struct RenderStats
{
    /// <summary>Rebuilds painted, and the quads they produced.</summary>
    public long Paints, QuadsPainted;
    /// <summary>Glyph quads copied from the previous frame instead of placed again.</summary>
    public long TextQuadsReused;
    /// <summary>Uploads to the GPU, and the quads they sent (unchanged quads aren't sent).</summary>
    public long Uploads, QuadsUploaded;
    /// <summary>Frames drawn from cached layers, and layers rendered (only after a rebuild).</summary>
    public long Composites, LayerRenders;
}
