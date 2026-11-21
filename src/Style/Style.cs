using System.Runtime.CompilerServices;
using Riateu.Graphics;

namespace Cosmiria;

public enum Display : byte { Flex, None, Grid }

public enum GridTrackKind : byte { Fr, Px, Auto }

/// <summary>A grid column or row size: a fixed size, a share of the free space (fr), or its content (auto).</summary>
public readonly struct GridTrack(GridTrackKind kind, float value)
{
    public readonly GridTrackKind Kind = kind;
    public readonly float Value = value;
    public static GridTrack Fr(float fr = 1) => new(GridTrackKind.Fr, fr);
    public static GridTrack Px(float px) => new(GridTrackKind.Px, px);
    public static readonly GridTrack Auto = new(GridTrackKind.Auto, 0);
}
public enum FlexDirection : byte { Row, Column }
public enum Justify : byte { Start, Center, End, SpaceBetween, SpaceAround, SpaceEvenly }
public enum Align : byte { Auto, Start, Center, End, Stretch }
/// <summary>Relative: in flow. Absolute: out of flow, placed in the parent. Fixed: out of flow, placed in the window.</summary>
public enum PositionMode : byte { Relative, Absolute, Fixed }
public enum Overflow : byte { Visible, Hidden, Scroll }
public enum WrapMode : byte { NoWrap, Wrap }
/// <summary>How text breaks into lines. Inherited; wrapping is the default.</summary>
public enum TextWrapMode : byte { Wrap, NoWrap }
/// <summary>Horizontal alignment of each text line inside its box. Inherited.</summary>
public enum TextAlign : byte { Left, Center, Right }

public enum LengthUnit : byte { Auto, Px, Percent }

/// <summary>Properties a transition animates (Tailwind's transition-* groups).</summary>
[Flags]
public enum TransitionParts : byte
{
    None = 0,
    /// <summary>Background, border and text colors.</summary>
    Colors = 1 << 0,
    Opacity = 1 << 1,
    /// <summary>Translate, scale and rotate.</summary>
    Transform = 1 << 2,
    /// <summary>Pixel widths and heights (animates layout), border width and corner radius.</summary>
    Size = 1 << 3,
    /// <summary>Tailwind's <c>transition</c>: colors, opacity and transform.</summary>
    Default = Colors | Opacity | Transform,
    All = Colors | Opacity | Transform | Size,
}

/// <summary>Background gradient shape.</summary>
public enum GradientKind : byte { None, Linear, Radial }

/// <summary>Timing functions (CSS cubic-bezier presets, as in Tailwind).</summary>
public enum Easing : byte
{
    /// <summary>cubic-bezier(0.4, 0, 0.2, 1): Tailwind's default for transitions.</summary>
    Default,
    Linear,
    /// <summary>cubic-bezier(0.4, 0, 1, 1)</summary>
    In,
    /// <summary>cubic-bezier(0, 0, 0.2, 1)</summary>
    Out,
    /// <summary>cubic-bezier(0.4, 0, 0.2, 1)</summary>
    InOut,
}

/// <summary>Which transform components a style sets (they merge one by one, like insets).</summary>
[Flags]
public enum TransformParts : byte
{
    None = 0,
    TranslateX = 1 << 0,
    TranslateY = 1 << 1,
    ScaleX = 1 << 2,
    ScaleY = 1 << 3,
    Rotate = 1 << 4,
    Origin = 1 << 5,
}

/// <summary>A size value: auto, pixels, or a percentage of the parent's content box.</summary>
public readonly struct Length
{
    public readonly float Value;
    public readonly LengthUnit Unit;

    public Length(float value, LengthUnit unit) { Value = value; Unit = unit; }

    public static Length Auto => default;
    public static Length Full => new Length(100, LengthUnit.Percent);
    public static Length Px(float v) => new Length(v, LengthUnit.Px);
    public static Length Percent(float v) => new Length(v, LengthUnit.Percent);

    public static implicit operator Length(float px) => new Length(px, LengthUnit.Px);

    public bool IsAuto => Unit == LengthUnit.Auto;

    /// <summary>Resolves against a parent size; returns <paramref name="fallback"/> for Auto
    /// (or for Percent when the parent size is unknown, i.e. NaN).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Resolve(float parent, float fallback) => Unit switch
    {
        LengthUnit.Px => Value,
        LengthUnit.Percent when !float.IsNaN(parent) => parent * Value * 0.01f,
        _ => fallback
    };
}

public struct Edges
{
    public float Left, Top, Right, Bottom;
    public Edges(float all) { Left = Top = Right = Bottom = all; }
    public Edges(float x, float y) { Left = Right = x; Top = Bottom = y; }
    public Edges(float left, float top, float right, float bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
    public readonly float Horizontal => Left + Right;
    public readonly float Vertical => Top + Bottom;
}

public struct Corners
{
    public float TopLeft, TopRight, BottomRight, BottomLeft;
    public Corners(float all) { TopLeft = TopRight = BottomRight = BottomLeft = all; }
    public Corners(float tl, float tr, float br, float bl) { TopLeft = tl; TopRight = tr; BottomRight = br; BottomLeft = bl; }
}

[Flags]
public enum StyleFlags : ulong
{
    None = 0,
    Display = 1 << 0,
    Direction = 1 << 1,
    Justify = 1 << 2,
    AlignItems = 1 << 3,
    AlignSelf = 1 << 4,
    ColumnGap = 1 << 5,
    Padding = 1 << 6,
    Margin = 1 << 7,
    Width = 1 << 8,
    Height = 1 << 9,
    MinWidth = 1 << 10,
    MinHeight = 1 << 11,
    MaxWidth = 1 << 12,
    MaxHeight = 1 << 13,
    Grow = 1 << 14,
    Shrink = 1 << 15,
    Position = 1 << 16,
    Inset = 1 << 17,
    Overflow = 1 << 18,
    Background = 1 << 19,
    BorderColor = 1 << 20,
    BorderWidth = 1 << 21,
    Radius = 1 << 22,
    TextColor = 1 << 23,
    Font = 1 << 24,
    TextSize = 1 << 25,
    RowGap = 1 << 26,
    FlexWrap = 1 << 27,
    TextWrap = 1 << 28,
    TextAlign = 1 << 29,
    Overlay = 1u << 30,
    /// <summary>Any transform component; which ones is in <see cref="Style.TransformMask"/>.</summary>
    Transform = 1u << 31,
    Opacity = 1ul << 32,
    Transition = 1ul << 33,
    Gradient = 1ul << 34,
    /// <summary>Box shadow; which parts are in <see cref="Style.ShadowMask"/>.</summary>
    Shadow = 1ul << 35,
    Blur = 1ul << 36,
    BackdropBlur = 1ul << 37,
    GridColumns = 1ul << 38,
    GridRows = 1ul << 39,
    /// <summary>Grid item placement: which parts are non-zero in ColStart/ColSpan/RowStart/RowSpan.</summary>
    GridPlacement = 1ul << 40,

