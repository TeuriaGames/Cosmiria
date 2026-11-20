using System.Numerics;
using Riateu.Graphics;

namespace Cosmiria;

/// <summary>A glyph rasterized at one pixel size: its bitmap in the atlas and its placement.</summary>
internal struct RasterGlyph
{
    public Vector4 UV;
    /// <summary>Bitmap size in pixels at the rasterized size.</summary>
    public float Width, Height;
    /// <summary>Offset from the pen position to the bitmap's top-left, in pixels at the rasterized size.</summary>
    public float OffsetX, OffsetY;
    public bool Visible;
}

/// <summary>
/// Glyphs rasterized from each font's outlines at the exact pixel size they're drawn at (text
/// size × display scale × transform scale), so text is crisp at every size instead of scaling
/// one baked bitmap. Sizes are quantized to 1/4 px. Shared by all fonts; cleared when full.
/// </summary>
internal sealed class GlyphAtlas : IDisposable
{
    private const float Quantum = 4f; // sizes are rounded to 1/4 px

    private readonly ShelfAtlas atlas;
    private readonly Dictionary<(int font, int glyph, int size), RasterGlyph> entries = new();
    private Color[] scratch = new Color[64 * 64];

    public Texture Texture => atlas.Texture;
    public bool WasReset { get; set; }

    public GlyphAtlas(GraphicsDevice device) { atlas = new ShelfAtlas(device, 2048); }

    /// <summary>Pixel size used for <paramref name="size"/> (what <see cref="Get"/> rasterizes at).</summary>
    public static float Quantize(float size) => MathF.Max(1f, MathF.Round(size * Quantum) / Quantum);

    /// <summary>Glyph <paramref name="glyphId"/> (a glyph index, as the shaper picks it) at <paramref name="pixelSize"/>.</summary>
    public ref readonly RasterGlyph Get(UIFont font, int glyphId, float pixelSize)
    {
        int key = (int)MathF.Round(Quantize(pixelSize) * Quantum);
        ref RasterGlyph entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
            entries, (font.Id, glyphId, key), out bool exists);
        if (!exists) entry = Rasterize(font, glyphId, key / Quantum);
        return ref entry;
    }

    /// <summary>Bumped whenever the atlas is cleared: cached UVs from before are stale.</summary>
    public int Epoch { get; private set; }

    private RasterGlyph Rasterize(UIFont font, int glyphId, float size)
    {
        Font.Character ch = font.Font.Font.GetCharacterByGlyph(glyphId, size);
        var glyph = new RasterGlyph
        {
            Width = ch.Width,
            Height = ch.Height,
            OffsetX = ch.OffsetX,
            OffsetY = ch.OffsetY,
            Visible = ch.Visible && ch.Width > 0 && ch.Height > 0,
        };
        if (!glyph.Visible) return glyph;

        int w = Math.Min(ch.Width, atlas.MaxSide), h = Math.Min(ch.Height, atlas.MaxSide);
        if (!atlas.TryAdd(w, h, out byte[] pixels, out int stride, out glyph.UV))
        {
            // Full: start over; the next paint re-rasterizes what's on screen.
            entries.Clear();
            atlas.Clear();
            WasReset = true;
            Epoch++;
            if (!atlas.TryAdd(w, h, out pixels, out stride, out glyph.UV)) { glyph.Visible = false; return glyph; }
        }

        int count = ch.Width * ch.Height;
        if (scratch.Length < count) scratch = new Color[count];
        Span<Color> bitmap = scratch.AsSpan(0, count);
        bitmap.Clear();
        font.Font.Font.GetPixelsByCharacter(ch, bitmap);
        int origin = ShelfAtlas.Origin(stride);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color c = bitmap[y * ch.Width + x];
                int o = origin + y * stride + x * 4;
                pixels[o] = c.R; pixels[o + 1] = c.G; pixels[o + 2] = c.B; pixels[o + 3] = c.A;
            }
        }
        return glyph;
    }

    public void Upload(CommandBuffer cmd) => atlas.Upload(cmd);

    public void Dispose() => atlas.Dispose();
}
