using System.Numerics;

namespace Cosmiria;

/// <summary>
/// CPU rasterizer for <see cref="SvgIcon"/>: flattens curves in pixel space, accumulates signed
/// area coverage per pixel (the font-rs technique; exact anti-aliasing for non-zero fills),
/// expands strokes into polygons (round joins, butt/round/square caps), and composites shapes
/// in document order into premultiplied RGBA. Parts drawn with currentColor are rendered white,
/// so the GPU can tint them with the element's text color.
/// </summary>
internal sealed class SvgRasterizer
{
    private float[] acc = Array.Empty<float>();
    private float[] rgba = Array.Empty<float>();
    private int width, height, stride;
    private readonly List<Vector2> points = new();
    private readonly List<int> contourEnds = new();
    private readonly List<Vector2> polygon = new();

    /// <summary>Rasterize to premultiplied RGBA8 (width × height × 4 bytes).</summary>
    public void Rasterize(SvgIcon icon, int w, int h, Span<byte> output)
    {
        width = w;
        height = h;
        stride = w + 2;
        if (acc.Length < stride * h) acc = new float[stride * h];
        if (rgba.Length < w * h * 4) rgba = new float[w * h * 4];
        Array.Clear(rgba, 0, w * h * 4);

        // viewBox -> pixels (uniform scale, centered, like preserveAspectRatio="xMidYMid meet").
        Vector4 vb = icon.ViewBox;
        float scale = MathF.Min(w / vb.Z, h / vb.W);
        float ox = (w - vb.Z * scale) * 0.5f - vb.X * scale;
        float oy = (h - vb.W * scale) * 0.5f - vb.Y * scale;
        Matrix3x2 toPixels = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(ox, oy);

        foreach (SvgShape shape in icon.Shapes)
        {
            Matrix3x2 m = shape.Transform * toPixels;
            float lineScale = MathF.Sqrt(MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21));

            if (!shape.Fill.IsNone && shape.FillOpacity > 0)
            {
                Flatten(shape, m);
                ClearAcc();
                for (int c = 0, start = 0; c < contourEnds.Count; start = contourEnds[c], c++)
                {
                    int end = contourEnds[c];
                    for (int i = start; i < end; i++)
                    {
                        // Fills are always implicitly closed.
                        Line(points[i], points[i + 1 < end ? i + 1 : start]);
                    }
                }
                Composite(shape.Fill, shape.FillOpacity);
            }

            if (!shape.Stroke.IsNone && shape.StrokeOpacity > 0 && shape.StrokeWidth > 0)
            {
                Flatten(shape, m);
                ClearAcc();
                Stroke(shape, MathF.Max(0.5f, shape.StrokeWidth * lineScale) * 0.5f);
                Composite(shape.Stroke, shape.StrokeOpacity);
            }
        }

