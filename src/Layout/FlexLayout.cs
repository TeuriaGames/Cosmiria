using System.Runtime.CompilerServices;

namespace Cosmiria;

/// <summary>
/// Flexbox layout over the <see cref="UIStack"/> arena, with flex-wrap and wrapping text.
/// <list type="number">
/// <item><b>Measure</b> (bottom-up, width-constrained): a node's size given the width
/// available to it. Results are cached per available width, so re-measuring is cheap.</item>
/// <item><b>Arrange</b> (top-down): breaks children into flex lines, applies grow/shrink,
/// justify/align, re-measures width-dependent children (wrapping text) once their final width
/// is known, then positions lines, absolute children, scroll offsets and clip rects.</item>
/// </list>
/// Styles must already be resolved (inheritance + hover/active) before calling.
/// </summary>
internal sealed partial class FlexLayout
{
    private struct Line
    {
        public int Start, End;   // range into the flow scratch buffer
        public float Main;       // used main size after flexing (incl. gaps and margins)
        public float Cross;      // line thickness on the cross axis
    }

    private const float Epsilon = 0.01f;

    private UIStack ui;
    private FontRegistry fonts;
    private float rootW, rootH;
    private Func<int, float> getScroll;
    private Action<int, float> setScroll;

    // Reused scratch buffers; each Arrange frame releases its slice before recursing.
    private int[] flow = new int[128];
    private int flowTop;
    private Line[] lines = new Line[32];
    private int lineTop;

    public void Run(UIStack ui, FontRegistry fonts, int root, float width, float height,
        Func<int, float> getScroll, Action<int, float> setScroll)
    {
        this.ui = ui;
        this.fonts = fonts;
        this.getScroll = getScroll;
        this.setScroll = setScroll;
        flowTop = 0;
        lineTop = 0;
        rootW = width;
        rootH = height;

        Measure(root, width, width, exact: true);
        ref NodeData r = ref ui.Nodes[root];
        r.X = 0; r.Y = 0; r.W = width; r.H = height;
        r.ClipX0 = 0; r.ClipY0 = 0; r.ClipX1 = width; r.ClipY1 = height;
        Arrange(root);
    }

    // ================================================================== measure

