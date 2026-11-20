using System.Numerics;
using Riateu.Graphics;

namespace Cosmiria;

/// <summary>
/// A registered font: the SpriteFont (for its outlines and metrics) plus a HarfBuzz shaper. Text
/// is shaped as whole paragraphs (see <see cref="ShapedText"/>), so widths include ligatures and
/// kerning, and the renderer draws the glyphs the shaper picked.
/// </summary>
public sealed class UIFont : IDisposable
{
    public SpriteFont Font { get; }
    /// <summary>Index in the <see cref="FontRegistry"/>.</summary>
    public int Id { get; }
    public float Size => Font.Size;
    public float LineHeight => Font.LineHeight;

    private readonly TextShaper shaper;

    internal UIFont(SpriteFont font, int id)
    {
        Font = font;
        Id = id;
        shaper = new TextShaper(font.Font.Data, font.Font.GetScale(font.Size));
    }

    /// <summary>The shaped <paramref name="text"/> (cached by content).</summary>
    internal ShapedText Shape(ReadOnlySpan<char> text) => shaper.Shape(text);

    /// <summary>Width of a single line of text in font units (shaped: kerning and ligatures included).</summary>
    public float Advance(ReadOnlySpan<char> text) => text.IsEmpty ? 0 : Shape(text).Width(0, text.Length);

    /// <summary>Width of a space (font units).</summary>
    internal float SpaceAdvance => spaceAdvance >= 0 ? spaceAdvance : spaceAdvance = Advance(" ");
    private float spaceAdvance = -1;

    /// <summary>Caret index closest to <paramref name="x"/> (font units) in a single line.</summary>
    internal int IndexAt(ReadOnlySpan<char> text, float x) => text.IsEmpty ? 0 : Shape(text).IndexAt(0, text.Length, x);

    /// <summary>Measure text at the given scale (1 = the font's native size); '\n' starts a line.</summary>
    public Vector2 Measure(ReadOnlySpan<char> text, float scale = 1f)
    {
        if (text.IsEmpty)
            return Vector2.Zero;
        ShapedText shaped = Shape(text);
        float maxWidth = 0;
        int lines = 1, lineStart = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n') continue;
            maxWidth = MathF.Max(maxWidth, shaped.Width(lineStart, i));
            if (i < text.Length) lines++;
            lineStart = i + 1;
        }
        return new Vector2(maxWidth, lines * LineHeight) * scale;
    }

    public void Dispose() => shaper.Dispose();
}

/// <summary>Font id → font. Id 0 is the default font given to the <see cref="UIContext"/>.</summary>
public sealed class FontRegistry
{
    private readonly List<UIFont> fonts = new();

    public int Add(SpriteFont font)
    {
        fonts.Add(new UIFont(font, fonts.Count));
        return fonts.Count - 1;
    }

    public UIFont this[int id] => (uint)id < (uint)fonts.Count ? fonts[id] : fonts[0];

    public int Count => fonts.Count;
}