    Inherited = TextColor | Font | TextSize | TextWrap | TextAlign,
}

/// <summary>
/// A value-type style description with a fluent, GPUI-like builder API.
/// Every builder method returns a modified copy, so styles can be stored and reused freely.
/// Only fields that were explicitly set (tracked by <see cref="Set"/>) take part in
/// <see cref="Merge"/>, which is how hover/active styles layer over the base style.
/// </summary>
public struct Style
{
    public StyleFlags Set;

    public Display Display;
    public FlexDirection Direction;
    public Justify Justify;
    public Align AlignItems;
    public Align AlignSelf;
    public PositionMode Position;
    public Overflow Overflow;

    /// <summary>Horizontal space between items (CSS column-gap).</summary>
    public float ColumnGap;
    /// <summary>Vertical space between items / wrapped lines (CSS row-gap).</summary>
    public float RowGap;
    public WrapMode Wrapping;
    public Edges PaddingEdges;
    public Edges MarginEdges;
    /// <summary>Absolute offsets; NaN means "not set" for that side.</summary>
    public Edges InsetEdges;
    /// <summary>Bit per side (left, top, right, bottom): that inset is a percentage of the parent.</summary>
    public byte InsetPercentMask;
    /// <summary>Painted above everything else, hit-tested first, not clipped by ancestors.</summary>
    public bool IsOverlay;

    public Length Width, Height, MinWidth, MinHeight, MaxWidth, MaxHeight;
    public float GrowFactor;
    public float ShrinkFactor;

    public Color Background;
    public Color BorderColor;
    public float BorderWidth;
    public Corners Radius;

    public Color TextColor;
    public int FontId;
    public float TextSize;
    public TextWrapMode TextWrapping;
    public TextAlign TextAlignment;

    // ---- Transform (paint-time only, like CSS: layout and siblings don't move)
    /// <summary>Translation in pixels, or a percentage of the element's own size.</summary>
    public Length TranslateX, TranslateY;
    /// <summary>Scale factors; 1 is unscaled. Only meaningful where <see cref="TransformMask"/> says so.</summary>
    public float ScaleX, ScaleY;
    /// <summary>Clockwise rotation in degrees.</summary>
    public float RotateDegrees;
    /// <summary>Transform origin as a fraction of the element's size (0.5, 0.5 is the center).</summary>
    public float OriginX, OriginY;
    public TransformParts TransformMask;

    // ---- Gradient background (drawn instead of Background when set)
    public GradientKind Gradient;
    /// <summary>Linear direction in degrees, CSS convention: 0 = to top, 90 = to right, 180 = to bottom.</summary>
    public float GradientAngle;
    public Color GradientFrom, GradientVia, GradientTo;
    /// <summary>Bit 0: kind/angle, 1: from, 2: via, 3: to (merge per part).</summary>
    public byte GradientMask;

    /// <summary>Opacity of the element and its children (0..1; 1 when not set).</summary>
    public float OpacityValue;

    // ---- Box shadow (CSS box-shadow, outer): a blurred copy of the box under it, offset by
    // (ShadowX, ShadowY), grown by ShadowSpread, and only visible outside the box itself.
    public float ShadowX, ShadowY, ShadowBlur, ShadowSpread;
    public Color ShadowColor;
    /// <summary>Bit 0: geometry set, bit 1: color set explicitly (merge per part, like Tailwind's shadow color).</summary>
    public byte ShadowMask;

    /// <summary>Gaussian blur of the element and its children (CSS <c>filter: blur()</c>), as the standard deviation.</summary>
    public float BlurRadius;
    /// <summary>Gaussian blur of what is behind the element (CSS <c>backdrop-filter: blur()</c>).</summary>
    public float BackdropBlurRadius;

    // ---- Grid container: N equal (1fr) tracks, or explicit Tracks when set.
    public int GridColumnCount, GridRowCount;
    /// <summary>Auto-fill: as many 1fr columns as fit at this minimum width (0 = off).</summary>
    public float GridColumnMin;
    /// <summary>With <see cref="GridColumnMin"/>: auto-fit, empty columns collapse so the items stretch to the full width.</summary>
    public bool GridColumnFit;
    public GridTrack[] GridColumnTracks, GridRowTracks;
    // ---- Grid item: 1-based start line (0 = auto-placed) and span (0 = 1).
    public short ColStart, ColSpan, RowStart, RowSpan;

    /// <summary>A shadow will be drawn.</summary>
    public readonly bool HasShadow => (Set & StyleFlags.Shadow) != 0 && (ShadowMask & 1) != 0 && ShadowColor.A > 0;

    // ---- Transition (CSS transition): animate these properties when their value changes.
    public TransitionParts TransitionProperties;
    /// <summary>Seconds; 0.15 (Tailwind's default) when not set.</summary>
    public float TransitionDuration;
    public float TransitionDelay;
    public Easing TransitionEasing;
    /// <summary>Bit 0: duration set, bit 1: delay set, bit 2: easing set, bit 3: properties set (merge per field).</summary>
    public byte TransitionMask;

