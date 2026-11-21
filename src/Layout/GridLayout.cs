namespace Cosmiria;

/// <summary>
/// CSS grid, the common part: column and row tracks sized in px, fr or auto (to content), gaps,
/// row-major auto-placement with spans and explicit start lines, and implicit rows (auto) for
/// items past the declared ones. Items stretch to their cell unless they have a size; rows align
/// them like flex's cross axis (items-* / self-*). fr columns have no content minimum, like
/// Tailwind's grid-cols-N (minmax(0, 1fr)); fr rows share the free height of a sized container.
/// </summary>
internal sealed partial class FlexLayout
{
    // Per item: child index, column, row, column span, row span. A stack: nested grids are
    // measured while their parent's slice is still in use.
    private int[] gridItems = new int[160];
    private int gridItemTop;
    private float[] gridSizes = new float[32];
    private int gridSizeTop;
    private bool[] gridCells = new bool[64];

    private struct GridFrame
    {
        public int Items, Count, Columns, Rows, ColSizes, RowSizes, ItemTop, SizeTop;
    }

    private static GridTrack Track(GridTrack[] tracks, int i) =>
        tracks != null && i < tracks.Length ? tracks[i] : GridTrack.Fr(1);

    private static GridTrack RowTrack(in Style s, int i) =>
        s.GridRowTracks != null && i < s.GridRowTracks.Length ? s.GridRowTracks[i]
        : i < s.GridRowCount && s.GridRowTracks == null ? GridTrack.Fr(1) : GridTrack.Auto;

    private void MeasureGrid(ref NodeData n, ref Style s, float inner,
        out float cw, out float ch, out float minContent, out bool dependent, out float minH)
    {
        float width = float.IsFinite(inner) ? inner : float.NaN;
        GridFrame g = BeginGrid(ref n, ref s, width);
        SizeColumns(ref g, ref s, width, out minContent);
        SizeRows(ref g, ref s, float.NaN);
        cw = Sum(g.ColSizes, g.Columns, s.ColumnGap);
        ch = Sum(g.RowSizes, g.Rows, s.RowGap);
        minH = ch;
        dependent = true;
        EndGrid(g);
    }

    private void ArrangeGrid(ref NodeData n, ref Style s, float contentX, float contentY, float contentW, float contentH)
    {
        GridFrame g = BeginGrid(ref n, ref s, contentW);
        SizeColumns(ref g, ref s, contentW, out _);
        SizeRows(ref g, ref s, contentH);

        float scroll = 0;
        if (s.Overflow == Overflow.Scroll)
        {
            n.ScrollMax = MathF.Max(0, Sum(g.RowSizes, g.Rows, s.RowGap) - contentH);
            scroll = Math.Clamp(getScroll(n.StableId), 0, n.ScrollMax);
            setScroll(n.StableId, scroll);
        }

        for (int i = 0; i < g.Count; i++)
        {
            int at = g.Items + i * 5;
            int c = gridItems[at];
            int col = gridItems[at + 1], row = gridItems[at + 2], cspan = gridItems[at + 3], rspan = gridItems[at + 4];
            float cellX = contentX + Offset(g.ColSizes, col, s.ColumnGap);
            float cellY = contentY + Offset(g.RowSizes, row, s.RowGap) - scroll;
            float cellW = Span(g.ColSizes, col, cspan, s.ColumnGap);
            float cellH = Span(g.RowSizes, row, rspan, s.RowGap);

            ref NodeData cn = ref ui.Nodes[c];
            ref Style cs = ref ui.Styles[c];
            float mh = cs.MarginEdges.Horizontal, mv = cs.MarginEdges.Vertical;
            float w = cs.Width.IsAuto ? MathF.Max(0, cellW - mh) : cs.Width.Resolve(cellW, cn.MeasuredW);
            cn.W = Clamp(w, cs.MinWidth, cs.MaxWidth, cellW);
            if (cn.WidthDependent && cn.W != cn.MeasuredW) Measure(c, cn.W, cellW, exact: true);
            Align align = AlignOf(cs, s);
            float h = align == Align.Stretch && cs.Height.IsAuto ? MathF.Max(0, cellH - mv) : cs.Height.Resolve(cellH, cn.MeasuredH);
            cn.H = Clamp(h, cs.MinHeight, cs.MaxHeight, cellH);
            cn.X = cellX + cs.MarginEdges.Left;
            cn.Y = cellY + CrossOffset(align, cellH, cn.H, cs.MarginEdges.Top, cs.MarginEdges.Bottom);
        }
        EndGrid(g);
    }