    /// <param name="avail">Border-box width available to the node (NaN = unconstrained).</param>
    /// <param name="percentBase">Width that percentages resolve against (NaN = unknown).</param>
    /// <param name="exact">The node's width is already decided to be exactly <paramref name="avail"/>.</param>
    private void Measure(int index, float avail, float percentBase, bool exact = false)
    {
        ref NodeData n = ref ui.Nodes[index];
        float key = exact ? -avail - 1 : avail; // distinct cache key for exact widths
        if (n.MeasureValid && n.MeasuredAvail.Equals(key)) return;
        n.MeasureValid = true;
        n.MeasuredAvail = key;

        ref Style s = ref ui.Styles[index];
        if (s.Display == Display.None)
        {
            n.MeasuredW = n.MeasuredH = n.MinContentW = 0;
            n.WidthDependent = false;
            return;
        }

        float pw = s.PaddingEdges.Horizontal + s.BorderWidth * 2;
        float ph = s.PaddingEdges.Vertical + s.BorderWidth * 2;

        float styleW = s.Width.Resolve(percentBase, float.NaN);
        if (!float.IsNaN(styleW)) styleW = Clamp(styleW, s.MinWidth, s.MaxWidth, percentBase);
        float fixedW = exact ? avail : styleW;

        // Border-box width limit used to wrap content.
        float limit;
        if (!float.IsNaN(fixedW)) limit = fixedW;
        else
        {
            float maxW = s.MaxWidth.Resolve(percentBase, float.PositiveInfinity);
            limit = float.IsNaN(avail) ? maxW : MathF.Min(avail, maxW);
        }
        float inner = MathF.Max(0, limit - pw); // may be +inf

        float cw, ch, minContent, minH = float.NaN;
        bool dependent;

        if (n.Kind == NodeKind.Text)
        {
            UIFont font = fonts[s.FontId];
            float scale = s.TextSize > 0 ? s.TextSize / font.Size : 1f;
            ReadOnlySpan<char> text = ui.GetText(n);
            bool wrap = s.TextWrapping == TextWrapMode.Wrap;

            var (width, lineCount) = LineBreaker.Measure(font, text, wrap ? inner / scale : float.PositiveInfinity);
            cw = width * scale;
            ch = lineCount * font.LineHeight * scale;
            minContent = wrap ? LineBreaker.LongestWord(font, text) * scale : cw;
            dependent = wrap;
        }
        else if (n.Kind == NodeKind.Image)
        {
            // Natural size, or the aspect ratio applied to whichever side is known.
            float natW, natH;
            if (n.Icon != null) { natW = n.Icon.ViewBox.Z; natH = n.Icon.ViewBox.W; }
            else if (n.Image != null && n.Image.IsValid) { natW = n.Image.Width; natH = n.Image.Height; }
            else if (n.Texture != null) { natW = n.Texture.Width; natH = n.Texture.Height; }
            else { natW = natH = 0; }
            float aspect = natH > 0 ? natW / natH : 1f;
            float styleH = s.Height.Resolve(float.NaN, float.NaN);
            dependent = false;
            if (!float.IsNaN(fixedW))
            {
                cw = MathF.Max(0, fixedW - pw);
                ch = float.IsNaN(styleH) ? cw / aspect : MathF.Max(0, styleH - ph);
                dependent = float.IsNaN(styleH) && s.Width.Unit != LengthUnit.Px; // % or stretched width
            }
            else if (!float.IsNaN(styleH))
            {
                ch = MathF.Max(0, styleH - ph);
                cw = ch * aspect;
            }
            else
            {
                cw = natW;
                ch = natH;
                dependent = true; // a stretched width changes the height
            }
            minContent = 0; // images shrink freely, like text wrapping
        }
        else if (n.Kind == NodeKind.TextInput)
        {
            UIFont font = fonts[s.FontId];
            float scale = s.TextSize > 0 ? s.TextSize / font.Size : 1f;
            cw = 0;
            ch = font.LineHeight * scale * (n.Multiline ? Math.Max(1, n.Rows) : 1);
            minContent = 40;
            dependent = false;
        }
        else if (s.Display == Display.Grid)
        {
            MeasureGrid(ref n, ref s, inner, out cw, out ch, out minContent, out dependent, out minH);
        }
        else
        {
            MeasureContainer(ref n, ref s, inner, out cw, out ch, out minContent, out dependent, out minH);
        }
        if (float.IsNaN(minH)) minH = ch; // text, inputs, images: their content height

        float w = !float.IsNaN(fixedW) ? fixedW : cw + pw;
        float h = s.Height.Resolve(float.NaN, ch + ph);
        n.MeasuredW = exact ? avail : Clamp(w, s.MinWidth, s.MaxWidth, percentBase);
        n.MeasuredH = Clamp(h, s.MinHeight, s.MaxHeight, float.NaN);
        // Min-content is intrinsic: an exact (imposed) width must not change it, and neither does
        // a percentage width (as in CSS): it resolves against the parent, so counting it would let
        // a w-full child force its parent to the full available width.
        n.MinContentW = !float.IsNaN(styleW) && s.Width.Unit != LengthUnit.Percent
            ? styleW
            : Clamp(minContent + pw, s.MinWidth, s.MaxWidth, percentBase);
        if (!exact) n.MinContentW = MathF.Min(n.MinContentW, n.MeasuredW);
        // Like MinContentW for heights, except that scroll containers inside count as 0: they can
        // always shrink and scroll, so ancestors may shrink to make room without needing min-h-0.
        n.MinContentH = s.Height.Unit == LengthUnit.Px
            ? n.MeasuredH
            : MathF.Min(n.MeasuredH, Clamp(minH + ph, s.MinHeight, s.MaxHeight, float.NaN));
        n.WidthDependent = dependent;
    }

