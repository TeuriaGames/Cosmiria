namespace Cosmiria;

/// <summary>
/// Greedy word wrapper shared by layout (measuring) and painting, so both always agree.
/// Breaks at spaces; a word wider than the line is broken between characters (never inside one,
/// or between a letter and its marks). Explicit '\n' always breaks. Widths come from the shaped
/// text (<see cref="ShapedText"/>), in the font's unscaled units.
/// </summary>
internal ref struct LineBreaker
{
    private readonly ShapedText shaped;
    private readonly ReadOnlySpan<char> text;
    private readonly float maxWidth;
    private int pos;

    /// <param name="maxWidth">Line width limit; <see cref="float.PositiveInfinity"/> disables wrapping.</param>
    public LineBreaker(UIFont font, ReadOnlySpan<char> text, float maxWidth)
        : this(text.IsEmpty ? null : font.Shape(text), text, maxWidth) { }

    public LineBreaker(ShapedText shaped, ReadOnlySpan<char> text, float maxWidth)
    {
        this.shaped = shaped;
        this.text = text;
        // Tiny epsilon so text measured at exactly its own width doesn't re-wrap.
        this.maxWidth = maxWidth + 0.01f;
        pos = text.IsEmpty ? 1 : 0;
    }

    /// <summary>The shaped text being broken (null for empty text).</summary>
    public readonly ShapedText Shaped => shaped;

    public bool Next(out int start, out int length, out float width)
    {
        start = pos;
        if (pos > text.Length)
        {
            length = 0;
            width = 0;
            return false;
        }

        float w = 0, widthAtBreak = 0;
        int lastSpace = -1;
        float[] advance = shaped?.CharAdvance;

        for (int i = pos; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\n')
            {
                length = i - start;
                width = w;
                pos = i + 1;
                return true;
            }

            float adv = advance[i];

            if (ch == ' ')
            {
                lastSpace = i;
                widthAtBreak = w;
            }
            else if (adv > 0 && w + adv > maxWidth && i > start)
            {
                if (lastSpace >= start)
                {
                    // Break at the last space; the space itself is dropped.
                    length = lastSpace - start;
                    width = widthAtBreak;
                    pos = lastSpace + 1;
                }
                else
                {
                    // A single word longer than the line: break mid-word, at a character boundary.
                    int at = i;
                    while (at > start + 1 && !shaped.IsCaretStop(at)) at--;
                    length = at - start;
                    width = shaped.Width(start, at);
                    pos = at;
                }
                return true;
            }

            w += adv;
        }

        length = text.Length - start;
        width = w;
        pos = text.Length + 1;
        return true;
    }

    /// <summary>Wrapped size (unscaled): widest line and number of lines.</summary>
    public static (float width, int lines) Measure(UIFont font, ReadOnlySpan<char> text, float maxWidth)
    {
        var breaker = new LineBreaker(font, text, maxWidth);
        float widest = 0;
        int lines = 0;
        while (breaker.Next(out _, out _, out float w))
        {
            if (w > widest) widest = w;
            lines++;
        }
        return (widest, lines);
    }

    /// <summary>Width of the longest unbreakable word (unscaled): the text's min-content width.</summary>
    public static float LongestWord(UIFont font, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return 0;
        float[] advance = font.Shape(text).CharAdvance;
        float longest = 0, w = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == ' ' || ch == '\n')
            {
                if (w > longest) longest = w;
                w = 0;
                continue;
            }
            w += advance[i];
        }
        return w > longest ? w : longest;
    }
}