    public readonly bool Has(StyleFlags flag) => (Set & flag) != 0;
    public readonly bool HasTransform(TransformParts part) => (TransformMask & part) != 0;

    /// <summary>A copy of this style with <paramref name="o"/> merged on top.</summary>
    public readonly Style MergedWith(in Style o) { Style s = this; s.Merge(o); return s; }

    /// <summary>Overlay every field set in <paramref name="o"/> onto this style.</summary>
    public void Merge(in Style o)
    {
        StyleFlags s = o.Set;
        if (s == StyleFlags.None) return;
        if ((s & StyleFlags.Display) != 0) Display = o.Display;
        if ((s & StyleFlags.Direction) != 0) Direction = o.Direction;
        if ((s & StyleFlags.Justify) != 0) Justify = o.Justify;
        if ((s & StyleFlags.AlignItems) != 0) AlignItems = o.AlignItems;
        if ((s & StyleFlags.AlignSelf) != 0) AlignSelf = o.AlignSelf;
        if ((s & StyleFlags.ColumnGap) != 0) ColumnGap = o.ColumnGap;
        if ((s & StyleFlags.RowGap) != 0) RowGap = o.RowGap;
        if ((s & StyleFlags.FlexWrap) != 0) Wrapping = o.Wrapping;
        if ((s & StyleFlags.TextWrap) != 0) TextWrapping = o.TextWrapping;
        if ((s & StyleFlags.TextAlign) != 0) TextAlignment = o.TextAlignment;
        if ((s & StyleFlags.Padding) != 0) PaddingEdges = o.PaddingEdges;
        if ((s & StyleFlags.Margin) != 0) MarginEdges = o.MarginEdges;
        if ((s & StyleFlags.Width) != 0) Width = o.Width;
        if ((s & StyleFlags.Height) != 0) Height = o.Height;
        if ((s & StyleFlags.MinWidth) != 0) MinWidth = o.MinWidth;
        if ((s & StyleFlags.MinHeight) != 0) MinHeight = o.MinHeight;
        if ((s & StyleFlags.MaxWidth) != 0) MaxWidth = o.MaxWidth;
        if ((s & StyleFlags.MaxHeight) != 0) MaxHeight = o.MaxHeight;
        if ((s & StyleFlags.Grow) != 0) GrowFactor = o.GrowFactor;
        if ((s & StyleFlags.Shrink) != 0) ShrinkFactor = o.ShrinkFactor;
        if ((s & StyleFlags.Position) != 0) Position = o.Position;
        if ((s & StyleFlags.Inset) != 0) MergeInsets(o);
        if ((s & StyleFlags.Overlay) != 0) IsOverlay = o.IsOverlay;
        if ((s & StyleFlags.Overflow) != 0) Overflow = o.Overflow;
        if ((s & StyleFlags.Background) != 0) Background = o.Background;
        if ((s & StyleFlags.BorderColor) != 0) BorderColor = o.BorderColor;
        if ((s & StyleFlags.BorderWidth) != 0) BorderWidth = o.BorderWidth;
        if ((s & StyleFlags.Radius) != 0) Radius = o.Radius;
        if ((s & StyleFlags.TextColor) != 0) TextColor = o.TextColor;
        if ((s & StyleFlags.Font) != 0) FontId = o.FontId;
        if ((s & StyleFlags.TextSize) != 0) TextSize = o.TextSize;
        if ((s & StyleFlags.Transform) != 0) MergeTransform(o);
        if ((s & StyleFlags.Opacity) != 0) OpacityValue = o.OpacityValue;
        if ((s & StyleFlags.Gradient) != 0)
        {
            if ((o.GradientMask & 1) != 0) { Gradient = o.Gradient; GradientAngle = o.GradientAngle; }
            if ((o.GradientMask & 2) != 0) GradientFrom = o.GradientFrom;
            if ((o.GradientMask & 4) != 0) GradientVia = o.GradientVia;
            if ((o.GradientMask & 8) != 0) GradientTo = o.GradientTo;
            GradientMask |= o.GradientMask;
        }
        if ((s & StyleFlags.Shadow) != 0)
        {
            if ((o.ShadowMask & 1) != 0)
            {
                ShadowX = o.ShadowX; ShadowY = o.ShadowY; ShadowBlur = o.ShadowBlur; ShadowSpread = o.ShadowSpread;
                if ((ShadowMask & 2) == 0) ShadowColor = o.ShadowColor; // a preset's default color, unless one was chosen
            }
            if ((o.ShadowMask & 2) != 0) ShadowColor = o.ShadowColor;
            ShadowMask |= o.ShadowMask;
        }
        if ((s & StyleFlags.GridColumns) != 0) { GridColumnCount = o.GridColumnCount; GridColumnTracks = o.GridColumnTracks; GridColumnMin = o.GridColumnMin; GridColumnFit = o.GridColumnFit; }
        if ((s & StyleFlags.GridRows) != 0) { GridRowCount = o.GridRowCount; GridRowTracks = o.GridRowTracks; }
        if ((s & StyleFlags.GridPlacement) != 0)
        {
            if (o.ColStart != 0) ColStart = o.ColStart;
            if (o.ColSpan != 0) ColSpan = o.ColSpan;
            if (o.RowStart != 0) RowStart = o.RowStart;
            if (o.RowSpan != 0) RowSpan = o.RowSpan;
        }
        if ((s & StyleFlags.Blur) != 0) BlurRadius = o.BlurRadius;
        if ((s & StyleFlags.BackdropBlur) != 0) BackdropBlurRadius = o.BackdropBlurRadius;
        if ((s & StyleFlags.Transition) != 0)
        {
            if ((o.TransitionMask & 1) != 0) TransitionDuration = o.TransitionDuration;
            if ((o.TransitionMask & 2) != 0) TransitionDelay = o.TransitionDelay;
            if ((o.TransitionMask & 4) != 0) TransitionEasing = o.TransitionEasing;
            if ((o.TransitionMask & 8) != 0) TransitionProperties = o.TransitionProperties;
            TransitionMask |= o.TransitionMask;
        }
        Set |= s;
    }

