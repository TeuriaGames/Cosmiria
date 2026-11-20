namespace Cosmiria;

/// <summary>
/// Editable text for a <see cref="UIStack.TextInput"/>. Keep one in your component state; the
/// input edits it in place (no string allocation per keystroke) and tracks caret and selection.
/// <code>
/// public struct Form : IComponent
/// {
///     public TextBuffer Name;   // created once, e.g. in the Mount factory
///     public Node Render(UIStack ui, EventStack ev) => ui.TextInput(Name, "Your name");
/// }
/// </code>
/// Read the value with <see cref="Text"/> (a span, no allocation) or <see cref="ToString"/>.
/// </summary>
public sealed class TextBuffer
{
    private char[] chars;

    /// <summary>Number of characters.</summary>
    public int Length { get; private set; }

    /// <summary>Caret position (0..Length).</summary>
    public int Caret { get; private set; }

    /// <summary>Selection anchor; the selection is between Anchor and Caret.</summary>
    public int Anchor { get; private set; }

    /// <summary>Maximum number of characters accepted from typing and pasting.</summary>
    public int MaxLength { get; set; } = int.MaxValue;

    /// <summary>Incremented on every change to the text.</summary>
    public int Version { get; private set; }

    public TextBuffer(string initial = null, int capacity = 32)
    {
        chars = new char[Math.Max(capacity, initial?.Length ?? 0)];
        if (initial != null) Set(initial);
    }

    public ReadOnlySpan<char> Text => chars.AsSpan(0, Length);

    public bool HasSelection => Anchor != Caret;
    public int SelectionStart => Math.Min(Anchor, Caret);
    public int SelectionEnd => Math.Max(Anchor, Caret);
    public ReadOnlySpan<char> Selection => chars.AsSpan(SelectionStart, SelectionEnd - SelectionStart);

    /// <summary>Replace the whole text and put the caret at the end.</summary>
    public void Set(ReadOnlySpan<char> text)
    {
        EnsureCapacity(text.Length);
        text.CopyTo(chars);
        Length = text.Length;
        Caret = Anchor = Length;
        Version++;
    }

    public void Clear() => Set(default);

    public override string ToString() => new string(chars, 0, Length);

    // ------------------------------------------------------------------ editing (used by the input)

    internal void SetCaret(int position, bool extendSelection)
    {
        Caret = Math.Clamp(position, 0, Length);
        if (!extendSelection) Anchor = Caret;
    }

    /// <summary>Select the whole text (typing then replaces it).</summary>
    public void SelectAll()
    {
        Anchor = 0;
        Caret = Length;
    }

    /// <summary>Insert at the caret, replacing the selection. Returns false if nothing changed.</summary>
    internal bool Insert(ReadOnlySpan<char> text)
    {
        bool removed = DeleteSelection();
        int room = Math.Max(0, MaxLength - Length);
        if (text.Length > room) text = text.Slice(0, room);
        if (text.IsEmpty) return removed;

        EnsureCapacity(Length + text.Length);
        Array.Copy(chars, Caret, chars, Caret + text.Length, Length - Caret);
        text.CopyTo(chars.AsSpan(Caret));
        Length += text.Length;
        Caret += text.Length;
        Anchor = Caret;
        Version++;
        return true;
    }

    internal bool Backspace(bool word)
    {
        if (DeleteSelection()) return true;
        if (Caret == 0) return false;
        int from = word ? WordBoundary(Caret, -1) : Caret - 1;
        Remove(from, Caret - from);
        Caret = Anchor = from;
        return true;
    }

    internal bool Delete(bool word)
    {
        if (DeleteSelection()) return true;
        if (Caret == Length) return false;
        int to = word ? WordBoundary(Caret, +1) : Caret + 1;
        Remove(Caret, to - Caret);
        return true;
    }

    internal void Move(int direction, bool word, bool extendSelection)
    {
        int target;
        if (!extendSelection && HasSelection)
            target = direction < 0 ? SelectionStart : SelectionEnd; // collapse the selection
        else
            target = word ? WordBoundary(Caret, direction) : Caret + direction;
        SetCaret(target, extendSelection);
    }

    internal bool DeleteSelection()
    {
        if (!HasSelection) return false;
        int start = SelectionStart, end = SelectionEnd;
        Remove(start, end - start);
        Caret = Anchor = start;
        return true;
    }

    private void Remove(int start, int count)
    {
        Array.Copy(chars, start + count, chars, start, Length - start - count);
        Length -= count;
        Version++;
    }

    /// <summary>Next word boundary from <paramref name="from"/> in <paramref name="direction"/>.</summary>
    private int WordBoundary(int from, int direction)
    {
        int i = from;
        if (direction < 0)
        {
            while (i > 0 && char.IsWhiteSpace(chars[i - 1])) i--;
            while (i > 0 && !char.IsWhiteSpace(chars[i - 1])) i--;
        }
        else
        {
            while (i < Length && !char.IsWhiteSpace(chars[i])) i++;
            while (i < Length && char.IsWhiteSpace(chars[i])) i++;
        }
        return i;
    }

    private void EnsureCapacity(int size)
    {
        if (size > chars.Length) Array.Resize(ref chars, Math.Max(size, chars.Length * 2));
    }
}
