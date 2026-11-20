using System.Globalization;
using System.Numerics;
using System.Xml;

namespace Cosmiria;

/// <summary>
/// A parsed SVG document, rendered as an anti-aliased icon. Supports paths (all commands,
/// including arcs), rect / circle / ellipse / line / polyline / polygon, groups with transforms,
/// fill / stroke / stroke-width / stroke-linecap / opacity (as attributes or inline style), and
/// <c>currentColor</c>. Unspecified fills count as <c>currentColor</c>, so plain icons can be tinted
/// with the element's text color. Not supported: gradients, masks, clip paths, text, &lt;use&gt;;
/// even-odd fills render as non-zero.
/// <code>
/// static readonly SvgIcon Logo = SvgIcon.Load("Assets/logo.svg");
/// ui.Icon(Logo, 24).Styled(ui.Style().TextColorOf(Tailwind.Sky400));
/// </code>
/// </summary>
public sealed class SvgIcon
{
    private static int nextId;

    internal readonly int Id = Interlocked.Increment(ref nextId);
    internal readonly List<SvgShape> Shapes = new();

    /// <summary>The viewBox (x, y, width, height) shapes are defined in.</summary>
    public Vector4 ViewBox { get; private set; }

    /// <summary>Width / height of the viewBox.</summary>
    public float AspectRatio => ViewBox.W > 0 ? ViewBox.Z / ViewBox.W : 1f;

    /// <summary>True if any part is drawn with <c>currentColor</c> (the icon takes the text color).</summary>
    public bool UsesCurrentColor { get; private set; }

    private SvgIcon() { }

    public static SvgIcon Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Rasterize to premultiplied RGBA8 (w × h × 4 bytes). currentColor renders white.</summary>
    public byte[] Rasterize(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        new SvgRasterizer().Rasterize(this, width, height, pixels);
        return pixels;
    }

    public static SvgIcon Parse(string svg)
    {
        var icon = new SvgIcon();
        new SvgParser(icon).Parse(svg);
        return icon;
    }

    // ================================================================== parser

    private sealed class SvgParser
    {
        private readonly SvgIcon icon;
        private readonly Stack<SvgPaintState> states = new();

        public SvgParser(SvgIcon icon) => this.icon = icon;