    private void MergeTransform(in Style o)
    {
        TransformParts m = o.TransformMask;
        if ((m & TransformParts.TranslateX) != 0) TranslateX = o.TranslateX;
        if ((m & TransformParts.TranslateY) != 0) TranslateY = o.TranslateY;
        if ((m & TransformParts.ScaleX) != 0) ScaleX = o.ScaleX;
        if ((m & TransformParts.ScaleY) != 0) ScaleY = o.ScaleY;
        if ((m & TransformParts.Rotate) != 0) RotateDegrees = o.RotateDegrees;
        if ((m & TransformParts.Origin) != 0) { OriginX = o.OriginX; OriginY = o.OriginY; }
        TransformMask |= m;
    }

    /// <summary>Insets merge per side: an overlay that only sets Left must keep the base's Top.</summary>
    private void MergeInsets(in Style o)
    {
        if (!Has(StyleFlags.Inset))
        {
            InsetEdges = new Edges(float.NaN);
            InsetPercentMask = 0;
        }
        Edges e = InsetEdges;
        byte mask = InsetPercentMask;
        if (!float.IsNaN(o.InsetEdges.Left)) { e.Left = o.InsetEdges.Left; mask = (byte)((mask & ~1) | (o.InsetPercentMask & 1)); }
        if (!float.IsNaN(o.InsetEdges.Top)) { e.Top = o.InsetEdges.Top; mask = (byte)((mask & ~2) | (o.InsetPercentMask & 2)); }
        if (!float.IsNaN(o.InsetEdges.Right)) { e.Right = o.InsetEdges.Right; mask = (byte)((mask & ~4) | (o.InsetPercentMask & 4)); }
        if (!float.IsNaN(o.InsetEdges.Bottom)) { e.Bottom = o.InsetEdges.Bottom; mask = (byte)((mask & ~8) | (o.InsetPercentMask & 8)); }
        InsetEdges = e;
        InsetPercentMask = mask;
    }