    private void MeasureContainer(ref NodeData n, ref Style s, float inner,
        out float cw, out float ch, out float minContent, out bool dependent, out float minH)
    {
        minH = 0;
        bool row = s.Direction == FlexDirection.Row;
        bool wrap = s.Wrapping == WrapMode.Wrap;
        float mainGap = row ? s.ColumnGap : s.RowGap;
        float crossGap = row ? s.RowGap : s.ColumnGap;
        bool finite = float.IsFinite(inner);

        cw = ch = minContent = 0;
        dependent = row && wrap;

        // Row lines (only row-wrap can break lines during measure; column-wrap needs a
        // known height, which only exists at arrange time).
        float lineMain = 0, lineCross = 0, widest = 0, totalCross = 0;
        int lineItems = 0, lineCount = 0, flowCount = 0;

        for (int c = n.FirstChild; c >= 0; c = ui.Nodes[c].NextSibling)
        {
            ref Style cs = ref ui.Styles[c];
            float mh = cs.MarginEdges.Horizontal;
            Measure(c, finite ? MathF.Max(0, inner - mh) : float.NaN, finite ? inner : float.NaN);

            ref NodeData cn = ref ui.Nodes[c];
            if (cs.Display == Display.None) continue;
            dependent |= cn.WidthDependent;
            if (cs.Position != PositionMode.Relative) continue;

            float ow = cn.MeasuredW + mh;
            float oh = cn.MeasuredH + cs.MarginEdges.Vertical;
            float omin = cn.MinContentW + mh;
            float ominH = ItemMinHeight(c) + cs.MarginEdges.Vertical;

            if (row)
            {
                if (wrap && finite && lineItems > 0 && lineMain + mainGap + ow > inner + Epsilon)
                {
                    widest = MathF.Max(widest, lineMain);
                    totalCross += (lineCount > 0 ? crossGap : 0) + lineCross;
                    lineCount++;
                    lineMain = lineCross = 0;
                    lineItems = 0;
                }
                lineMain += (lineItems > 0 ? mainGap : 0) + ow;
                lineCross = MathF.Max(lineCross, oh);
                if (!wrap) minH = MathF.Max(minH, ominH);
                lineItems++;
                minContent = wrap ? MathF.Max(minContent, omin) : minContent + (flowCount > 0 ? mainGap : 0) + omin;
            }
            else
            {
                ch += (flowCount > 0 ? mainGap : 0) + oh;
                minH += (flowCount > 0 ? mainGap : 0) + ominH;
                cw = MathF.Max(cw, ow);
                minContent = MathF.Max(minContent, omin);
            }
            flowCount++;
        }

        if (row)
        {
            if (lineItems > 0)
            {
                widest = MathF.Max(widest, lineMain);
                totalCross += (lineCount > 0 ? crossGap : 0) + lineCross;
            }
            cw = widest;
            ch = totalCross;
            if (wrap) minH = ch; // wrapped lines don't get shorter
        }

        // Scroll containers measure their content like any box (so a content-sized parent, such
        // as a dialog, fits it); they can still shrink to any size, since their automatic minimum
        // is 0 (see MinMain), and then scroll. A horizontal scroller doesn't impose its content's
        // min-content width on its parent.
        if (s.Overflow == Overflow.Scroll && s.Position == PositionMode.Relative)
        {
            bool scrollsHorizontally = wrap ? !row : row;
            if (scrollsHorizontally) minContent = 0;
        }
    }

    // ================================================================== arrange