    /// <summary>Place the in-flow children into cells (row-major, sparse like CSS's default).</summary>
    private GridFrame BeginGrid(ref NodeData n, ref Style s, float width)
    {
        var g = new GridFrame { ItemTop = gridItemTop, SizeTop = gridSizeTop };
        g.Columns = s.GridColumnMin > 0
            ? float.IsNaN(width) ? 1 : Math.Max(1, (int)((width + s.ColumnGap) / (s.GridColumnMin + s.ColumnGap) + Epsilon))
            : Math.Max(1, s.GridColumnTracks?.Length ?? s.GridColumnCount);
        int count = 0;
        for (int c = n.FirstChild; c >= 0; c = ui.Nodes[c].NextSibling)
        {
            ref Style cs = ref ui.Styles[c];
            if (cs.Display != Display.None && cs.Position == PositionMode.Relative) count++;
        }
        g.Items = Reserve(ref gridItems, ref gridItemTop, count * 5);
        g.Count = count;

        int columns = g.Columns, rows = 0, cursorRow = 0, cursorCol = 0, i = 0, usedColumns = 0;
        Array.Clear(gridCells);
        int capacityRows = gridCells.Length / columns;
        for (int c = n.FirstChild; c >= 0; c = ui.Nodes[c].NextSibling)
        {
            ref Style cs = ref ui.Styles[c];
            if (cs.Display == Display.None || cs.Position != PositionMode.Relative) continue;
            int cspan = Math.Clamp((int)cs.ColSpan, 1, columns);
            int rspan = Math.Max(1, (int)cs.RowSpan);
            int col, row;
            if (cs.ColStart > 0 && cs.RowStart > 0)
            {
                col = Math.Min(cs.ColStart - 1, columns - cspan);
                row = cs.RowStart - 1;
            }
            else if (cs.ColStart > 0)
            {
                col = Math.Min(cs.ColStart - 1, columns - cspan);
                row = 0;
                while (!Free(row, col, cspan, rspan, columns, capacityRows)) row++;
            }
            else if (cs.RowStart > 0)
            {
                row = cs.RowStart - 1;
                col = 0;
                while (col + cspan < columns && !Free(row, col, cspan, rspan, columns, capacityRows)) col++;
            }
            else
            {
                row = cursorRow; col = cursorCol;
                while (true)
                {
                    if (col + cspan > columns) { row++; col = 0; continue; }
                    if (Free(row, col, cspan, rspan, columns, capacityRows)) break;
                    col++;
                }
                cursorRow = row; cursorCol = col + cspan;
            }

            capacityRows = Occupy(row, col, cspan, rspan, columns, capacityRows);
            rows = Math.Max(rows, row + rspan);
            usedColumns = Math.Max(usedColumns, col + cspan);
            int at = g.Items + i++ * 5;
            gridItems[at] = c; gridItems[at + 1] = col; gridItems[at + 2] = row; gridItems[at + 3] = cspan; gridItems[at + 4] = rspan;
        }

        // Auto-fit: the trailing columns nothing was placed in collapse.
        if (s.GridColumnFit && s.GridColumnMin > 0) g.Columns = Math.Max(1, usedColumns);
        g.Rows = Math.Max(rows, s.GridRowTracks?.Length ?? s.GridRowCount);
        g.ColSizes = Reserve(ref gridSizes, ref gridSizeTop, g.Columns);
        g.RowSizes = Reserve(ref gridSizes, ref gridSizeTop, g.Rows);
        return g;
    }

