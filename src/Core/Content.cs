using System.Runtime.CompilerServices;

namespace Cosmiria;

/// <summary>
/// A child given to <see cref="Node.Child(System.ReadOnlySpan{Content})"/>: an element or a
/// string (a C# 15 union of <see cref="Node"/> and <see cref="string"/>). Strings become text
/// elements, so text and elements mix in one list:
/// <code>
/// ui.View(style).Child(["Volume", ui.Mount&lt;Slider&gt;(0), muted ? "(muted)" : Node.None]);
/// </code>
/// Stored without boxing; <c>content switch { Node n =&gt; …, string s =&gt; … }</c> is exhaustive.
/// A string literal costs nothing, but an interpolated <c>$"…"</c> string allocates: for formatted
/// text use <c>ui.Text($"…")</c>, which writes straight into the element arena.
/// </summary>
[Union]
public readonly struct Content : IUnion
{
    private readonly Node node;
    private readonly string text;

    public Content(Node node) { this.node = node; text = null; }
    public Content(string text) { node = default; this.text = text; }

    /// <summary>The element or string (boxes an element; pattern matching uses <see cref="TryGetValue(out Node)"/> instead).</summary>
    public object Value => text != null ? text : node.IsNone ? null : node;

    /// <summary>False for <see cref="Node.None"/> and null strings: nothing to add.</summary>
    public bool HasValue => text != null || !node.IsNone;

    public bool TryGetValue(out Node value)
    {
        value = node;
        return text == null && !node.IsNone;
    }

    public bool TryGetValue(out string value)
    {
        value = text;
        return text != null;
    }
}