    private void Arrange(int index)
    {
        ref NodeData n = ref ui.Nodes[index];
        ref Style s = ref ui.Styles[index];
        if (n.Kind == NodeKind.Text || n.FirstChild < 0) return;

        float inset = s.BorderWidth;
        float contentX = n.X + s.PaddingEdges.Left + inset;
        float contentY = n.Y + s.PaddingEdges.Top + inset;
        float contentW = MathF.Max(0, n.W - s.PaddingEdges.Horizontal - inset * 2);
        float contentH = MathF.Max(0, n.H - s.PaddingEdges.Vertical - inset * 2);

        float cx0 = n.ClipX0, cy0 = n.ClipY0, cx1 = n.ClipX1, cy1 = n.ClipY1;
        if (s.Overflow != Overflow.Visible)
        {
            cx0 = MathF.Max(cx0, n.X + inset); cy0 = MathF.Max(cy0, n.Y + inset);
            cx1 = MathF.Min(cx1, n.X + n.W - inset); cy1 = MathF.Min(cy1, n.Y + n.H - inset);
        }

        if (s.Display == Display.Grid)
        {
            for (int c = n.FirstChild; c >= 0; c = ui.Nodes[c].NextSibling)
            {
                ref NodeData cn = ref ui.Nodes[c];
                if (ui.Styles[c].IsOverlay) { cn.ClipX0 = 0; cn.ClipY0 = 0; cn.ClipX1 = rootW; cn.ClipY1 = rootH; }
                else { cn.ClipX0 = cx0; cn.ClipY0 = cy0; cn.ClipX1 = cx1; cn.ClipY1 = cy1; }
                if (ui.Styles[c].Display == Display.None) cn.W = cn.H = 0;
            }
            ArrangeGrid(ref n, ref s, contentX, contentY, contentW, contentH);
            ArrangeChildren(ref n, inset);
            return;
        }

        bool row = s.Direction == FlexDirection.Row;
        bool wrap = s.Wrapping == WrapMode.Wrap;
        float mainGap = row ? s.ColumnGap : s.RowGap;
        float crossGap = row ? s.RowGap : s.ColumnGap;
        float mainSize = row ? contentW : contentH;
        float crossSize = row ? contentH : contentW;

        // ---- collect flow children and resolve base sizes
        int flowBase = flowTop;
        for (int c = n.FirstChild; c >= 0; c = ui.Nodes[c].NextSibling)
        {
            ref NodeData cn = ref ui.Nodes[c];
            ref Style cs = ref ui.Styles[c];
            if (cs.IsOverlay) { cn.ClipX0 = 0; cn.ClipY0 = 0; cn.ClipX1 = rootW; cn.ClipY1 = rootH; }
            else { cn.ClipX0 = cx0; cn.ClipY0 = cy0; cn.ClipX1 = cx1; cn.ClipY1 = cy1; }
            if (cs.Display == Display.None) { cn.W = cn.H = 0; continue; }
            if (cs.Position != PositionMode.Relative) continue;

            float mh = cs.MarginEdges.Horizontal;
            Measure(c, MathF.Max(0, contentW - mh), contentW);

            if (row)
            {
                cn.W = Clamp(cs.Width.Resolve(contentW, cn.MeasuredW), cs.MinWidth, cs.MaxWidth, contentW);
            }
            else
            {
                // Column: the width (cross size) is known up front, which fixes wrapped heights.
                Align align = AlignOf(cs, s);
                float w = !wrap && align == Align.Stretch && cs.Width.IsAuto
                    ? MathF.Max(0, contentW - mh)
                    : cs.Width.Resolve(contentW, cn.MeasuredW);
                cn.W = Clamp(w, cs.MinWidth, cs.MaxWidth, contentW);
                if (cn.WidthDependent && cn.W != cn.MeasuredW) Measure(c, cn.W, contentW, exact: true);
                cn.H = Clamp(cs.Height.Resolve(contentH, cn.MeasuredH), cs.MinHeight, cs.MaxHeight, contentH);
            }
            PushFlow(c);
        }
        int flowCount = flowTop - flowBase;

        // ---- break into lines, flex each line
        int lineBase = lineTop;
        for (int i = 0; i < flowCount;)
        {
            int j = i;
            float used = 0;
            while (j < flowCount)
            {
                float outer = OuterMain(flow[flowBase + j], row);
                if (wrap && j > i && used + mainGap + outer > mainSize + Epsilon) break;
                used += (j > i ? mainGap : 0) + outer;
                j++;
            }

            FlexLine(flowBase + i, flowBase + j, used, mainSize, mainGap, row, wrap, contentW, contentH, crossSize, ref s, out Line line);
            PushLine(line);
            i = j;
        }

        // ---- extents and scrolling
        int lineCount = lineTop - lineBase;
        float maxMain = 0, totalCross = 0;
        for (int l = 0; l < lineCount; l++)
        {
            maxMain = MathF.Max(maxMain, lines[lineBase + l].Main);
            totalCross += (l > 0 ? crossGap : 0) + lines[lineBase + l].Cross;
        }

        float mainScroll = 0, crossScroll = 0;
        if (s.Overflow == Overflow.Scroll)
        {
            // Wrapping containers scroll across their lines; others along the main axis.
            bool alongMain = !wrap;
            float extent = alongMain ? maxMain : totalCross;
            float viewport = alongMain ? mainSize : crossSize;
            n.ScrollMax = MathF.Max(0, extent - viewport);
            float scroll = Math.Clamp(getScroll(n.StableId), 0, n.ScrollMax);
            setScroll(n.StableId, scroll);
            if (alongMain) mainScroll = scroll; else crossScroll = scroll;
        }

        // ---- position lines and items
        float crossCursor = (row ? contentY : contentX) - crossScroll;
        for (int l = 0; l < lineCount; l++)
        {
            Line line = lines[lineBase + l];
            int items = line.End - line.Start;
            float free = mainSize - line.Main;
            float lead = 0, between = mainGap;
            if (free > 0 && items > 0)
            {
                switch (s.Justify)
                {
                    case Justify.Center: lead = free * 0.5f; break;
                    case Justify.End: lead = free; break;
                    case Justify.SpaceBetween: if (items > 1) between += free / (items - 1); break;
                    case Justify.SpaceAround: lead = free / items * 0.5f; between += free / items; break;
                    case Justify.SpaceEvenly: lead = free / (items + 1); between += lead; break;
                }
            }

            float cursor = (row ? contentX : contentY) + lead - mainScroll;
            for (int k = line.Start; k < line.End; k++)
            {
                int c = flow[k];
                ref NodeData cn = ref ui.Nodes[c];
                ref Style cs = ref ui.Styles[c];
                Align align = AlignOf(cs, s);
                if (row)
                {
                    cn.X = cursor + cs.MarginEdges.Left;
                    cn.Y = crossCursor + CrossOffset(align, line.Cross, cn.H, cs.MarginEdges.Top, cs.MarginEdges.Bottom);
                    cursor += cn.W + cs.MarginEdges.Horizontal + between;
                }
                else
                {
                    cn.Y = cursor + cs.MarginEdges.Top;
                    cn.X = crossCursor + CrossOffset(align, line.Cross, cn.W, cs.MarginEdges.Left, cs.MarginEdges.Right);
                    cursor += cn.H + cs.MarginEdges.Vertical + between;
                }
            }
            crossCursor += line.Cross + crossGap;
        }

        // Release scratch before recursing.
        flowTop = flowBase;
        lineTop = lineBase;

        ArrangeChildren(ref n, inset);
    }