    private void EndGrid(in GridFrame g)
    {
        gridItemTop = g.ItemTop;
        gridSizeTop = g.SizeTop;
    }

    private bool Free(int row, int col, int cspan, int rspan, int columns, int capacityRows)
    {
        for (int r = row; r < row + rspan && r < capacityRows; r++)
            for (int k = col; k < col + cspan; k++)
                if (gridCells[r * columns + k]) return false;
        return true;
    }

    private int Occupy(int row, int col, int cspan, int rspan, int columns, int capacityRows)
    {
        if (row + rspan > capacityRows)
        {
            int needed = Math.Max((row + rspan) * columns, gridCells.Length * 2);
            Array.Resize(ref gridCells, needed);
            Array.Clear(gridCells, capacityRows * columns, needed - capacityRows * columns);
            capacityRows = needed / columns;
        }
        for (int r = row; r < row + rspan; r++)
            for (int k = col; k < col + cspan; k++)
                gridCells[r * columns + k] = true;
        return capacityRows;
    }

    /// <summary>
    /// Column widths for a content width of <paramref name="inner"/> (NaN: size to content, as
    /// when the grid itself is measured without a width).
    /// </summary>
    private void SizeColumns(ref GridFrame g, ref Style s, float inner, out float minContent)
    {
        GridTrack[] tracks = s.GridColumnTracks;
        float fixedSum = 0, totalFr = 0, frUnit = 0;
        minContent = s.ColumnGap * (g.Columns - 1);
        for (int k = 0; k < g.Columns; k++)
        {
            GridTrack t = Track(tracks, k);
            gridSizes[g.ColSizes + k] = t.Kind == GridTrackKind.Px ? t.Value : 0;
            if (t.Kind == GridTrackKind.Fr) totalFr += t.Value;
        }

        float percentBase = float.IsNaN(inner) ? float.NaN : inner;
        for (int i = 0; i < g.Count; i++)
        {
            int at = g.Items + i * 5;
            if (gridItems[at + 3] != 1) continue;
            int c = gridItems[at], k = gridItems[at + 1];
            GridTrack t = Track(tracks, k);
            if (t.Kind == GridTrackKind.Px) continue;
            Measure(c, float.NaN, percentBase);
            ref NodeData cn = ref ui.Nodes[c];
            float mh = ui.Styles[c].MarginEdges.Horizontal;
            ref float size = ref gridSizes[g.ColSizes + k];
            if (t.Kind == GridTrackKind.Auto) size = MathF.Max(size, cn.MeasuredW + mh);
            else if (t.Value > 0) frUnit = MathF.Max(frUnit, (cn.MeasuredW + mh) / t.Value);
        }

        for (int k = 0; k < g.Columns; k++)
        {
            GridTrack t = Track(tracks, k);
            if (t.Kind != GridTrackKind.Fr) fixedSum += gridSizes[g.ColSizes + k];
            if (t.Kind == GridTrackKind.Px) minContent += t.Value;
        }
        if (totalFr > 0)
        {
            if (!float.IsNaN(inner))
                frUnit = MathF.Max(0, inner - fixedSum - s.ColumnGap * (g.Columns - 1)) / totalFr;
            for (int k = 0; k < g.Columns; k++)
            {
                GridTrack t = Track(tracks, k);
                if (t.Kind == GridTrackKind.Fr) gridSizes[g.ColSizes + k] = frUnit * t.Value;
            }
        }

        // Auto-fill columns never go below their minimum, so neither does a single one.
        if (s.GridColumnMin > 0)
        {
            minContent = MathF.Max(minContent, s.GridColumnMin);
            for (int k = 0; k < g.Columns; k++)
                gridSizes[g.ColSizes + k] = MathF.Max(gridSizes[g.ColSizes + k], s.GridColumnMin);
        }

        // Min-content: auto columns can't go below their items' minimum; fr columns can (to 0).
        for (int i = 0; i < g.Count; i++)
        {
            int at = g.Items + i * 5;
            if (gridItems[at + 3] != 1 || Track(tracks, gridItems[at + 1]).Kind != GridTrackKind.Auto) continue;
            int c = gridItems[at];
            minContent += ui.Nodes[c].MinContentW + ui.Styles[c].MarginEdges.Horizontal;
        }
    }