        public void Parse(string svg)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(svg), settings);
            states.Push(SvgPaintState.Default);
            bool sawRoot = false;
            int skipDepth = -1;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (skipDepth >= 0 && reader.Depth == skipDepth) { skipDepth = -1; continue; }
                    if (skipDepth < 0 && (reader.LocalName == "g" || reader.LocalName == "svg") && states.Count > 1) states.Pop();
                    continue;
                }
                if (reader.NodeType != XmlNodeType.Element || skipDepth >= 0) continue;

                string name = reader.LocalName;
                bool empty = reader.IsEmptyElement;
                switch (name)
                {
                    case "svg":
                        if (!sawRoot) ReadRoot(reader);
                        sawRoot = true;
                        PushState(reader, empty);
                        break;
                    case "g":
                        PushState(reader, empty);
                        break;
                    case "path": AddShape(reader, s => PathParser.Parse(reader.GetAttribute("d"), s)); break;
                    case "rect": AddShape(reader, s => Rect(reader, s)); break;
                    case "circle":
                        AddShape(reader, s => Ellipse(s, Num(reader, "cx"), Num(reader, "cy"), Num(reader, "r"), Num(reader, "r")));
                        break;
                    case "ellipse":
                        AddShape(reader, s => Ellipse(s, Num(reader, "cx"), Num(reader, "cy"), Num(reader, "rx"), Num(reader, "ry")));
                        break;
                    case "line":
                        AddShape(reader, s =>
                        {
                            s.MoveTo(new Vector2(Num(reader, "x1"), Num(reader, "y1")));
                            s.LineTo(new Vector2(Num(reader, "x2"), Num(reader, "y2")));
                        }, isLine: true);
                        break;
                    case "polyline": AddShape(reader, s => Points(reader.GetAttribute("points"), s, close: false)); break;
                    case "polygon": AddShape(reader, s => Points(reader.GetAttribute("points"), s, close: true)); break;
                    default:
                        // defs, title, desc, clipPath, gradients, ...: skip the whole subtree.
                        if (!empty) skipDepth = reader.Depth;
                        break;
                }
            }

            if (icon.ViewBox.Z <= 0 || icon.ViewBox.W <= 0) icon.ViewBox = new Vector4(0, 0, 24, 24);
        }

        private void ReadRoot(XmlReader r)
        {
            string vb = r.GetAttribute("viewBox");
            if (vb != null)
            {
                var n = NumberList(vb);
                if (n.Count == 4) { icon.ViewBox = new Vector4(n[0], n[1], n[2], n[3]); return; }
            }
            float w = Num(r, "width", 24), h = Num(r, "height", 24);
            icon.ViewBox = new Vector4(0, 0, w, h);
        }

        private void PushState(XmlReader r, bool empty)
        {
            if (empty) return; // nothing nested: no state to keep
            states.Push(ReadPaint(r, states.Peek()));
        }

        private void AddShape(XmlReader r, Action<SvgPathBuilder> build, bool isLine = false)
        {
            SvgPaintState paint = ReadPaint(r, states.Peek());
            var builder = new SvgPathBuilder();
            build(builder);
            if (builder.Contours.Count == 0) return;

            var shape = new SvgShape
            {
                Contours = builder.Contours,
                Transform = paint.Transform,
                Fill = isLine ? SvgPaint.None : paint.Fill,
                Stroke = paint.Stroke,
                StrokeWidth = paint.StrokeWidth,
                LineCap = paint.LineCap,
                FillOpacity = paint.Opacity * paint.FillOpacity,
                StrokeOpacity = paint.Opacity * paint.StrokeOpacity,
            };
            if (shape.Fill.IsCurrent || shape.Stroke.IsCurrent) icon.UsesCurrentColor = true;
            icon.Shapes.Add(shape);
        }

        private static SvgPaintState ReadPaint(XmlReader r, SvgPaintState parent)
        {
            var s = parent;
            s.Opacity = parent.Opacity; // multiplied below
            string transform = r.GetAttribute("transform");
            if (transform != null) s.Transform = TransformParser.Parse(transform) * parent.Transform;

            float ownOpacity = 1;
            void Apply(string key, string value)
            {
                value = value.Trim();
                switch (key)
                {
                    case "fill": s.Fill = SvgPaint.Parse(value, parent.Fill); break;
                    case "stroke": s.Stroke = SvgPaint.Parse(value, parent.Stroke); break;
                    case "stroke-width": s.StrokeWidth = ParseLength(value, parent.StrokeWidth); break;
                    case "stroke-linecap": s.LineCap = value switch { "round" => SvgLineCap.Round, "square" => SvgLineCap.Square, _ => SvgLineCap.Butt }; break;
                    case "opacity": ownOpacity = ParseLength(value, 1); break;
                    case "fill-opacity": s.FillOpacity = ParseLength(value, 1); break;
                    case "stroke-opacity": s.StrokeOpacity = ParseLength(value, 1); break;
                }
            }

            foreach (string key in PaintAttributes)
            {
                string v = r.GetAttribute(key);
                if (v != null) Apply(key, v);
            }
            string style = r.GetAttribute("style");
            if (style != null)
            {
                foreach (string decl in style.Split(';'))
                {
                    int colon = decl.IndexOf(':');
                    if (colon > 0) Apply(decl.Substring(0, colon).Trim(), decl.Substring(colon + 1));
                }
            }
            s.Opacity = parent.Opacity * ownOpacity;
            return s;
        }

        private static readonly string[] PaintAttributes =
            { "fill", "stroke", "stroke-width", "stroke-linecap", "opacity", "fill-opacity", "stroke-opacity" };

        private static void Rect(XmlReader r, SvgPathBuilder s)
        {
            float x = Num(r, "x"), y = Num(r, "y"), w = Num(r, "width"), h = Num(r, "height");
            if (w <= 0 || h <= 0) return;
            string rxs = r.GetAttribute("rx"), rys = r.GetAttribute("ry");
            float rx = rxs != null ? ParseLength(rxs, 0) : (rys != null ? ParseLength(rys, 0) : 0);
            float ry = rys != null ? ParseLength(rys, 0) : rx;
            rx = MathF.Min(rx, w / 2); ry = MathF.Min(ry, h / 2);
            if (rx <= 0 || ry <= 0)
            {
                s.MoveTo(new(x, y)); s.LineTo(new(x + w, y)); s.LineTo(new(x + w, y + h)); s.LineTo(new(x, y + h)); s.Close();
                return;
            }
            s.MoveTo(new(x + rx, y));
            s.LineTo(new(x + w - rx, y)); s.ArcTo(rx, ry, 0, false, true, new(x + w, y + ry));
            s.LineTo(new(x + w, y + h - ry)); s.ArcTo(rx, ry, 0, false, true, new(x + w - rx, y + h));
            s.LineTo(new(x + rx, y + h)); s.ArcTo(rx, ry, 0, false, true, new(x, y + h - ry));
            s.LineTo(new(x, y + ry)); s.ArcTo(rx, ry, 0, false, true, new(x + rx, y));
            s.Close();
        }

        private static void Ellipse(SvgPathBuilder s, float cx, float cy, float rx, float ry)
        {
            if (rx <= 0 || ry <= 0) return;
            s.MoveTo(new(cx + rx, cy));
            s.ArcTo(rx, ry, 0, false, true, new(cx - rx, cy));
            s.ArcTo(rx, ry, 0, false, true, new(cx + rx, cy));
            s.Close();
        }

        private static void Points(string points, SvgPathBuilder s, bool close)
        {
            if (points == null) return;
            var n = NumberList(points);
            for (int i = 0; i + 1 < n.Count; i += 2)
            {
                var p = new Vector2(n[i], n[i + 1]);
                if (i == 0) s.MoveTo(p); else s.LineTo(p);
            }
            if (close) s.Close();
        }

        private static float Num(XmlReader r, string name, float fallback = 0)
        {
            string v = r.GetAttribute(name);
            return v == null ? fallback : ParseLength(v, fallback);
        }

        internal static float ParseLength(string v, float fallback)
        {
            v = v.Trim();
            int end = 0;
            while (end < v.Length && (char.IsDigit(v[end]) || v[end] == '.' || v[end] == '-' || v[end] == '+' || v[end] == 'e' || v[end] == 'E')) end++;
            return float.TryParse(v.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }

        internal static List<float> NumberList(string s)
        {
            var list = new List<float>();
            var t = new PathTokenizer(s);
            while (t.TryNumber(out float f)) list.Add(f);
            return list;
        }
    }
}

