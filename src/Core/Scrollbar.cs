namespace Cosmiria;

/// <summary>
/// Overlay scrollbar geometry for scroll containers (along their main axis) and text areas
/// (vertical), shared by painting and hit testing. Coordinates are in the node's layout space.
/// </summary>
internal static class Scrollbar
{
    public const float Thickness = 6, Margin = 2, MinThumb = 24;

    public struct Geometry
    {
        public bool Vertical;
        public float TrackX, TrackY, TrackW, TrackH;
        public float TrackStart, TrackLength, ThumbStart, ThumbLength;

        public readonly bool TrackContains(float x, float y) =>
            x >= TrackX - 2 && x < TrackX + TrackW + 2 && y >= TrackY && y < TrackY + TrackH; // 2px slack to grab

        public readonly bool ThumbContains(float x, float y)
        {
            float a = Vertical ? y : x;
            return TrackContains(x, y) && a >= ThumbStart && a < ThumbStart + ThumbLength;
        }
    }

    /// <summary>False when the node doesn't scroll (or is too small for a bar).</summary>
    public static bool Compute(ref NodeData n, ref Style s, float scroll, out Geometry g)
    {
        g = default;
        if (n.ScrollMax <= 0) return false;
        bool vertical = n.Kind == NodeKind.TextInput || s.Direction == FlexDirection.Column;
        float b = s.BorderWidth;
        float view = vertical ? n.H - b * 2 : n.W - b * 2;
        if (view <= MinThumb) return false;
        g.Vertical = vertical;
        if (vertical)
        {
            g.TrackX = n.X + n.W - b - Margin - Thickness; g.TrackY = n.Y + b + Margin;
            g.TrackW = Thickness; g.TrackH = n.H - b * 2 - Margin * 2;
            g.TrackStart = g.TrackY; g.TrackLength = g.TrackH;
        }
        else
        {
            g.TrackX = n.X + b + Margin; g.TrackY = n.Y + n.H - b - Margin - Thickness;
            g.TrackW = n.W - b * 2 - Margin * 2; g.TrackH = Thickness;
            g.TrackStart = g.TrackX; g.TrackLength = g.TrackW;
        }
        g.ThumbLength = MathF.Max(MinThumb, g.TrackLength * view / (view + n.ScrollMax));
        g.ThumbStart = g.TrackStart + (g.TrackLength - g.ThumbLength) * Math.Clamp(scroll / n.ScrollMax, 0, 1);
        return true;
    }
}
