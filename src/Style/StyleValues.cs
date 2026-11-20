using Riateu.Graphics;

namespace Cosmiria;

/// <summary>
/// Conversions used by CML's C# [@value] utilities (<c>w-[@Width]</c>, <c>text-[@Accent]</c>…),
/// so any numeric type, a <see cref="Length"/> or a color works where it makes sense.
/// </summary>
public static class StyleValues
{
    public static Length Len(float px) => Length.Px(px);
    public static Length Len(double px) => Length.Px((float)px);
    public static Length Len(int px) => Length.Px(px);
    public static Length Len(Length length) => length;
    public static Length Negate(Length l) => new(-l.Value, l.Unit);

    /// <summary>text-[@x]: a number is the text size, a color (Color or 0xRRGGBBAA) is the text color.</summary>
    public static Style TextValue(this Style s, float size) => s.TextSizeOf(size);
    public static Style TextValue(this Style s, double size) => s.TextSizeOf((float)size);
    public static Style TextValue(this Style s, int size) => s.TextSizeOf(size);
    public static Style TextValue(this Style s, Color color) => s.TextColorOf(color);
    public static Style TextValue(this Style s, uint rgba) => s.TextColorOf(rgba);

    /// <summary>border-[@x]: a number is the border width, a color is the border color.</summary>
    public static Style BorderValue(this Style s, float width) => s.BorderWidthOf(width);
    public static Style BorderValue(this Style s, double width) => s.BorderWidthOf((float)width);
    public static Style BorderValue(this Style s, int width) => s.BorderWidthOf(width);
    public static Style BorderValue(this Style s, Color color) => s.BorderColorOf(color);
    public static Style BorderValue(this Style s, uint rgba) => s.BorderColorOf(rgba);
}