    /// <summary>Place absolute and fixed children, then arrange every child's own content.</summary>
    private void ArrangeChildren(ref NodeData n, float inset)
    {
        for (int c = n.FirstChild; c >= 0; c = ui.Nodes[c].NextSibling)
        {
            ref Style cs = ref ui.Styles[c];
            if (cs.Display == Display.None) continue;
            if (cs.Position == PositionMode.Absolute)
            {
                PlaceAbsolute(c, n.X + inset, n.Y + inset, n.W - inset * 2, n.H - inset * 2);
            }
            else if (cs.Position == PositionMode.Fixed)
            {
                PlaceAbsolute(c, 0, 0, rootW, rootH); // relative to the window
            }
            Arrange(c);
        }
    }

    /// <summary>Grow/shrink one flex line and size its items on the cross axis.</summary>
    private void FlexLine(int start, int end, float used, float mainSize, float mainGap, bool row, bool wrap,
        float contentW, float contentH, float crossSize, ref Style s, out Line line)
    {
        float free = mainSize - used;
        // Shrinking happens in scroll containers too (as in CSS): items stop at their minimum size
        // (their content, unless min-h-0 / overflow), and whatever still doesn't fit scrolls.
        if (free > Epsilon || free < -Epsilon)
        {
            ResolveFlexibleLengths(start, end, free, row, contentW, contentH);
        }

        float lineMain = 0, lineCross = 0;

        for (int k = start; k < end; k++)
        {
            int c = flow[k];
            ref NodeData cn = ref ui.Nodes[c];
            ref Style cs = ref ui.Styles[c];
            float main = row ? cn.W : cn.H;

            Align align = AlignOf(cs, s);
            if (row)
            {
                // Final width known: re-measure wrapped content to get its true height.
                if (cn.WidthDependent && cn.W != cn.MeasuredW) Measure(c, cn.W, contentW, exact: true);

                cn.H = !wrap && align == Align.Stretch && cs.Height.IsAuto
                    ? MathF.Max(0, crossSize - cs.MarginEdges.Vertical)
                    : cs.Height.Resolve(contentH, cn.MeasuredH);
                cn.H = Clamp(cn.H, cs.MinHeight, cs.MaxHeight, contentH);
                lineCross = MathF.Max(lineCross, cn.H + cs.MarginEdges.Vertical);
            }
            else
            {
                lineCross = MathF.Max(lineCross, cn.W + cs.MarginEdges.Horizontal);
            }

            lineMain += (k > start ? mainGap : 0) + main + (row ? cs.MarginEdges.Horizontal : cs.MarginEdges.Vertical);
        }

        if (!wrap)
        {
            lineCross = crossSize;
        }
        else
        {
            // Stretch items to the line's thickness.
            for (int k = start; k < end; k++)
            {
                int c = flow[k];
                ref NodeData cn = ref ui.Nodes[c];
                ref Style cs = ref ui.Styles[c];
                if (AlignOf(cs, s) != Align.Stretch) continue;
                if (row && cs.Height.IsAuto)
                    cn.H = Clamp(lineCross - cs.MarginEdges.Vertical, cs.MinHeight, cs.MaxHeight, contentH);
                else if (!row && cs.Width.IsAuto)
                    cn.W = Clamp(lineCross - cs.MarginEdges.Horizontal, cs.MinWidth, cs.MaxWidth, contentW);
            }
        }

        line = new Line { Start = start, End = end, Main = lineMain, Cross = lineCross };
    }

