using System.Numerics;
using Riateu.Graphics;

namespace Cosmiria;

/// <summary>
/// CSS transitions. Runs while styles are resolved (before layout), so any animated property,
/// size included, is laid out and painted at its in-between value. Per element (stable id) it
/// remembers the last target; when the resolved style's target changes it animates from the value
/// currently shown, and requests a rebuild every frame until done.
/// </summary>
internal sealed class TransitionEngine
{
    private struct Values
    {
        public Vector4 Bg, Border, Text; // premultiplied
        public float Opacity, BorderWidth;
        public Vector4 Radius;
        public float Tx, Ty, Sx, Sy, Rot;
        public bool TxPercent, TyPercent;
        public float W, H; // NaN unless pixels

        public readonly bool SameAs(in Values o) =>
            Bg == o.Bg && Border == o.Border && Text == o.Text && Opacity == o.Opacity && BorderWidth == o.BorderWidth &&
            Radius == o.Radius && Tx == o.Tx && Ty == o.Ty && Sx == o.Sx && Sy == o.Sy && Rot == o.Rot &&
            TxPercent == o.TxPercent && TyPercent == o.TyPercent && Same(W, o.W) && Same(H, o.H);

        private static bool Same(float a, float b) => a == b || (float.IsNaN(a) && float.IsNaN(b));
    }

    private struct State
    {
        public Values From, To;
        public double Start;
        public float Duration;
        public Easing Easing;
        public TransitionParts Parts;
        public int Build;
    }

    private readonly Dictionary<int, State> states = new();
    private readonly List<int> dead = new();

    /// <summary>True while some transition is still running (rebuild next frame).</summary>
    public bool Animating { get; private set; }

    public void BeginBuild() => Animating = false;

    /// <summary>Animate the resolved style <paramref name="s"/> of element <paramref name="id"/> in place.</summary>
    public void Apply(int id, ref Style s, double now, int build)
    {
        Values target = Snapshot(s);
        ref State st = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(states, id, out bool exists);
        if (!exists)
        {
            // First appearance: no animation (as in CSS).
            st.From = st.To = target;
            st.Start = double.NegativeInfinity;
        }
        else if (!st.To.SameAs(target))
        {
            st.From = Evaluate(st, now, out _); // continue from what's on screen
            st.To = target;
            st.Start = now + s.TransitionDelay;
        }
        st.Duration = s.EffectiveDuration;
        st.Easing = s.TransitionEasing;
        st.Parts = s.TransitionProperties;
        st.Build = build;

        Values v = Evaluate(st, now, out bool running);
        if (running) Animating = true;
        Write(ref s, v, st.Parts);
    }

    public void Sweep(int build)
    {
        foreach (var pair in states)
            if (pair.Value.Build != build) dead.Add(pair.Key);
        foreach (int id in dead) states.Remove(id);
        dead.Clear();
    }

    private static Values Evaluate(in State st, double now, out bool running)
    {
        float t = st.Duration <= 0 ? 1f : (float)Math.Clamp((now - st.Start) / st.Duration, 0, 1);
        running = t < 1f;
        if (!running) return st.To;
        float e = Ease(st.Easing, t);
        Values a = st.From, b = st.To, v = b;
        if ((st.Parts & TransitionParts.Colors) != 0)
        {
            v.Bg = Vector4.Lerp(a.Bg, b.Bg, e);
            v.Border = Vector4.Lerp(a.Border, b.Border, e);
            v.Text = Vector4.Lerp(a.Text, b.Text, e);
        }
        if ((st.Parts & TransitionParts.Opacity) != 0) v.Opacity = Lerp(a.Opacity, b.Opacity, e);
        if ((st.Parts & TransitionParts.Transform) != 0)
        {
            if (a.TxPercent == b.TxPercent) v.Tx = Lerp(a.Tx, b.Tx, e);
            if (a.TyPercent == b.TyPercent) v.Ty = Lerp(a.Ty, b.Ty, e);
            v.Sx = Lerp(a.Sx, b.Sx, e);
            v.Sy = Lerp(a.Sy, b.Sy, e);
            v.Rot = Lerp(a.Rot, b.Rot, e);
        }
        if ((st.Parts & TransitionParts.Size) != 0)
        {
            v.BorderWidth = Lerp(a.BorderWidth, b.BorderWidth, e);
            v.Radius = Vector4.Lerp(a.Radius, b.Radius, e);
            if (!float.IsNaN(a.W) && !float.IsNaN(b.W)) v.W = Lerp(a.W, b.W, e);
            if (!float.IsNaN(a.H) && !float.IsNaN(b.H)) v.H = Lerp(a.H, b.H, e);
        }
        return v;
    }