    /// <summary>
    /// Row heights from the items measured at their cell width. Spanning items grow their last
    /// flexible row; with a known <paramref name="contentH"/>, fr rows share what's left.
    /// </summary>
    private void SizeRows(ref GridFrame g, ref Style s, float contentH)
    {
        for (int r = 0; r < g.Rows; r++)
        {
            GridTrack t = RowTrack(s, r);
            gridSizes[g.RowSizes + r] = t.Kind == GridTrackKind.Px ? t.Value : 0;
        }

        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < g.Count; i++)
            {
                int at = g.Items + i * 5;
                int c = gridItems[at], col = gridItems[at + 1], row = gridItems[at + 2], cspan = gridItems[at + 3], rspan = gridItems[at + 4];
                if ((rspan == 1) != (pass == 0)) continue;
                ref Style cs = ref ui.Styles[c];
                float cellW = Span(g.ColSizes, col, cspan, s.ColumnGap);
                float mh = cs.MarginEdges.Horizontal;
                Measure(c, MathF.Max(0, cellW - mh), cellW, exact: cs.Width.IsAuto);
                float h = ui.Nodes[c].MeasuredH + cs.MarginEdges.Vertical;

                if (rspan == 1)
                {
                    if (RowTrack(s, row).Kind != GridTrackKind.Px)
                        gridSizes[g.RowSizes + row] = MathF.Max(gridSizes[g.RowSizes + row], h);
                    continue;
                }
                float deficit = h - Span(g.RowSizes, row, rspan, s.RowGap);
                if (deficit <= 0) continue;
                for (int r = row + rspan - 1; r >= row; r--)
                {
                    if (RowTrack(s, r).Kind == GridTrackKind.Px) continue;
                    gridSizes[g.RowSizes + r] += deficit;
                    break;
                }
            }
        }

        if (float.IsNaN(contentH)) return;
        float fixedSum = 0, totalFr = 0;
        for (int r = 0; r < g.Rows; r++)
        {
            GridTrack t = RowTrack(s, r);
            if (t.Kind == GridTrackKind.Fr) totalFr += t.Value;
            else fixedSum += gridSizes[g.RowSizes + r];
        }
        if (totalFr <= 0) return;
        float unit = MathF.Max(0, contentH - fixedSum - s.RowGap * (g.Rows - 1)) / totalFr;
        for (int r = 0; r < g.Rows; r++)
        {
            GridTrack t = RowTrack(s, r);
            if (t.Kind == GridTrackKind.Fr) gridSizes[g.RowSizes + r] = MathF.Max(gridSizes[g.RowSizes + r], unit * t.Value);
        }
    }

    private float Sum(int at, int count, float gap)
    {
        float total = count > 0 ? gap * (count - 1) : 0;
        for (int k = 0; k < count; k++) total += gridSizes[at + k];
        return total;
    }

    private float Offset(int at, int index, float gap)
    {
        float x = 0;
        for (int k = 0; k < index; k++) x += gridSizes[at + k] + gap;
        return x;
    }

    private float Span(int at, int start, int span, float gap)
    {
        float w = gap * (span - 1);
        for (int k = start; k < start + span; k++) w += gridSizes[at + k];
        return w;
    }

    private static int Reserve<T>(ref T[] buffer, ref int top, int count)
    {
        if (top + count > buffer.Length) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, top + count));
        int at = top;
        top += count;
        return at;
    }
}