    /// <summary>
    /// CSS "resolve flexible lengths": distribute free space by grow (or shrink × base size),
    /// clamp to min/max (and min-content when shrinking), freeze the violators and repeat,
    /// so space an item can't take is redistributed to the others.
    /// </summary>
    private void ResolveFlexibleLengths(int start, int end, float free, bool row, float contentW, float contentH)
    {
        bool growing = free > 0;
        EnsureFlexScratch(end);

        int unfrozen = 0;
        for (int k = start; k < end; k++)
        {
            int c = flow[k];
            ref NodeData cn = ref ui.Nodes[c];
            ref Style cs = ref ui.Styles[c];
            float size = row ? cn.W : cn.H;
            baseSize[k] = size;
            target[k] = size;
            float factor = growing ? cs.GrowFactor : ShrinkOf(cs) * size;
            // Items that can't flex in this direction are frozen at their base size.
            bool frozenNow = factor <= 0 || (!growing && size <= MinMain(c, row, contentW, contentH) + Epsilon);
            frozen[k] = frozenNow;
            if (!frozenNow) unfrozen++;
        }

        // Total space taken by everything except the flexible part of unfrozen items.
        float lineSpace = free;
        for (int k = start; k < end; k++) lineSpace += baseSize[k];

        for (int iteration = 0; unfrozen > 0 && iteration <= end - start; iteration++)
        {
            float remaining = lineSpace, totalFactor = 0;
            for (int k = start; k < end; k++)
            {
                if (frozen[k]) { remaining -= target[k]; continue; }
                remaining -= baseSize[k];
                ref Style cs = ref ui.Styles[flow[k]];
                totalFactor += growing ? cs.GrowFactor : ShrinkOf(cs) * baseSize[k];
            }
            if (totalFactor <= 0) break;

            float violation = 0;
            for (int k = start; k < end; k++)
            {
                if (frozen[k]) continue;
                int c = flow[k];
                ref Style cs = ref ui.Styles[c];
                float factor = growing ? cs.GrowFactor : ShrinkOf(cs) * baseSize[k];
                float raw = baseSize[k] + remaining * factor / totalFactor;
                float clamped = ClampMain(c, raw, row, contentW, contentH);
                if (!growing) clamped = MathF.Max(clamped, MinMain(c, row, contentW, contentH));
                target[k] = clamped;
                violation += clamped - raw;
                minViolation[k] = clamped > raw + Epsilon;
                maxViolation[k] = clamped < raw - Epsilon;
            }

            if (MathF.Abs(violation) <= Epsilon)
            {
                break; // all targets satisfied
            }

            // Freeze min violators if the total is positive, max violators if negative.
            for (int k = start; k < end; k++)
            {
                if (frozen[k]) continue;
                if (violation > 0 ? minViolation[k] : maxViolation[k])
                {
                    frozen[k] = true;
                    unfrozen--;
                }
            }
        }

        for (int k = start; k < end; k++)
        {
            ref NodeData cn = ref ui.Nodes[flow[k]];
            if (row) cn.W = target[k]; else cn.H = target[k];
        }
    }