    private static Values Snapshot(in Style s)
    {
        const float MaxRadius = 4096; // RoundedFull is float.MaxValue: keep it interpolable
        var v = new Values
        {
            Bg = s.Has(StyleFlags.Background) ? Premultiply(s.Background) : Vector4.Zero,
            Border = s.Has(StyleFlags.BorderColor) ? Premultiply(s.BorderColor) : Vector4.Zero,
            Text = Premultiply(s.TextColor),
            Opacity = s.EffectiveOpacity,
            BorderWidth = s.BorderWidth,
            Radius = new Vector4(MathF.Min(s.Radius.TopLeft, MaxRadius), MathF.Min(s.Radius.TopRight, MaxRadius),
                MathF.Min(s.Radius.BottomRight, MaxRadius), MathF.Min(s.Radius.BottomLeft, MaxRadius)),
            Sx = 1, Sy = 1,
            W = s.Width.Unit == LengthUnit.Px ? s.Width.Value : float.NaN,
            H = s.Height.Unit == LengthUnit.Px ? s.Height.Value : float.NaN,
        };
        if (s.Has(StyleFlags.Transform))
        {
            if (s.HasTransform(TransformParts.TranslateX)) { v.Tx = s.TranslateX.Value; v.TxPercent = s.TranslateX.Unit == LengthUnit.Percent; }
            if (s.HasTransform(TransformParts.TranslateY)) { v.Ty = s.TranslateY.Value; v.TyPercent = s.TranslateY.Unit == LengthUnit.Percent; }
            if (s.HasTransform(TransformParts.ScaleX)) v.Sx = s.ScaleX;
            if (s.HasTransform(TransformParts.ScaleY)) v.Sy = s.ScaleY;
            if (s.HasTransform(TransformParts.Rotate)) v.Rot = s.RotateDegrees;
        }
        return v;
    }

    private static void Write(ref Style s, in Values v, TransitionParts parts)
    {
        if ((parts & TransitionParts.Colors) != 0)
        {
            if (v.Bg.W > 0 || s.Has(StyleFlags.Background)) { s.Background = Unpremultiply(v.Bg); s.Set |= StyleFlags.Background; }
            if (v.Border.W > 0 || s.Has(StyleFlags.BorderColor)) { s.BorderColor = Unpremultiply(v.Border); s.Set |= StyleFlags.BorderColor; }
            s.TextColor = Unpremultiply(v.Text);
        }
        if ((parts & TransitionParts.Opacity) != 0 && (v.Opacity < 1 || s.Has(StyleFlags.Opacity)))
        {
            s.OpacityValue = v.Opacity;
            s.Set |= StyleFlags.Opacity;
        }
        if ((parts & TransitionParts.Transform) != 0 &&
            (v.Tx != 0 || v.Ty != 0 || v.Sx != 1 || v.Sy != 1 || v.Rot != 0 || s.Has(StyleFlags.Transform)))
        {
            if (!s.Has(StyleFlags.Transform)) { s.OriginX = s.OriginY = 0.5f; }
            s.TranslateX = v.TxPercent ? Length.Percent(v.Tx) : Length.Px(v.Tx);
            s.TranslateY = v.TyPercent ? Length.Percent(v.Ty) : Length.Px(v.Ty);
            s.ScaleX = v.Sx; s.ScaleY = v.Sy; s.RotateDegrees = v.Rot;
            s.TransformMask |= TransformParts.TranslateX | TransformParts.TranslateY | TransformParts.ScaleX | TransformParts.ScaleY | TransformParts.Rotate;
            s.Set |= StyleFlags.Transform;
        }
        if ((parts & TransitionParts.Size) != 0)
        {
            s.BorderWidth = v.BorderWidth;
            s.Radius = new Corners(v.Radius.X, v.Radius.Y, v.Radius.Z, v.Radius.W);
            if (!float.IsNaN(v.W)) s.Width = Length.Px(v.W);
            if (!float.IsNaN(v.H)) s.Height = Length.Px(v.H);
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0, 255);

    private static Vector4 Premultiply(Color c)
    {
        Vector4 v = c.ToVector4();
        return new Vector4(v.X * v.W, v.Y * v.W, v.Z * v.W, v.W);
    }

    private static Color Unpremultiply(Vector4 p)
    {
        if (p.W <= 0.0001f) return default; // fully transparent
        // Round and clamp to bytes (the float constructor truncates), so a finished transition
        // lands exactly on its target color despite the unpremultiply division.
        return new Color(ToByte(p.X / p.W), ToByte(p.Y / p.W), ToByte(p.Z / p.W), ToByte(p.W));
    }

    /// <summary>CSS cubic-bezier presets, solved for x with Newton's method.</summary>
    public static float Ease(Easing easing, float t) => easing switch
    {
        Easing.Linear => t,
        Easing.In => Bezier(0.4f, 0f, 1f, 1f, t),
        Easing.Out => Bezier(0f, 0f, 0.2f, 1f, t),
        _ => Bezier(0.4f, 0f, 0.2f, 1f, t),
    };

    private static float Bezier(float x1, float y1, float x2, float y2, float x)
    {
        float u = x;
        for (int i = 0; i < 8; i++)
        {
            float fx = B(x1, x2, u) - x;
            float d = DB(x1, x2, u);
            if (MathF.Abs(fx) < 1e-5f || MathF.Abs(d) < 1e-6f) break;
            u = Math.Clamp(u - fx / d, 0f, 1f);
        }
        return B(y1, y2, u);
    }

    private static float B(float p1, float p2, float u) => 3 * (1 - u) * (1 - u) * u * p1 + 3 * (1 - u) * u * u * p2 + u * u * u;
    private static float DB(float p1, float p2, float u) => 3 * (1 - u) * (1 - u) * p1 + 6 * (1 - u) * u * (p2 - p1) + 3 * u * u * (1 - p2);
}
