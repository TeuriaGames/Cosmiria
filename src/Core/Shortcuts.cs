using Riateu.Inputs;

namespace Cosmiria;

/// <summary>A parsed keyboard shortcut like "Ctrl+Shift+S", "F2" or "Del".</summary>
internal readonly record struct ShortcutKey(KeyCode Key, bool Ctrl, bool Shift, bool Alt)
{
    private static readonly Dictionary<string, ShortcutKey> cache = new();
    private static readonly Dictionary<string, KeyCode> aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Del"] = KeyCode.Delete, ["Ins"] = KeyCode.Insert, ["Esc"] = KeyCode.Escape, ["Enter"] = KeyCode.Return,
        ["Return"] = KeyCode.Return, ["Space"] = KeyCode.Space, ["Tab"] = KeyCode.Tab, ["Backspace"] = KeyCode.Backspace,
        ["PgUp"] = KeyCode.PageUp, ["PgDn"] = KeyCode.PageDown, ["Plus"] = KeyCode.Equals, ["Minus"] = KeyCode.Minus,
        ["-"] = KeyCode.Minus, ["="] = KeyCode.Equals, [","] = KeyCode.Comma, ["."] = KeyCode.Period, ["/"] = KeyCode.Slash,
    };

    /// <summary>Parse (cached per string, so literals cost a dictionary lookup per build).</summary>
    public static bool TryParse(string text, out ShortcutKey key)
    {
        lock (cache)
        {
            if (cache.TryGetValue(text, out key)) return key.Key != KeyCode.Unknown;
            key = Parse(text);
            cache[text] = key;
            return key.Key != KeyCode.Unknown;
        }
    }

    private static ShortcutKey Parse(string text)
    {
        bool ctrl = false, shift = false, alt = false;
        KeyCode main = KeyCode.Unknown;
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Cmd", StringComparison.OrdinalIgnoreCase)) ctrl = true;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase) || part.Equals("Option", StringComparison.OrdinalIgnoreCase)) alt = true;
            else if (aliases.TryGetValue(part, out KeyCode alias)) main = alias;
            else if (part.Length == 1 && char.IsDigit(part[0])) main = Digit(part[0]);
            else if (Enum.TryParse(part, ignoreCase: true, out KeyCode k)) main = k;
            else return default;
        }
        return new ShortcutKey(main, ctrl, shift, alt);
    }

    private static KeyCode Digit(char c) => c == '0' ? KeyCode.D0 : KeyCode.D1 + (c - '1');
}

public sealed partial class UIStack
{
    /// <summary>
    /// Run <paramref name="run"/> (a listener on this component) when <paramref name="keys"/> is
    /// pressed, e.g. "Ctrl+S", "Ctrl+Shift+Z", "F2", "Del". Register it on every render (like a
    /// listener). Disabled while a modal is open; while typing in a text input only Ctrl/Alt
    /// shortcuts that don't edit text fire. Menu items with a shortcut register theirs.
    /// </summary>
    public void Shortcut(string keys, EventHandle run)
    {
        if (keys != null && run.IsValid && ShortcutKey.TryParse(keys, out var key)) context.RegisterShortcut(key, run);
    }

    /// <summary>For generated code: <c>ui.ShortcutFor("Ctrl+S").Run(listener)</c>.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public ShortcutCall ShortcutFor(string keys) => new(this, keys);
}

/// <summary>Returned by <see cref="UIStack.ShortcutFor"/>.</summary>
public readonly struct ShortcutCall
{
    private readonly UIStack ui;
    private readonly string keys;
    internal ShortcutCall(UIStack ui, string keys) { this.ui = ui; this.keys = keys; }
    public void Run(EventHandle run) => ui.Shortcut(keys, run);
}