    /// <summary>
    /// Smallest main size an item may shrink to. As in CSS: an explicit min-width/min-height
    /// replaces the automatic minimum, and scroll/hidden containers have none (so they can shrink
    /// and scroll or clip); otherwise it's the content size (min-content width, measured height).
    /// </summary>
    private float MinMain(int c, bool row, float contentW, float contentH)
    {
        ref NodeData cn = ref ui.Nodes[c];
        ref Style cs = ref ui.Styles[c];
        bool explicitMin = cs.Has(row ? StyleFlags.MinWidth : StyleFlags.MinHeight);
        float auto = cs.Overflow != Overflow.Visible ? 0f : row ? cn.MinContentW : cn.MinContentH;
        if (explicitMin) auto = 0f;
        return row
            ? Clamp(auto, cs.MinWidth, cs.MaxWidth, contentW)
            : Clamp(auto, cs.MinHeight, cs.MaxHeight, contentH);
    }

    /// <summary>A child's contribution to its parent's minimum height (see <see cref="NodeData.MinContentH"/>).</summary>
    private float ItemMinHeight(int c)
    {
        ref NodeData cn = ref ui.Nodes[c];
        ref Style cs = ref ui.Styles[c];
        if (cs.Height.Unit == LengthUnit.Px) return cn.MeasuredH;
        if (cs.Has(StyleFlags.MinHeight)) return cs.MinHeight.Resolve(float.NaN, 0);
        return cs.Overflow != Overflow.Visible ? 0f : cn.MinContentH;
    }

    private float ClampMain(int c, float v, bool row, float contentW, float contentH)
    {
        ref Style cs = ref ui.Styles[c];
        return row ? Clamp(v, cs.MinWidth, cs.MaxWidth, contentW) : Clamp(v, cs.MinHeight, cs.MaxHeight, contentH);
    }