// ====================================================================== model

internal enum SvgLineCap : byte { Butt, Round, Square }

internal readonly struct SvgPaint
{
    public readonly bool IsNone;
    public readonly bool IsCurrent;
    public readonly Vector4 Color; // straight RGBA

    private SvgPaint(bool none, bool current, Vector4 color) { IsNone = none; IsCurrent = current; Color = color; }

    public static readonly SvgPaint None = new(true, false, default);
    public static readonly SvgPaint Current = new(false, true, Vector4.One);
    public static SvgPaint Solid(Vector4 c) => new(false, false, c);

    public static SvgPaint Parse(string value, SvgPaint inherited)
    {
        value = value.Trim();
        if (value == "none" || value == "transparent") return None;
        if (value == "currentColor") return Current;
        if (value == "inherit") return inherited;
        if (value.StartsWith("url(")) return Current; // gradients/patterns: unsupported, use currentColor
        if (value.StartsWith("#"))
        {
            string hex = value.Substring(1);
            if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint raw))
            {
                if (hex.Length == 3)
                    return Solid(new Vector4(((raw >> 8) & 0xF) / 15f, ((raw >> 4) & 0xF) / 15f, (raw & 0xF) / 15f, 1));
                if (hex.Length == 6)
                    return Solid(new Vector4(((raw >> 16) & 0xFF) / 255f, ((raw >> 8) & 0xFF) / 255f, (raw & 0xFF) / 255f, 1));
            }
            return inherited;
        }
        if (value.StartsWith("rgb"))
        {
            var n = new List<float>();
            var t = new PathTokenizer(value.Substring(value.IndexOf('(') + 1));
            while (t.TryNumber(out float f)) n.Add(f);
            if (n.Count >= 3) return Solid(new Vector4(n[0] / 255f, n[1] / 255f, n[2] / 255f, n.Count > 3 ? n[3] : 1));
            return inherited;
        }
        return value switch
        {
            "black" => Solid(new Vector4(0, 0, 0, 1)),
            "white" => Solid(Vector4.One),
            "red" => Solid(new Vector4(1, 0, 0, 1)),
            "green" => Solid(new Vector4(0, 0.5f, 0, 1)),
            "blue" => Solid(new Vector4(0, 0, 1, 1)),
            "gray" or "grey" => Solid(new Vector4(0.5f, 0.5f, 0.5f, 1)),
            _ => inherited,
        };
    }
}