    /// <summary>Copy inheritable text properties from the parent where not set locally.</summary>
    internal void Inherit(in Style parent)
    {
        if ((Set & StyleFlags.TextColor) == 0) TextColor = parent.TextColor;
        if ((Set & StyleFlags.Font) == 0) FontId = parent.FontId;
        if ((Set & StyleFlags.TextSize) == 0) TextSize = parent.TextSize;
        if ((Set & StyleFlags.TextWrap) == 0) TextWrapping = parent.TextWrapping;
        if ((Set & StyleFlags.TextAlign) == 0) TextAlignment = parent.TextAlignment;
        Set |= StyleFlags.Inherited;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly Style With(StyleFlags f, out Style s) { s = this; s.Set |= f; return s; }

    private static Color Hex(uint rgba) => new Color(rgba);

    // ---- Display / direction -------------------------------------------------
    public readonly Style Flex() { With(StyleFlags.Display, out var s); s.Display = Display.Flex; return s; }
    public readonly Style Hidden() { With(StyleFlags.Display, out var s); s.Display = Display.None; return s; }
    public readonly Style FlexRow() { With(StyleFlags.Direction | StyleFlags.Display, out var s); s.Display = Display.Flex; s.Direction = FlexDirection.Row; return s; }
    public readonly Style FlexCol() { With(StyleFlags.Direction | StyleFlags.Display, out var s); s.Display = Display.Flex; s.Direction = FlexDirection.Column; return s; }

    // ---- Alignment -----------------------------------------------------------
    public readonly Style JustifyStart() => WithJustify(Justify.Start);
    public readonly Style JustifyCenter() => WithJustify(Justify.Center);
    public readonly Style JustifyEnd() => WithJustify(Justify.End);
    public readonly Style JustifyBetween() => WithJustify(Justify.SpaceBetween);
    public readonly Style JustifyAround() => WithJustify(Justify.SpaceAround);
    public readonly Style JustifyEvenly() => WithJustify(Justify.SpaceEvenly);
    private readonly Style WithJustify(Justify j) { With(StyleFlags.Justify, out var s); s.Justify = j; return s; }

    public readonly Style ItemsStart() => WithItems(Align.Start);
    public readonly Style ItemsCenter() => WithItems(Align.Center);
    public readonly Style ItemsEnd() => WithItems(Align.End);
    public readonly Style ItemsStretch() => WithItems(Align.Stretch);
    private readonly Style WithItems(Align a) { With(StyleFlags.AlignItems, out var s); s.AlignItems = a; return s; }

    public readonly Style SelfStart() => WithSelf(Align.Start);
    public readonly Style SelfCenter() => WithSelf(Align.Center);
    public readonly Style SelfEnd() => WithSelf(Align.End);
    public readonly Style SelfStretch() => WithSelf(Align.Stretch);
    private readonly Style WithSelf(Align a) { With(StyleFlags.AlignSelf, out var s); s.AlignSelf = a; return s; }

    // ---- Spacing -------------------------------------------------------------
    /// <summary>Space between items on both axes (and between wrapped lines).</summary>
    public readonly Style Gap(float gap) => Gap(gap, gap);
    public readonly Style Gap(float x, float y) { With(StyleFlags.ColumnGap | StyleFlags.RowGap, out var s); s.ColumnGap = x; s.RowGap = y; return s; }
    public readonly Style GapX(float x) { With(StyleFlags.ColumnGap, out var s); s.ColumnGap = x; return s; }
    public readonly Style GapY(float y) { With(StyleFlags.RowGap, out var s); s.RowGap = y; return s; }

    /// <summary>Let items wrap onto new lines when they don't fit (CSS flex-wrap: wrap).</summary>
    /// <summary>CSS grid: children fill the cells row by row; see <see cref="GridCols(int)"/> and <see cref="ColSpanOf"/>.</summary>
    public readonly Style Grid() { With(StyleFlags.Display, out var s); s.Display = Display.Grid; return s; }
    /// <summary><paramref name="count"/> equal columns (Tailwind's grid-cols-N: each minmax(0, 1fr)).</summary>
    public readonly Style GridCols(int count) { With(StyleFlags.GridColumns | StyleFlags.Display, out var s); s.Display = Display.Grid; s.GridColumnCount = Math.Max(1, count); s.GridColumnTracks = null; s.GridColumnMin = 0; s.GridColumnFit = false; return s; }
    /// <summary>Columns of the given sizes. Keep the array (e.g. static readonly): it isn't copied.</summary>
    public readonly Style GridCols(params GridTrack[] tracks) { With(StyleFlags.GridColumns | StyleFlags.Display, out var s); s.Display = Display.Grid; s.GridColumnCount = tracks.Length; s.GridColumnTracks = tracks; s.GridColumnMin = 0; s.GridColumnFit = false; return s; }
    /// <summary>
    /// As many equal columns as fit, each at least <paramref name="minWidth"/> wide, stretched to fill
    /// the row (CSS <c>repeat(auto-fill, minmax(minWidth, 1fr))</c>). Without a known width: one column.
    /// </summary>
    public readonly Style GridColsFill(float minWidth) { With(StyleFlags.GridColumns | StyleFlags.Display, out var s); s.Display = Display.Grid; s.GridColumnCount = 0; s.GridColumnTracks = null; s.GridColumnMin = MathF.Max(1, minWidth); s.GridColumnFit = false; return s; }

    /// <summary>
    /// Like <see cref="GridColsFill"/>, but columns left empty collapse, so fewer items than fit
    /// stretch across the whole row (CSS <c>repeat(auto-fit, minmax(minWidth, 1fr))</c>).
    /// </summary>
    public readonly Style GridColsFit(float minWidth) { Style s = GridColsFill(minWidth); s.GridColumnFit = true; return s; }
    /// <summary>At least <paramref name="count"/> equal rows; more are added (sized to content) as needed.</summary>
    public readonly Style GridRows(int count) { With(StyleFlags.GridRows, out var s); s.GridRowCount = Math.Max(0, count); s.GridRowTracks = null; return s; }
    /// <inheritdoc cref="GridCols(GridTrack[])"/>
    public readonly Style GridRows(params GridTrack[] tracks) { With(StyleFlags.GridRows, out var s); s.GridRowCount = tracks.Length; s.GridRowTracks = tracks; return s; }
    /// <summary>Cover <paramref name="span"/> columns (<see cref="short.MaxValue"/>: the whole row).</summary>
    public readonly Style ColSpanOf(int span) { With(StyleFlags.GridPlacement, out var s); s.ColSpan = (short)Math.Clamp(span, 1, short.MaxValue); return s; }
    public readonly Style RowSpanOf(int span) { With(StyleFlags.GridPlacement, out var s); s.RowSpan = (short)Math.Clamp(span, 1, short.MaxValue); return s; }
    /// <summary>Start at column line <paramref name="line"/> (1 is the first column).</summary>
    public readonly Style ColStartOf(int line) { With(StyleFlags.GridPlacement, out var s); s.ColStart = (short)Math.Clamp(line, 1, short.MaxValue); return s; }
    public readonly Style RowStartOf(int line) { With(StyleFlags.GridPlacement, out var s); s.RowStart = (short)Math.Clamp(line, 1, short.MaxValue); return s; }

    public readonly Style FlexWrap() { With(StyleFlags.FlexWrap, out var s); s.Wrapping = WrapMode.Wrap; return s; }
    public readonly Style FlexNoWrap() { With(StyleFlags.FlexWrap, out var s); s.Wrapping = WrapMode.NoWrap; return s; }

    /// <summary>Wrap text at word boundaries to fit the available width (default, inherited).</summary>
    public readonly Style TextWrap() { With(StyleFlags.TextWrap, out var s); s.TextWrapping = TextWrapMode.Wrap; return s; }
    /// <summary>Keep text on one line (only explicit '\n' breaks), like CSS white-space: nowrap.</summary>
    public readonly Style TextLeft() { With(StyleFlags.TextAlign, out var s); s.TextAlignment = TextAlign.Left; return s; }
    public readonly Style TextCenter() { With(StyleFlags.TextAlign, out var s); s.TextAlignment = TextAlign.Center; return s; }
    public readonly Style TextRight() { With(StyleFlags.TextAlign, out var s); s.TextAlignment = TextAlign.Right; return s; }

    public readonly Style NoWrap() { With(StyleFlags.TextWrap, out var s); s.TextWrapping = TextWrapMode.NoWrap; return s; }

    public readonly Style Padding(float all) => WithPadding(new Edges(all));
    public readonly Style Padding(float x, float y) => WithPadding(new Edges(x, y));
    public readonly Style Padding(float left, float top, float right, float bottom) => WithPadding(new Edges(left, top, right, bottom));
    public readonly Style P(float all) => Padding(all);
    public readonly Style Px(float x) { var p = PaddingEdges; p.Left = p.Right = x; return WithPadding(p); }
    public readonly Style Py(float y) { var p = PaddingEdges; p.Top = p.Bottom = y; return WithPadding(p); }
    public readonly Style Pt(float v) { var p = PaddingEdges; p.Top = v; return WithPadding(p); }
    public readonly Style Pb(float v) { var p = PaddingEdges; p.Bottom = v; return WithPadding(p); }
    public readonly Style Pl(float v) { var p = PaddingEdges; p.Left = v; return WithPadding(p); }
    public readonly Style Pr(float v) { var p = PaddingEdges; p.Right = v; return WithPadding(p); }
    private readonly Style WithPadding(Edges e) { With(StyleFlags.Padding, out var s); s.PaddingEdges = e; return s; }

    public readonly Style Margin(float all) => WithMargin(new Edges(all));
    public readonly Style Margin(float x, float y) => WithMargin(new Edges(x, y));
    public readonly Style Margin(float left, float top, float right, float bottom) => WithMargin(new Edges(left, top, right, bottom));
    public readonly Style M(float all) => Margin(all);
    public readonly Style Mx(float x) { var m = MarginEdges; m.Left = m.Right = x; return WithMargin(m); }
    public readonly Style My(float y) { var m = MarginEdges; m.Top = m.Bottom = y; return WithMargin(m); }
    public readonly Style Mt(float v) { var m = MarginEdges; m.Top = v; return WithMargin(m); }
    public readonly Style Mb(float v) { var m = MarginEdges; m.Bottom = v; return WithMargin(m); }
    public readonly Style Ml(float v) { var m = MarginEdges; m.Left = v; return WithMargin(m); }
    public readonly Style Mr(float v) { var m = MarginEdges; m.Right = v; return WithMargin(m); }
    private readonly Style WithMargin(Edges e) { With(StyleFlags.Margin, out var s); s.MarginEdges = e; return s; }

    // ---- Size ----------------------------------------------------------------
    public readonly Style W(Length w) { With(StyleFlags.Width, out var s); s.Width = w; return s; }
    public readonly Style H(Length h) { With(StyleFlags.Height, out var s); s.Height = h; return s; }
    public readonly Style Size(Length w, Length h) => W(w).H(h);
    public readonly Style Size(Length both) => W(both).H(both);
    public readonly Style WFull() => W(Length.Full);
    public readonly Style HFull() => H(Length.Full);
    public readonly Style SizeFull() => W(Length.Full).H(Length.Full);
    public readonly Style MinW(Length v) { With(StyleFlags.MinWidth, out var s); s.MinWidth = v; return s; }
    public readonly Style MinH(Length v) { With(StyleFlags.MinHeight, out var s); s.MinHeight = v; return s; }
    public readonly Style MaxW(Length v) { With(StyleFlags.MaxWidth, out var s); s.MaxWidth = v; return s; }
    public readonly Style MaxH(Length v) { With(StyleFlags.MaxHeight, out var s); s.MaxHeight = v; return s; }

    public readonly Style Grow(float grow = 1) { With(StyleFlags.Grow, out var s); s.GrowFactor = grow; return s; }
    public readonly Style Shrink(float shrink = 1) { With(StyleFlags.Shrink, out var s); s.ShrinkFactor = shrink; return s; }
    public readonly Style Flex1() => Grow(1).Shrink(1);

    // ---- Positioning / overflow ---------------------------------------------
    public readonly Style Relative() { With(StyleFlags.Position, out var s); s.Position = PositionMode.Relative; return s; }
    public readonly Style Absolute() { With(StyleFlags.Position | StyleFlags.Inset, out var s); s.Position = PositionMode.Absolute; if (!Has(StyleFlags.Inset)) { s.InsetEdges = new Edges(float.NaN); s.InsetPercentMask = 0; } return s; }
    /// <summary>Out of flow and placed relative to the window (CSS position: fixed): modals, popups at the pointer.</summary>
    public readonly Style Fixed() { var s = Absolute(); s.Position = PositionMode.Fixed; return s; }
    /// <summary>All four insets; pixels or a percentage of the parent (<c>Length.Percent(50)</c>).</summary>
    public readonly Style Inset(Length all) => WithInsetSide(0, all).WithInsetSide(1, all).WithInsetSide(2, all).WithInsetSide(3, all);
    public readonly Style Left(Length v) => WithInsetSide(0, v);
    public readonly Style Top(Length v) => WithInsetSide(1, v);
    public readonly Style Right(Length v) => WithInsetSide(2, v);
    public readonly Style Bottom(Length v) => WithInsetSide(3, v);

    private readonly Style WithInsetSide(int side, Length v)
    {
        Edges e = Has(StyleFlags.Inset) ? InsetEdges : new Edges(float.NaN);
        byte mask = Has(StyleFlags.Inset) ? InsetPercentMask : (byte)0;
        float value = v.IsAuto ? float.NaN : v.Value;
        switch (side)
        {
            case 0: e.Left = value; break;
            case 1: e.Top = value; break;
            case 2: e.Right = value; break;
            default: e.Bottom = value; break;
        }
        byte bit = (byte)(1 << side);
        mask = v.Unit == LengthUnit.Percent ? (byte)(mask | bit) : (byte)(mask & ~bit);
        With(StyleFlags.Inset, out var s);
        s.InsetEdges = e;
        s.InsetPercentMask = mask;
        return s;
    }

    /// <summary>Lift this element into the overlay layer (popups, dropdowns, tooltips).</summary>
    public readonly Style Overlay() { With(StyleFlags.Overlay, out var s); s.IsOverlay = true; return s; }

    public readonly Style OverflowHidden() { With(StyleFlags.Overflow, out var s); s.Overflow = Overflow.Hidden; return s; }
    public readonly Style OverflowScroll() { With(StyleFlags.Overflow, out var s); s.Overflow = Overflow.Scroll; return s; }

    // ---- Visuals -------------------------------------------------------------
    public readonly Style Bg(Color c) { With(StyleFlags.Background, out var s); s.Background = c; return s; }
    /// <summary>Background from a 0xRRGGBBAA value.</summary>
    public readonly Style Bg(uint rgba) => Bg(Hex(rgba));

    public readonly Style Border(float width, Color color) { With(StyleFlags.BorderWidth | StyleFlags.BorderColor, out var s); s.BorderWidth = width; s.BorderColor = color; return s; }
    public readonly Style Border(float width, uint rgba) => Border(width, Hex(rgba));
    public readonly Style BorderColorOf(Color c) { With(StyleFlags.BorderColor, out var s); s.BorderColor = c; return s; }
    public readonly Style BorderColorOf(uint rgba) => BorderColorOf(Hex(rgba));
    public readonly Style BorderWidthOf(float width) { With(StyleFlags.BorderWidth, out var s); s.BorderWidth = width; return s; }

    public readonly Style Rounded(float r) { With(StyleFlags.Radius, out var s); s.Radius = new Corners(r); return s; }
    public readonly Style Rounded(float tl, float tr, float br, float bl) { With(StyleFlags.Radius, out var s); s.Radius = new Corners(tl, tr, br, bl); return s; }
    public readonly Style RoundedFull() => Rounded(float.MaxValue);

    public readonly Style TextColorOf(Color c) { With(StyleFlags.TextColor, out var s); s.TextColor = c; return s; }
    public readonly Style TextColorOf(uint rgba) => TextColorOf(Hex(rgba));
    public readonly Style Font(int fontId) { With(StyleFlags.Font, out var s); s.FontId = fontId; return s; }
    public readonly Style TextSizeOf(float size) { With(StyleFlags.TextSize, out var s); s.TextSize = size; return s; }

    // Tailwind's font-size scale (text-xs … text-4xl).
    public readonly Style TextXs() => TextSizeOf(12);
    public readonly Style TextSm() => TextSizeOf(14);
    public readonly Style TextBase() => TextSizeOf(16);
    public readonly Style TextLg() => TextSizeOf(18);
    public readonly Style TextXl() => TextSizeOf(20);
    public readonly Style Text2Xl() => TextSizeOf(24);
    public readonly Style Text3Xl() => TextSizeOf(30);
    public readonly Style Text4Xl() => TextSizeOf(36);

    // ---- Gradients -----------------------------------------------------------------

    /// <summary>
    /// A linear gradient background at <paramref name="angleDegrees"/> (CSS: 0 = to top, 90 = to
    /// right, 180 = to bottom). Colors come from <see cref="From(Color)"/>, <see cref="Via(Color)"/>
    /// and <see cref="To(Color)"/>; a missing end fades the other to transparent.
    /// </summary>
    public readonly Style LinearGradient(float angleDegrees) { var s = WithGradient(1); s.Gradient = GradientKind.Linear; s.GradientAngle = angleDegrees; return s; }
    /// <summary>A radial gradient background (an ellipse from the center to the corners).</summary>
    public readonly Style RadialGradient() { var s = WithGradient(1); s.Gradient = GradientKind.Radial; return s; }
    public readonly Style From(Color c) { var s = WithGradient(2); s.GradientFrom = c; return s; }
    public readonly Style From(uint rgba) => From(Hex(rgba));
    public readonly Style Via(Color c) { var s = WithGradient(4); s.GradientVia = c; return s; }
    public readonly Style Via(uint rgba) => Via(Hex(rgba));
    public readonly Style To(Color c) { var s = WithGradient(8); s.GradientTo = c; return s; }
    public readonly Style To(uint rgba) => To(Hex(rgba));
    /// <summary>Shorthand: <c>LinearGradient(angle).From(from).To(to)</c>.</summary>
    public readonly Style LinearGradient(float angleDegrees, uint from, uint to) => LinearGradient(angleDegrees).From(from).To(to);

    private readonly Style WithGradient(byte part)
    {
        With(StyleFlags.Gradient, out var s);
        s.GradientMask |= part;
        return s;
    }

    // ---- Opacity / transitions ---------------------------------------------------

    /// <summary>Opacity of the element and everything inside it (0..1).</summary>
    /// <summary>A box shadow (CSS <c>box-shadow: x y blur spread color</c>), drawn outside the element's box.</summary>
    public readonly Style Shadow(float x, float y, float blur, float spread, Color color)
    {
        With(StyleFlags.Shadow, out var s);
        s.ShadowX = x; s.ShadowY = y; s.ShadowBlur = MathF.Max(0, blur); s.ShadowSpread = spread; s.ShadowColor = color;
        s.ShadowMask |= 3;
        return s;
    }

    /// <inheritdoc cref="Shadow(float, float, float, float, Color)"/>
    public readonly Style Shadow(float x, float y, float blur, float spread, uint rgba) => Shadow(x, y, blur, spread, Hex(rgba));

    /// <summary>The shadow's color, keeping its size (Tailwind's <c>shadow-black/50</c>).</summary>
    public readonly Style ShadowColorOf(Color c) { With(StyleFlags.Shadow, out var s); s.ShadowColor = c; s.ShadowMask |= 2; return s; }
    public readonly Style ShadowColorOf(uint rgba) => ShadowColorOf(Hex(rgba));

    // Tailwind's shadow scale (its main layer), in black unless a color was chosen.
    public readonly Style ShadowSm() => ShadowPreset(0, 1, 2, 0, 0.05f);
    public readonly Style Shadow() => ShadowPreset(0, 1, 3, 0, 0.1f);
    public readonly Style ShadowMd() => ShadowPreset(0, 4, 6, -1, 0.1f);
    public readonly Style ShadowLg() => ShadowPreset(0, 10, 15, -3, 0.1f);
    public readonly Style ShadowXl() => ShadowPreset(0, 20, 25, -5, 0.1f);
    public readonly Style Shadow2Xl() => ShadowPreset(0, 25, 50, -12, 0.25f);
    /// <summary>No shadow (overrides one set earlier, e.g. <c>hover:shadow-none</c>).</summary>
    public readonly Style ShadowNone() => Shadow(0, 0, 0, 0, default(Color));

    private readonly Style ShadowPreset(float x, float y, float blur, float spread, float alpha)
    {
        With(StyleFlags.Shadow, out var s);
        s.ShadowX = x; s.ShadowY = y; s.ShadowBlur = blur; s.ShadowSpread = spread;
        if ((s.ShadowMask & 2) == 0) s.ShadowColor = new Color((byte)0, (byte)0, (byte)0, (byte)MathF.Round(alpha * 255));
        s.ShadowMask |= 1;
        return s;
    }

    /// <summary>Blur the element and everything inside it (CSS <c>filter: blur(radius)</c>).</summary>
    public readonly Style Blur(float radius) { With(StyleFlags.Blur, out var s); s.BlurRadius = MathF.Max(0, radius); return s; }
    /// <summary>Blur what is drawn behind the element, inside its rounded box (CSS <c>backdrop-filter: blur(radius)</c>).</summary>
    public readonly Style BackdropBlur(float radius) { With(StyleFlags.BackdropBlur, out var s); s.BackdropBlurRadius = MathF.Max(0, radius); return s; }
    public readonly float EffectiveBlur => Has(StyleFlags.Blur) ? BlurRadius : 0f;
    public readonly float EffectiveBackdropBlur => Has(StyleFlags.BackdropBlur) ? BackdropBlurRadius : 0f;

    public readonly Style Opacity(float opacity) { With(StyleFlags.Opacity, out var s); s.OpacityValue = Math.Clamp(opacity, 0, 1); return s; }
    /// <summary>Effective opacity (1 when not set).</summary>
    public readonly float EffectiveOpacity => Has(StyleFlags.Opacity) ? OpacityValue : 1f;

    /// <summary>
    /// Animate changes of <paramref name="parts"/> (CSS transition): when a re-render (or a
    /// hover/active state) changes those properties, they move to the new value over the duration
    /// instead of jumping. Defaults: 150 ms, <see cref="Easing.Default"/>.
    /// </summary>
    public readonly Style Transition(TransitionParts parts = TransitionParts.Default)
    {
        With(StyleFlags.Transition, out var s);
        s.TransitionProperties = parts;
        s.TransitionMask |= 8;
        return s;
    }
    /// <summary>Transition duration in milliseconds.</summary>
    public readonly Style Duration(float milliseconds) { With(StyleFlags.Transition, out var s); s.TransitionDuration = MathF.Max(0, milliseconds) / 1000f; s.TransitionMask |= 1; return s; }
    /// <summary>Transition delay in milliseconds.</summary>
    public readonly Style Delay(float milliseconds) { With(StyleFlags.Transition, out var s); s.TransitionDelay = MathF.Max(0, milliseconds) / 1000f; s.TransitionMask |= 2; return s; }
    public readonly Style Ease(Easing easing) { With(StyleFlags.Transition, out var s); s.TransitionEasing = easing; s.TransitionMask |= 4; return s; }

    /// <summary>Seconds the transition takes (0.15 when only the properties were set).</summary>
    internal readonly float EffectiveDuration => (TransitionMask & 1) != 0 ? TransitionDuration : 0.15f;

    // ---- Transform -------------------------------------------------------------
    // Applied when painting (and hit testing), around the origin (the center by default).
    // Children are transformed with their parent. Layout is unaffected.

    /// <summary>Move by pixels, or by a percentage of the element's own size (<c>Length.Percent(-50)</c>).</summary>
    public readonly Style Translate(Length x, Length y) => TranslateXOf(x).TranslateYOf(y);
    public readonly Style TranslateXOf(Length x) { var s = WithTransform(TransformParts.TranslateX); s.TranslateX = x; return s; }
    public readonly Style TranslateYOf(Length y) { var s = WithTransform(TransformParts.TranslateY); s.TranslateY = y; return s; }
    /// <summary>Uniform scale; 1 is unscaled (Tailwind's <c>scale-150</c> is 1.5).</summary>
    public readonly Style Scale(float both) => Scale(both, both);
    public readonly Style Scale(float x, float y) => ScaleXOf(x).ScaleYOf(y);
    public readonly Style ScaleXOf(float x) { var s = WithTransform(TransformParts.ScaleX); s.ScaleX = x; return s; }
    public readonly Style ScaleYOf(float y) { var s = WithTransform(TransformParts.ScaleY); s.ScaleY = y; return s; }
    /// <summary>Rotate clockwise by <paramref name="degrees"/>.</summary>
    public readonly Style Rotate(float degrees) { var s = WithTransform(TransformParts.Rotate); s.RotateDegrees = degrees; return s; }
    /// <summary>Transform origin as fractions of the element's size: (0, 0) is the top-left corner, (0.5, 0.5) the center.</summary>
    public readonly Style Origin(float x, float y) { var s = WithTransform(TransformParts.Origin); s.OriginX = x; s.OriginY = y; return s; }

    private readonly Style WithTransform(TransformParts part)
    {
        With(StyleFlags.Transform, out var s);
        if (!Has(StyleFlags.Transform)) { s.ScaleX = s.ScaleY = 1; s.OriginX = s.OriginY = 0.5f; }
        s.TransformMask |= part;
        return s;
    }

    /// <summary>The element's transform in window coordinates, for a box at (x, y, w, h). Identity if none.</summary>
    internal readonly System.Numerics.Matrix3x2 TransformFor(float x, float y, float w, float h)
    {
        if (!Has(StyleFlags.Transform) || TransformMask == TransformParts.None) return System.Numerics.Matrix3x2.Identity;
        float ox = x + w * (HasTransform(TransformParts.Origin) ? OriginX : 0.5f);
        float oy = y + h * (HasTransform(TransformParts.Origin) ? OriginY : 0.5f);
        float sx = HasTransform(TransformParts.ScaleX) ? ScaleX : 1;
        float sy = HasTransform(TransformParts.ScaleY) ? ScaleY : 1;
        float tx = HasTransform(TransformParts.TranslateX) ? TranslateX.Resolve(w, 0) : 0;
        float ty = HasTransform(TransformParts.TranslateY) ? TranslateY.Resolve(h, 0) : 0;
        float rad = HasTransform(TransformParts.Rotate) ? RotateDegrees * (MathF.PI / 180f) : 0;
        // CSS order (translate, rotate, scale) around the origin; System.Numerics composes left to right.
        return System.Numerics.Matrix3x2.CreateTranslation(-ox, -oy)
             * System.Numerics.Matrix3x2.CreateScale(sx, sy)
             * System.Numerics.Matrix3x2.CreateRotation(rad)
             * System.Numerics.Matrix3x2.CreateTranslation(ox + tx, oy + ty);
    }
}