    private float[] baseSize = new float[128], target = new float[128];
    private bool[] frozen = new bool[128], minViolation = new bool[128], maxViolation = new bool[128];

    private void EnsureFlexScratch(int count)
    {
        if (count <= baseSize.Length) return;
        int n = Math.Max(count, baseSize.Length * 2);
        Array.Resize(ref baseSize, n);
        Array.Resize(ref target, n);
        Array.Resize(ref frozen, n);
        Array.Resize(ref minViolation, n);
        Array.Resize(ref maxViolation, n);
    }

    private void PlaceAbsolute(int c, float px, float py, float pw, float ph)
    {
        ref NodeData cn = ref ui.Nodes[c];
        ref Style cs = ref ui.Styles[c];
        Edges i = cs.Has(StyleFlags.Inset) ? cs.InsetEdges : new Edges(float.NaN);
        byte pct = cs.Has(StyleFlags.Inset) ? cs.InsetPercentMask : (byte)0;
        if ((pct & 1) != 0) i.Left *= pw * 0.01f;
        if ((pct & 2) != 0) i.Top *= ph * 0.01f;
        if ((pct & 4) != 0) i.Right *= pw * 0.01f;
        if ((pct & 8) != 0) i.Bottom *= ph * 0.01f;
        bool hasL = !float.IsNaN(i.Left), hasR = !float.IsNaN(i.Right);
        bool hasT = !float.IsNaN(i.Top), hasB = !float.IsNaN(i.Bottom);

        Measure(c, MathF.Max(0, pw - cs.MarginEdges.Horizontal), pw);
        float w = cs.Width.Resolve(pw, hasL && hasR ? pw - i.Left - i.Right : cn.MeasuredW);
        cn.W = Clamp(w, cs.MinWidth, cs.MaxWidth, pw);
        if (cn.WidthDependent && cn.W != cn.MeasuredW) Measure(c, cn.W, pw, exact: true);

        float h = cs.Height.Resolve(ph, hasT && hasB ? ph - i.Top - i.Bottom : cn.MeasuredH);
        cn.H = Clamp(h, cs.MinHeight, cs.MaxHeight, ph);
        cn.X = px + (hasL ? i.Left : hasR ? pw - i.Right - cn.W : 0) + cs.MarginEdges.Left;
        cn.Y = py + (hasT ? i.Top : hasB ? ph - i.Bottom - cn.H : 0) + cs.MarginEdges.Top;
    }

    // ================================================================== helpers

    private float OuterMain(int c, bool row)
    {
        ref NodeData cn = ref ui.Nodes[c];
        ref Style cs = ref ui.Styles[c];
        return row ? cn.W + cs.MarginEdges.Horizontal : cn.H + cs.MarginEdges.Vertical;
    }

    private void PushFlow(int c)
    {
        if (flowTop == flow.Length) Array.Resize(ref flow, flow.Length * 2);
        flow[flowTop++] = c;
    }

    private void PushLine(in Line line)
    {
        if (lineTop == lines.Length) Array.Resize(ref lines, lines.Length * 2);
        lines[lineTop++] = line;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Align AlignOf(in Style child, in Style parent) =>
        child.AlignSelf != Align.Auto ? child.AlignSelf : (parent.AlignItems == Align.Auto ? Align.Stretch : parent.AlignItems);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CrossOffset(Align align, float space, float size, float m0, float m1) => align switch
    {
        Align.Center => m0 + (space - size - m0 - m1) * 0.5f,
        Align.End => space - size - m1,
        _ => m0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ShrinkOf(in Style s) => s.Has(StyleFlags.Shrink) ? s.ShrinkFactor : 1f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp(float v, Length min, Length max, float parent)
    {
        float hi = max.Resolve(parent, float.PositiveInfinity);
        float lo = min.Resolve(parent, 0);
        if (v > hi) v = hi;
        if (v < lo) v = lo;
        return MathF.Max(0, v);
    }
}
