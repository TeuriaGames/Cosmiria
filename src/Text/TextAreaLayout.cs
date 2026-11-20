namespace Cosmiria;

/// <summary>
/// Caret geometry for wrapped multi-line text, built on <see cref="LineBreaker"/> so the renderer
/// and the keyboard/mouse handling agree on where lines break. Widths are in unscaled font units,
/// measured on the shaped paragraph (so a caret sits where the shaped glyphs are).
/// A line owns the indices from its start up to the next line's start (the dropped space or
/// newline at a break belongs to the line it ends).
/// </summary>
internal static class TextAreaLayout
{
    /// <summary>Visual line count (at least 1).</summary>
    public static int LineCount(UIFont font, ReadOnlySpan<char> text, float wrap)
    {
        var breaker = new LineBreaker(font, text, wrap);
        int count = 0;
        while (breaker.Next(out _, out _, out _)) count++;
        return Math.Max(1, count);
    }

    /// <summary>Line of <paramref name="index"/>, its x offset, and that line's start and length.</summary>
    public static void Locate(UIFont font, ReadOnlySpan<char> text, float wrap, int index,
        out int line, out float x, out int lineStart, out int lineLength)
    {
        line = 0; x = 0; lineStart = 0; lineLength = 0;
        var breaker = new LineBreaker(font, text, wrap);
        int i = 0;
        bool have = breaker.Next(out int start, out int length, out _);
        while (have)
        {
            bool hasNext = breaker.Next(out int nextStart, out int nextLength, out _);
            if (!hasNext || index < nextStart)
            {
                line = i; lineStart = start; lineLength = length;
                x = breaker.Shaped?.Width(start, start + Math.Clamp(index - start, 0, length)) ?? 0;
                return;
            }
            i++;
            start = nextStart;
            length = nextLength;
            have = true;
        }
    }

    /// <summary>Caret index closest to (<paramref name="x"/>, <paramref name="line"/>); lines are clamped.</summary>
    public static int IndexAt(UIFont font, ReadOnlySpan<char> text, float wrap, int line, float x)
    {
        var breaker = new LineBreaker(font, text, wrap);
        int i = 0, lastStart = 0, lastLength = 0;
        while (breaker.Next(out int start, out int length, out _))
        {
            lastStart = start; lastLength = length;
            if (i == Math.Max(0, line)) break;
            i++;
        }
        return breaker.Shaped?.IndexAt(lastStart, lastStart + lastLength, x) ?? 0;
    }
}