        for (int i = 0; i < w * h * 4; i++)
        {
            output[i] = (byte)(Math.Clamp(rgba[i], 0f, 1f) * 255f + 0.5f);
        }
    }

    private void ClearAcc() => Array.Clear(acc, 0, stride * height);

    // ------------------------------------------------------------------ flatten

    private void Flatten(SvgShape shape, Matrix3x2 m)
    {
        points.Clear();
        contourEnds.Clear();
        closedFlags.Clear();
        foreach (SvgContour contour in shape.Contours)
        {
            Vector2 p = Vector2.Transform(contour.Start, m);
            points.Add(p);
            foreach (SvgSegment seg in contour.Segments)
            {
                Vector2 end = Vector2.Transform(seg.End, m);
                if (!seg.Cubic)
                {
                    points.Add(end);
                }
                else
                {
                    Vector2 c1 = Vector2.Transform(seg.C1, m), c2 = Vector2.Transform(seg.C2, m);
                    float len = Vector2.Distance(p, c1) + Vector2.Distance(c1, c2) + Vector2.Distance(c2, end);
                    int n = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(len) * 1.5f), 2, 64);
                    for (int i = 1; i <= n; i++)
                    {
                        float t = i / (float)n, u = 1 - t;
                        points.Add(u * u * u * p + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * end);
                    }
                }
                p = end;
            }
            if (contour.Closed && points.Count > 0 && Vector2.DistanceSquared(points[^1], Vector2.Transform(contour.Start, m)) < 1e-6f)
            {
                points.RemoveAt(points.Count - 1); // duplicate closing point
            }
            contourEnds.Add(points.Count);
            closedFlags.Add(contour.Closed);
        }
    }

    private readonly List<bool> closedFlags = new();

    // ------------------------------------------------------------------ stroke

    private void Stroke(SvgShape shape, float r)
    {
        for (int c = 0, start = 0; c < contourEnds.Count; start = contourEnds[c], c++)
        {
            int end = contourEnds[c];
            bool closed = closedFlags[c];
            int count = end - start;
            if (count == 0) continue;
            if (count == 1)
            {
                if (shape.LineCap != SvgLineCap.Butt) Disc(points[start], r); // dot
                continue;
            }

            int segs = closed ? count : count - 1;
            for (int i = 0; i < segs; i++)
            {
                Vector2 a = points[start + i];
                Vector2 b = points[start + (i + 1) % count];
                if (!closed && shape.LineCap == SvgLineCap.Square)
                {
                    Vector2 dir = Vector2.Normalize(b - a);
                    if (float.IsNaN(dir.X)) continue;
                    if (i == 0) a -= dir * r;
                    if (i == segs - 1) b += dir * r;
                }
                Segment(a, b, r);
            }

            // Joins (round, which also approximates miter/bevel at icon sizes).
            int firstJoin = closed ? 0 : 1, lastJoin = closed ? count : count - 1;
            for (int i = firstJoin; i < lastJoin; i++) Disc(points[start + i], r);

            if (!closed && shape.LineCap == SvgLineCap.Round)
            {
                Disc(points[start], r);
                Disc(points[end - 1], r);
            }
        }
    }

    private void Segment(Vector2 a, Vector2 b, float r)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return;
        Vector2 n = new Vector2(-d.Y, d.X) / len * r;
        polygon.Clear();
        polygon.Add(a + n); polygon.Add(b + n); polygon.Add(b - n); polygon.Add(a - n);
        FillPolygon();
    }

    private void Disc(Vector2 c, float r)
    {
        int n = Math.Clamp((int)MathF.Ceiling(r * 3), 8, 48);
        polygon.Clear();
        for (int i = 0; i < n; i++)
        {
            float t = i * MathF.Tau / n;
            polygon.Add(c + new Vector2(MathF.Cos(t), MathF.Sin(t)) * r);
        }
        FillPolygon();
    }

    /// <summary>Add a convex polygon with a consistent (positive) orientation, so overlapping
    /// stroke pieces accumulate instead of cancelling.</summary>
    private void FillPolygon()
    {
        float area = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            Vector2 p = polygon[i], q = polygon[(i + 1) % polygon.Count];
            area += p.X * q.Y - q.X * p.Y;
        }
        if (area < 0) polygon.Reverse();
        for (int i = 0; i < polygon.Count; i++) Line(polygon[i], polygon[(i + 1) % polygon.Count]);
    }

    // ------------------------------------------------------------------ coverage

    /// <summary>Accumulate the signed area a line contributes to each pixel (font-rs).</summary>
    private void Line(Vector2 p0, Vector2 p1)
    {
        if (p0.Y == p1.Y) return;
        float dir;
        if (p0.Y < p1.Y) dir = 1;
        else { dir = -1; (p0, p1) = (p1, p0); }

        // Clamp x into the row (contributions left/right of the image still count for winding).
        p0.X = Math.Clamp(p0.X, 0, width);
        p1.X = Math.Clamp(p1.X, 0, width);

        float dxdy = (p1.X - p0.X) / (p1.Y - p0.Y);
        float x = p0.X;
        if (p0.Y < 0) x -= p0.Y * dxdy;
        int yStart = Math.Max(0, (int)MathF.Floor(p0.Y));
        int yEnd = Math.Min(height, (int)MathF.Ceiling(p1.Y));

        for (int y = yStart; y < yEnd; y++)
        {
            int row = y * stride;
            float dy = MathF.Min(y + 1, p1.Y) - MathF.Max(y, p0.Y);
            float xNext = x + dxdy * dy;
            float d = dy * dir;
            float x0 = MathF.Min(x, xNext), x1 = MathF.Max(x, xNext);
            x0 = Math.Clamp(x0, 0, width);
            x1 = Math.Clamp(x1, 0, width);
            float x0Floor = MathF.Floor(x0);
            int x0i = (int)x0Floor;
            float x1Ceil = MathF.Ceiling(x1);
            int x1i = (int)x1Ceil;

            if (x1i <= x0i + 1)
            {
                float xmf = 0.5f * (x + xNext) - x0Floor;
                acc[row + x0i] += d - d * xmf;
                acc[row + x0i + 1] += d * xmf;
            }
            else
            {
                float s = 1f / (x1 - x0);
                float x0f = x0 - x0Floor;
                float a0 = 0.5f * s * (1 - x0f) * (1 - x0f);
                float x1f = x1 - x1Ceil + 1;
                float am = 0.5f * s * x1f * x1f;
                acc[row + x0i] += d * a0;
                if (x1i == x0i + 2)
                {
                    acc[row + x0i + 1] += d * (1 - a0 - am);
                }
                else
                {
                    float a1 = s * (1.5f - x0f);
                    acc[row + x0i + 1] += d * (a1 - a0);
                    for (int xi = x0i + 2; xi < x1i - 1; xi++) acc[row + xi] += d * s;
                    float a2 = a1 + (x1i - x0i - 3) * s;
                    acc[row + x1i - 1] += d * (1 - a2 - am);
                }
                acc[row + x1i] += d * am;
            }
            x = xNext;
        }
    }

    /// <summary>Resolve coverage (|winding| clamped to 1) and composite the paint over the image.</summary>
    private void Composite(SvgPaint paint, float opacity)
    {
        Vector4 c = paint.IsCurrent ? Vector4.One : paint.Color;
        float alpha = c.W * opacity;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            float sum = 0;
            for (int x = 0; x < width; x++)
            {
                sum += acc[row + x];
                float coverage = MathF.Min(1f, MathF.Abs(sum));
                if (coverage <= 0.0005f) continue;
                float a = coverage * alpha;
                int o = (y * width + x) * 4;
                float inv = 1 - a;
                rgba[o + 0] = c.X * a + rgba[o + 0] * inv;
                rgba[o + 1] = c.Y * a + rgba[o + 1] * inv;
                rgba[o + 2] = c.Z * a + rgba[o + 2] * inv;
                rgba[o + 3] = a + rgba[o + 3] * inv;
            }
        }
    }
}