internal struct SvgPaintState
{
    public SvgPaint Fill, Stroke;
    public float StrokeWidth, Opacity, FillOpacity, StrokeOpacity;
    public SvgLineCap LineCap;
    public Matrix3x2 Transform;

    // SVG's default fill is black; for icons that means "the icon color", i.e. currentColor.
    public static SvgPaintState Default => new()
    {
        Fill = SvgPaint.Current,
        Stroke = SvgPaint.None,
        StrokeWidth = 1,
        Opacity = 1,
        FillOpacity = 1,
        StrokeOpacity = 1,
        Transform = Matrix3x2.Identity,
    };
}

internal sealed class SvgShape
{
    public List<SvgContour> Contours;
    public Matrix3x2 Transform;
    public SvgPaint Fill, Stroke;
    public float StrokeWidth, FillOpacity, StrokeOpacity;
    public SvgLineCap LineCap;
}

/// <summary>A subpath: start point plus segments (lines and cubic curves), in user units.</summary>
internal sealed class SvgContour
{
    public readonly List<SvgSegment> Segments = new();
    public Vector2 Start;
    public bool Closed;
}

internal struct SvgSegment
{
    public bool Cubic;
    public Vector2 C1, C2, End; // C1/C2 only for cubic
}

/// <summary>Builds contours; quadratic curves and arcs are converted to cubics.</summary>
internal sealed class SvgPathBuilder
{
    public readonly List<SvgContour> Contours = new();
    private SvgContour current;
    public Vector2 Point;
    public Vector2 LastControl; // for S/T reflection
    public char LastCommand;

    public void MoveTo(Vector2 p)
    {
        current = new SvgContour { Start = p };
        Contours.Add(current);
        Point = p;
    }

    private void Ensure()
    {
        if (current == null) MoveTo(Point);
    }

    public void LineTo(Vector2 p)
    {
        Ensure();
        current.Segments.Add(new SvgSegment { End = p });
        Point = p;
    }

    public void CubicTo(Vector2 c1, Vector2 c2, Vector2 p)
    {
        Ensure();
        current.Segments.Add(new SvgSegment { Cubic = true, C1 = c1, C2 = c2, End = p });
        Point = p;
        LastControl = c2;
    }

    public void QuadTo(Vector2 c, Vector2 p)
    {
        Vector2 p0 = Point;
        CubicTo(p0 + 2f / 3f * (c - p0), p + 2f / 3f * (c - p), p);
        LastControl = c;
    }

    public void Close()
    {
        if (current == null) return;
        current.Closed = true;
        Point = current.Start;
        current = null; // a following command without M starts a new subpath at the start point
    }

    /// <summary>SVG elliptical arc (endpoint parameterization) as cubic segments.</summary>
    public void ArcTo(float rx, float ry, float rotationDeg, bool largeArc, bool sweep, Vector2 p)
    {
        Vector2 p0 = Point;
        if (p0 == p) return;
        rx = MathF.Abs(rx); ry = MathF.Abs(ry);
        if (rx < 1e-6f || ry < 1e-6f) { LineTo(p); return; }

        float phi = rotationDeg * MathF.PI / 180f;
        float cos = MathF.Cos(phi), sin = MathF.Sin(phi);
        Vector2 d = (p0 - p) * 0.5f;
        float x1p = cos * d.X + sin * d.Y;
        float y1p = -sin * d.X + cos * d.Y;

        float lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1) { float s = MathF.Sqrt(lambda); rx *= s; ry *= s; }

        float num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        float den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        float coef = MathF.Sqrt(MathF.Max(0, num / den)) * (largeArc == sweep ? -1 : 1);
        float cxp = coef * rx * y1p / ry;
        float cyp = -coef * ry * x1p / rx;
        Vector2 mid = (p0 + p) * 0.5f;
        float cx = cos * cxp - sin * cyp + mid.X;
        float cy = sin * cxp + cos * cyp + mid.Y;

        float theta1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        float dtheta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && dtheta > 0) dtheta -= 2 * MathF.PI;
        else if (sweep && dtheta < 0) dtheta += 2 * MathF.PI;

        int n = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(dtheta) / (MathF.PI / 2)));
        float step = dtheta / n;
        float k = 4f / 3f * MathF.Tan(step / 4);
        float t = theta1;
        for (int i = 0; i < n; i++)
        {
            float c1 = MathF.Cos(t), s1 = MathF.Sin(t);
            float c2 = MathF.Cos(t + step), s2 = MathF.Sin(t + step);
            Vector2 e1 = new(c1 - k * s1, s1 + k * c1);
            Vector2 e2 = new(c2 + k * s2, s2 - k * c2);
            Vector2 e3 = new(c2, s2);
            CubicTo(Map(e1), Map(e2), i == n - 1 ? p : Map(e3));
            t += step;
        }

        Vector2 Map(Vector2 u) => new(cx + rx * u.X * cos - ry * u.Y * sin, cy + rx * u.X * sin + ry * u.Y * cos);
    }

    private static float Angle(float ux, float uy, float vx, float vy)
    {
        float a = MathF.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        return a;
    }
}

/// <summary>Tokenizer for SVG number lists / path data (handles "1.5.5", "-1-2", "1e-3").</summary>
internal ref struct PathTokenizer
{
    private readonly ReadOnlySpan<char> s;
    public int Pos;

    public PathTokenizer(ReadOnlySpan<char> s) { this.s = s; Pos = 0; }

    public void SkipSeparators()
    {
        while (Pos < s.Length && (char.IsWhiteSpace(s[Pos]) || s[Pos] == ',')) Pos++;
    }

    public bool AtEnd { get { SkipSeparators(); return Pos >= s.Length; } }

    public char PeekChar() { SkipSeparators(); return Pos < s.Length ? s[Pos] : '\0'; }

    public bool TryCommand(out char c)
    {
        SkipSeparators();
        if (Pos < s.Length && char.IsLetter(s[Pos]) && s[Pos] != 'e' && s[Pos] != 'E')
        {
            c = s[Pos++];
            return true;
        }
        c = '\0';
        return false;
    }

    public bool TryNumber(out float value)
    {
        SkipSeparators();
        int start = Pos;
        if (Pos < s.Length && (s[Pos] == '+' || s[Pos] == '-')) Pos++;
        bool digits = false, dot = false;
        while (Pos < s.Length)
        {
            char c = s[Pos];
            if (char.IsDigit(c)) { digits = true; Pos++; }
            else if (c == '.' && !dot) { dot = true; Pos++; }
            else break;
        }
        if (digits && Pos < s.Length && (s[Pos] == 'e' || s[Pos] == 'E'))
        {
            int save = Pos;
            Pos++;
            if (Pos < s.Length && (s[Pos] == '+' || s[Pos] == '-')) Pos++;
            if (Pos < s.Length && char.IsDigit(s[Pos])) { while (Pos < s.Length && char.IsDigit(s[Pos])) Pos++; }
            else Pos = save;
        }
        if (!digits)
        {
            Pos = start;
            value = 0;
            return false;
        }
        return float.TryParse(s.Slice(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Arc flags may be written without separators ("a1 1 0 01 5 5").</summary>
    public bool TryFlag(out bool flag)
    {
        SkipSeparators();
        if (Pos < s.Length && (s[Pos] == '0' || s[Pos] == '1'))
        {
            flag = s[Pos++] == '1';
            return true;
        }
        flag = false;
        return false;
    }
}

internal static class PathParser
{
    public static void Parse(string d, SvgPathBuilder b)
    {
        if (string.IsNullOrEmpty(d)) return;
        var t = new PathTokenizer(d);
        char cmd = '\0';
        while (!t.AtEnd)
        {
            if (t.TryCommand(out char c)) cmd = c;
            else if (cmd == '\0') return; // garbage before the first command
            else if (cmd == 'M') cmd = 'L'; // implicit lineto after moveto
            else if (cmd == 'm') cmd = 'l';

            bool rel = char.IsLower(cmd);
            Vector2 o = rel ? b.Point : Vector2.Zero;
            char up = char.ToUpperInvariant(cmd);
            bool ok = true;
            switch (up)
            {
                case 'M':
                    ok = P(ref t, o, out var m);
                    if (ok) b.MoveTo(m);
                    break;
                case 'L':
                    ok = P(ref t, o, out var l);
                    if (ok) b.LineTo(l);
                    break;
                case 'H':
                    ok = t.TryNumber(out float hx);
                    if (ok) b.LineTo(new Vector2(rel ? b.Point.X + hx : hx, b.Point.Y));
                    break;
                case 'V':
                    ok = t.TryNumber(out float vy);
                    if (ok) b.LineTo(new Vector2(b.Point.X, rel ? b.Point.Y + vy : vy));
                    break;
                case 'C':
                    if (P(ref t, o, out var c1) && P(ref t, o, out var c2) && P(ref t, o, out var ce)) b.CubicTo(c1, c2, ce);
                    else ok = false;
                    break;
                case 'S':
                {
                    Vector2 r1 = b.LastCommand is 'C' or 'S' ? 2 * b.Point - b.LastControl : b.Point;
                    if (P(ref t, o, out var s2) && P(ref t, o, out var se)) b.CubicTo(r1, s2, se);
                    else ok = false;
                    break;
                }
                case 'Q':
                    if (P(ref t, o, out var q1) && P(ref t, o, out var qe)) b.QuadTo(q1, qe);
                    else ok = false;
                    break;
                case 'T':
                {
                    Vector2 r1 = b.LastCommand is 'Q' or 'T' ? 2 * b.Point - b.LastControl : b.Point;
                    ok = P(ref t, o, out var te);
                    if (ok) b.QuadTo(r1, te);
                    break;
                }
                case 'A':
                    if (t.TryNumber(out float rx) && t.TryNumber(out float ry) && t.TryNumber(out float rot)
                        && t.TryFlag(out bool large) && t.TryFlag(out bool sweep) && P(ref t, o, out var ae))
                        b.ArcTo(rx, ry, rot, large, sweep, ae);
                    else ok = false;
                    break;
                case 'Z':
                    b.Close();
                    break;
                default:
                    return; // unknown command: stop (like browsers do)
            }
            if (!ok) return;
            b.LastCommand = up;
            if (up == 'Z') cmd = '\0';
        }
    }

    private static bool P(ref PathTokenizer t, Vector2 origin, out Vector2 p)
    {
        if (t.TryNumber(out float x) && t.TryNumber(out float y)) { p = origin + new Vector2(x, y); return true; }
        p = default;
        return false;
    }
}

internal static class TransformParser
{
    /// <summary>Parse an SVG transform list into a matrix (applied right-to-left as in SVG).</summary>
    public static Matrix3x2 Parse(string s)
    {
        Matrix3x2 result = Matrix3x2.Identity;
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
            int n0 = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            string name = s.Substring(n0, i - n0);
            int open = s.IndexOf('(', i), close = open < 0 ? -1 : s.IndexOf(')', open);
            if (open < 0 || close < 0) break;
            var a = new List<float>();
            var t = new PathTokenizer(s.AsSpan(open + 1, close - open - 1));
            while (t.TryNumber(out float f)) a.Add(f);
            i = close + 1;

            Matrix3x2 m = Matrix3x2.Identity;
            switch (name)
            {
                case "matrix" when a.Count == 6: m = new Matrix3x2(a[0], a[1], a[2], a[3], a[4], a[5]); break;
                case "translate": m = Matrix3x2.CreateTranslation(a.Count > 0 ? a[0] : 0, a.Count > 1 ? a[1] : 0); break;
                case "scale": m = Matrix3x2.CreateScale(a.Count > 0 ? a[0] : 1, a.Count > 1 ? a[1] : (a.Count > 0 ? a[0] : 1)); break;
                case "rotate":
                {
                    float rad = (a.Count > 0 ? a[0] : 0) * MathF.PI / 180f;
                    m = a.Count >= 3 ? Matrix3x2.CreateRotation(rad, new Vector2(a[1], a[2])) : Matrix3x2.CreateRotation(rad);
                    break;
                }
                case "skewX": m = Matrix3x2.CreateSkew((a.Count > 0 ? a[0] : 0) * MathF.PI / 180f, 0); break;
                case "skewY": m = Matrix3x2.CreateSkew(0, (a.Count > 0 ? a[0] : 0) * MathF.PI / 180f); break;
            }
            // "A B" means apply B first, then A (row-vector convention: m * result).
            result = m * result;
        }
        return result;
    }
}
