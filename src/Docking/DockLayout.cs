namespace Cosmiria;

/// <summary>Where a dragged panel lands relative to a tab group.</summary>
public enum DockZone : byte { Center, Left, Right, Top, Bottom }

/// <summary>
/// A node of a <see cref="DockLayout"/>: a <see cref="DockGroup"/> or a <see cref="DockSplit"/>,
/// nothing else (a closed hierarchy, so a switch over both is exhaustive).
/// </summary>
public closed class DockNode
{
    internal DockSplit Parent;
}

/// <summary>Panels shown as tabs; one is visible at a time.</summary>
public sealed class DockGroup : DockNode
{
    /// <summary>The floating window holding this group, or null when it's docked.</summary>
    public DockFloat Float { get; internal set; }

    public readonly List<string> Panels = new();
    public int Active;
    public string ActivePanel => Panels.Count > 0 ? Panels[Math.Clamp(Active, 0, Panels.Count - 1)] : null;
}

/// <summary>Two nodes side by side (or stacked when <see cref="Vertical"/>), split at <see cref="Fraction"/>.</summary>
public sealed class DockSplit : DockNode
{
    public bool Vertical;
    /// <summary>Share of the first node (0..1).</summary>
    public float Fraction;
    public DockNode First, Second;
}

/// <summary>
/// A floating window: a tab group drawn above the docked panels at a position and size (window
/// pixels). Drag its tab bar to move it, its corner to resize it, and its tabs to dock them back.
/// </summary>
public sealed class DockFloat
{
    public DockGroup Group { get; internal set; }
    public float X, Y, W, H;
    /// <summary>Stable identity, so a window keeps its scroll and hover state when raised.</summary>
    internal int Id;
}

/// <summary>
/// The arrangement of a <see cref="UIStack.DockSpace"/>: a tree of splits and tab groups. Keep it
/// in your component's state (it's a reference type, so it persists and the dock space mutates it
/// when the user drags tabs or dividers):
/// <code>
/// Layout = new DockLayout(DockLayout.Row(0.25f,
///     DockLayout.Tabs("hierarchy"),
///     DockLayout.Column(0.7f, DockLayout.Tabs("scene", "game"), DockLayout.Tabs("console", "assets"))));
/// </code>
/// Panels declared with <c>DockSpace.Panel</c> but missing from the layout are added to the first group.
/// </summary>
public sealed class DockLayout
{
    public DockNode Root { get; private set; }

    /// <summary>Floating windows, back to front.</summary>
    public IReadOnlyList<DockFloat> Floating => floating;
    private readonly List<DockFloat> floating = new();
    private int nextFloatId = 1;

    /// <summary>Panels closed by the user (a window's close button, or <see cref="Close(string)"/>).</summary>
    public IReadOnlyCollection<string> ClosedPanels => closed;
    internal readonly HashSet<string> closed = new();

    /// <summary>Raised with each panel's id when it's closed.</summary>
    public event Action<string> PanelClosed;

    /// <summary>Smallest size a floating window can be resized to.</summary>
    public const float MinFloatWidth = 160, MinFloatHeight = 100;

    // Per-build slots filled by the dock space (headers and content of each panel), and titles.
    internal readonly Dictionary<string, (Node header, Node content, bool active, int build)> Slots = new();
    internal readonly Dictionary<string, string> Titles = new();
    /// <summary>Panels declared closable (<see cref="DockSpace.Panel"/>), as of their last build.</summary>
    internal readonly HashSet<string> ClosablePanels = new();

    /// <summary>Whether the user can close <paramref name="panel"/> (it was declared with <c>closable: true</c>).</summary>
    public bool IsClosable(string panel) => ClosablePanels.Contains(panel);

    /// <summary>Every panel of <paramref name="group"/> is closable (a floating window then gets a close button).</summary>
    internal bool AllClosable(DockGroup group)
    {
        if (group.Panels.Count == 0) return false;
        foreach (string panel in group.Panels) if (!ClosablePanels.Contains(panel)) return false;
        return true;
    }
    internal int Build;

    public DockLayout(DockNode root)
    {
        Root = root ?? new DockGroup();
        Link(Root, null);
    }

    public static DockGroup Tabs(params string[] panels)
    {
        var g = new DockGroup();
        g.Panels.AddRange(panels);
        return g;
    }

    public static DockSplit Row(float fraction, DockNode left, DockNode right) =>
        new() { Vertical = false, Fraction = fraction, First = left, Second = right };

    public static DockSplit Column(float fraction, DockNode top, DockNode bottom) =>
        new() { Vertical = true, Fraction = fraction, First = top, Second = bottom };

    public bool Contains(string panel) => FindGroup(panel) != null;

    /// <summary>The group showing <paramref name="panel"/> (docked or floating), or null.</summary>
    public DockGroup FindGroup(string panel)
    {
        DockGroup g = FindGroup(Root, panel);
        if (g != null) return g;
        foreach (DockFloat f in floating)
            if (f.Group.Panels.Contains(panel)) return f.Group;
        return null;
    }

    /// <summary>Closed by the user: it stays out of the layout until <see cref="Open"/> or <see cref="Activate"/>.</summary>
    public bool IsClosed(string panel) => closed.Contains(panel);

    /// <summary>
    /// Close <paramref name="panel"/>: it leaves the layout, and <see cref="DockSpace.Panel"/>
    /// doesn't add it back (as it would for a new panel) until it's opened again.
    /// </summary>
    private readonly Dictionary<string, System.Numerics.Vector2> minSizes = new();

    /// <summary>
    /// Keep <paramref name="panel"/> at least this big when dividers are dragged: a divider stops
    /// where a panel on either side, however deeply nested, would get smaller.
    /// </summary>
    public void SetMinSize(string panel, float width, float height) => minSizes[panel] = new(width, height);

    /// <summary>The smallest size <paramref name="node"/> can take with its panels' minimums.</summary>
    internal System.Numerics.Vector2 MinSize(DockNode node, float handle)
    {
        switch (node)
        {
            case DockGroup g:
                var size = System.Numerics.Vector2.Zero;
                foreach (string panel in g.Panels)
                    if (minSizes.TryGetValue(panel, out var m)) size = System.Numerics.Vector2.Max(size, m);
                return size;
            case DockSplit s:
                var a = MinSize(s.First, handle);
                var b = MinSize(s.Second, handle);
                return s.Vertical
                    ? new(MathF.Max(a.X, b.X), a.Y + b.Y + handle)
                    : new(a.X + b.X + handle, MathF.Max(a.Y, b.Y));
            default:
                return System.Numerics.Vector2.Zero;
        }
    }

    /// <summary>The fraction for a drag of <paramref name="split"/>'s divider, kept within its panels' minimums.</summary>
    internal float DragFraction(DockSplit split, in PointerEvent e)
    {
        bool vertical = split.Vertical;
        float handle = vertical ? e.Size.Y : e.Size.X;
        float shared = (vertical ? e.ParentSize.Y : e.ParentSize.X) - handle;
        if (shared <= 0) return split.Fraction;
        var first = MinSize(split.First, handle);
        var second = MinSize(split.Second, handle);
        float lo = MathF.Max(0.05f, (vertical ? first.Y : first.X) / shared);
        float hi = MathF.Min(0.95f, 1 - (vertical ? second.Y : second.X) / shared);
        if (lo > hi) return split.Fraction; // both sides can't fit: leave it where it is
        return Math.Clamp(Split.FractionAt(e, vertical), lo, hi);
    }

    public void Close(string panel)
    {
        if (!Contains(panel)) return;
        Remove(panel);
        closed.Add(panel);
        PanelClosed?.Invoke(panel);
    }

    /// <summary>Close a floating window and every panel in it.</summary>
    public void Close(DockFloat window)
    {
        if (window == null || !floating.Contains(window)) return;
        string[] panels = window.Group.Panels.ToArray();
        foreach (string panel in panels) Close(panel); // the last one closes the window
    }

    /// <summary>Reopen a closed panel as a tab of the first group (nothing if it isn't closed).</summary>
    public void Open(string panel)
    {
        if (!closed.Remove(panel)) return;
        Add(panel);
    }

    /// <summary>Bring <paramref name="panel"/>'s tab to the front (and its floating window, if any). Reopens it if it was closed.</summary>
    public void Activate(string panel)
    {
        Open(panel);
        DockGroup g = FindGroup(panel);
        if (g == null) return;
        g.Active = g.Panels.IndexOf(panel);
        if (g.Float != null) BringToFront(g.Float);
    }

    /// <summary>
    /// Show <paramref name="panel"/> in a floating window at (x, y) with size (w, h). A panel that's
    /// already alone in a floating window just moves (keeping its size).
    /// </summary>
    public DockFloat Float(string panel, float x, float y, float w = 360, float h = 260)
    {
        DockGroup source = FindGroup(panel);
        if (source == null) return null;
        if (source.Float != null && source.Panels.Count == 1)
        {
            source.Float.X = x; source.Float.Y = y;
            BringToFront(source.Float);
            return source.Float;
        }
        Remove(panel);
        return AddFloat(Tabs(panel), x, y, w, h);
    }

    /// <summary>Draw <paramref name="window"/> above the other floating windows.</summary>
    public void BringToFront(DockFloat window)
    {
        int i = floating.IndexOf(window);
        if (i < 0 || i == floating.Count - 1) return;
        floating.RemoveAt(i);
        floating.Add(window);
    }

    internal DockFloat AddFloat(DockGroup group, float x, float y, float w, float h)
    {
        var f = new DockFloat
        {
            Group = group, X = x, Y = Math.Max(0, y),
            W = Math.Max(MinFloatWidth, w), H = Math.Max(MinFloatHeight, h), Id = nextFloatId++,
        };
        group.Float = f;
        group.Parent = null;
        floating.Add(f);
        return f;
    }

    /// <summary>Add a panel as a tab of the first group (if it isn't in the layout yet; this reopens a closed one).</summary>
    public void Add(string panel)
    {
        closed.Remove(panel);
        if (Contains(panel)) return;
        DockGroup g = FirstGroup(Root);
        g.Panels.Add(panel);
    }

    /// <summary>Remove a panel; an emptied group disappears and its sibling takes the space.</summary>
    public void Remove(string panel)
    {
        DockGroup g = FindGroup(panel);
        if (g == null) return;
        int i = g.Panels.IndexOf(panel);
        g.Panels.RemoveAt(i);
        if (g.Active >= i && g.Active > 0) g.Active--;
        if (g.Panels.Count > 0) return;
        if (g.Float != null) floating.Remove(g.Float); // an emptied window closes
        else Collapse(g);
    }

    /// <summary>
    /// Dock <paramref name="panel"/> onto <paramref name="target"/>: as a tab (<see cref="DockZone.Center"/>)
    /// or in a new group splitting the target in half on that side. A floating window's group only
    /// takes tabs (Center).
    /// </summary>
    public void Move(string panel, DockGroup target, DockZone zone)
    {
        DockGroup source = FindGroup(panel);
        if (source == null || target == null) return;
        if (target.Float != null && zone != DockZone.Center) return;
        if (zone == DockZone.Center && source == target) return;
        if (zone != DockZone.Center && source == target && source.Panels.Count == 1) return;

        Remove(panel);
        if (!IsLive(target)) return; // can't happen: target != emptied source
        if (zone == DockZone.Center)
        {
            target.Panels.Add(panel);
            target.Active = target.Panels.Count - 1;
            return;
        }

        SplitBeside(target, Tabs(panel), zone);
    }

    /// <summary>
    /// Dock a whole floating <paramref name="window"/> onto <paramref name="target"/>: its tabs join
    /// the target (<see cref="DockZone.Center"/>), or its group splits the target on that side.
    /// </summary>
    public void Dock(DockFloat window, DockGroup target, DockZone zone)
    {
        if (zone == DockZone.Center) { Dock(window, target, target?.Panels.Count ?? 0); return; }
        if (window == null || target == null || !floating.Contains(window) || target == window.Group) return;
        if (target.Float != null || !IsLive(target)) return; // a window's group only takes tabs
        DockGroup g = window.Group;
        floating.Remove(window);
        g.Float = null;
        SplitBeside(target, g, zone);
    }

    /// <summary>Dock a whole floating <paramref name="window"/>'s tabs into <paramref name="target"/> at <paramref name="index"/> (its selected tab stays selected).</summary>
    public void Dock(DockFloat window, DockGroup target, int index)
    {
        if (window == null || target == null || !floating.Contains(window) || target == window.Group || !IsLive(target)) return;
        DockGroup g = window.Group;
        string active = g.ActivePanel;
        floating.Remove(window);
        g.Float = null;
        index = Math.Clamp(index, 0, target.Panels.Count);
        target.Panels.InsertRange(index, g.Panels);
        target.Active = Math.Max(0, target.Panels.IndexOf(active));
        if (target.Float != null) BringToFront(target.Float);
    }

    /// <summary>Replace <paramref name="target"/> by a split of it and <paramref name="group"/>, which goes on <paramref name="zone"/>'s side.</summary>
    private void SplitBeside(DockGroup target, DockGroup group, DockZone zone)
    {
        bool vertical = zone == DockZone.Top || zone == DockZone.Bottom;
        bool first = zone == DockZone.Left || zone == DockZone.Top;
        DockSplit parent = target.Parent;
        var split = new DockSplit { Vertical = vertical, Fraction = 0.5f, First = first ? group : target, Second = first ? target : group };
        Replace(parent, target, split);
        Link(split, parent);
    }

    /// <summary>
    /// Put <paramref name="panel"/>'s tab at position <paramref name="index"/> of
    /// <paramref name="target"/>: reorders it within its own group (keeping the selected tab), or
    /// moves it into another group there and selects it.
    /// </summary>
    public void MoveTab(string panel, DockGroup target, int index)
    {
        DockGroup source = FindGroup(panel);
        if (source == null || target == null) return;
        if (source == target)
        {
            string active = source.ActivePanel;
            int from = source.Panels.IndexOf(panel);
            index = Math.Clamp(index, 0, source.Panels.Count - 1);
            if (from == index) return;
            source.Panels.RemoveAt(from);
            source.Panels.Insert(index, panel);
            source.Active = source.Panels.IndexOf(active);
            return;
        }
        Remove(panel);
        if (!IsLive(target)) return;
        index = Math.Clamp(index, 0, target.Panels.Count);
        target.Panels.Insert(index, panel);
        target.Active = index;
    }

    // ------------------------------------------------------------------ tree helpers

    private void Collapse(DockGroup empty)
    {
        DockSplit parent = empty.Parent;
        if (parent == null) return; // the root group stays (empty)
        DockNode sibling = parent.First == empty ? parent.Second : parent.First;
        Replace(parent.Parent, parent, sibling);
        sibling.Parent = parent.Parent;
    }

    private void Replace(DockSplit parent, DockNode old, DockNode node)
    {
        if (parent == null) Root = node;
        else if (parent.First == old) parent.First = node;
        else parent.Second = node;
        node.Parent = parent;
    }

    /// <summary>Still part of the layout: docked in the tree, or an open floating window's group.</summary>
    private bool IsLive(DockGroup group) => group.Float != null ? floating.Contains(group.Float) : IsInTree(group);

    private bool IsInTree(DockNode node)
    {
        while (node.Parent != null) node = node.Parent;
        return node == Root;
    }

    private static void Link(DockNode node, DockSplit parent)
    {
        node.Parent = parent;
        if (node is DockSplit s)
        {
            Link(s.First, s);
            Link(s.Second, s);
        }
    }

    private static DockGroup FindGroup(DockNode node, string panel) => node switch
    {
        DockGroup g => g.Panels.Contains(panel) ? g : null,
        DockSplit s => FindGroup(s.First, panel) ?? FindGroup(s.Second, panel),
    };

    private static DockGroup FirstGroup(DockNode node) => node switch
    {
        DockGroup g => g,
        DockSplit s => FirstGroup(s.First),
    };
}

/// <summary>Text form of a <see cref="DockLayout"/> for saving and restoring workspaces.</summary>
public static class DockLayoutText
{
    /// <summary>
    /// Serialize: <c>(row 0.27 (tabs 0 "showcase") (column 0.6 (tabs 1 "widgets" "notes") (tabs 0 "list")))</c>,
    /// followed by any floating windows: <c>(float 120 80 360 260 (tabs 0 "explorer"))</c>.
    /// Split fractions, tab order, active tabs and window rects are kept.
    /// </summary>
    public static string Serialize(this DockLayout layout)
    {
        var sb = new System.Text.StringBuilder();
        Write(sb, layout.Root);
        foreach (DockFloat f in layout.Floating)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            sb.Append(" (float ").Append(f.X.ToString("0.#", inv)).Append(' ').Append(f.Y.ToString("0.#", inv)).Append(' ')
              .Append(f.W.ToString("0.#", inv)).Append(' ').Append(f.H.ToString("0.#", inv)).Append(' ');
            WriteGroup(sb, f.Group);
            sb.Append(')');
        }
        if (layout.ClosedPanels.Count > 0)
        {
            sb.Append(" (closed");
            foreach (string panel in layout.ClosedPanels) { sb.Append(' '); WriteQuoted(sb, panel); }
            sb.Append(')');
        }
        return sb.ToString();
    }

    private static void Write(System.Text.StringBuilder sb, DockNode node)
    {
        switch (node)
        {
            case DockSplit s: WriteSplit(sb, s); break;
            case DockGroup g: WriteGroup(sb, g); break;
        }
    }

    private static void WriteSplit(System.Text.StringBuilder sb, DockSplit s)
    {
        sb.Append('(').Append(s.Vertical ? "column " : "row ")
          .Append(s.Fraction.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
        Write(sb, s.First);
        sb.Append(' ');
        Write(sb, s.Second);
        sb.Append(')');
    }

    private static void WriteGroup(System.Text.StringBuilder sb, DockGroup g)
    {
        sb.Append("(tabs ").Append(g.Active);
        foreach (string panel in g.Panels) { sb.Append(' '); WriteQuoted(sb, panel); }
        sb.Append(')');
    }

    private static void WriteQuoted(System.Text.StringBuilder sb, string text)
    {
        sb.Append('"');
        foreach (char c in text) { if (c == '"' || c == '\\') sb.Append('\\'); sb.Append(c); }
        sb.Append('"');
    }

    /// <summary>Parse text from <see cref="Serialize"/>. Returns false (and <c>null</c>) for malformed text.</summary>
    public static bool TryParse(string text, out DockLayout layout)
    {
        layout = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        int i = 0;
        try
        {
            DockNode root = ReadNode(text, ref i);
            var result = new DockLayout(root);
            // Floating windows follow the tree: (float x y w h (tabs ...)).
            Skip(text, ref i);
            while (i < text.Length)
            {
                Expect(text, ref i, '(');
                string entry = Word(text, ref i);
                if (entry == "closed")
                {
                    // (closed "a" "b"): panels closed by the user stay closed after a restart.
                    Skip(text, ref i);
                    while (i < text.Length && text[i] == '"') { result.closed.Add(Quoted(text, ref i)); Skip(text, ref i); }
                    Expect(text, ref i, ')');
                    Skip(text, ref i);
                    continue;
                }
                if (entry != "float") return false;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                float x = float.Parse(Word(text, ref i), inv), y = float.Parse(Word(text, ref i), inv);
                float w = float.Parse(Word(text, ref i), inv), h = float.Parse(Word(text, ref i), inv);
                if (ReadNode(text, ref i) is not DockGroup g || g.Panels.Count == 0) return false;
                Expect(text, ref i, ')');
                result.AddFloat(g, x, y, w, h);
                Skip(text, ref i);
            }
            layout = result;
            return true;
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            return false;
        }
    }

    private static DockNode ReadNode(string t, ref int i)
    {
        Expect(t, ref i, '(');
        string kind = Word(t, ref i);
        DockNode node;
        if (kind == "row" || kind == "column")
        {
            float f = float.Parse(Word(t, ref i), System.Globalization.CultureInfo.InvariantCulture);
            DockNode a = ReadNode(t, ref i), b = ReadNode(t, ref i);
            node = new DockSplit { Vertical = kind == "column", Fraction = Math.Clamp(f, 0.05f, 0.95f), First = a, Second = b };
        }
        else if (kind == "tabs")
        {
            var g = new DockGroup { Active = int.Parse(Word(t, ref i), System.Globalization.CultureInfo.InvariantCulture) };
            Skip(t, ref i);
            while (i < t.Length && t[i] == '"') { g.Panels.Add(Quoted(t, ref i)); Skip(t, ref i); }
            node = g;
        }
        else throw new FormatException($"Unknown dock node '{kind}'");
        Expect(t, ref i, ')');
        return node;
    }

    private static void Skip(string t, ref int i) { while (i < t.Length && char.IsWhiteSpace(t[i])) i++; }

    private static void Expect(string t, ref int i, char c)
    {
        Skip(t, ref i);
        if (i >= t.Length || t[i] != c) throw new FormatException($"Expected '{c}'");
        i++;
    }

    private static string Word(string t, ref int i)
    {
        Skip(t, ref i);
        int s = i;
        while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] != '(' && t[i] != ')' && t[i] != '"') i++;
        if (s == i) throw new FormatException("Expected a word");
        return t.Substring(s, i - s);
    }

    private static string Quoted(string t, ref int i)
    {
        i++; // opening quote
        var sb = new System.Text.StringBuilder();
        while (i < t.Length && t[i] != '"')
        {
            if (t[i] == '\\' && i + 1 < t.Length) i++;
            sb.Append(t[i++]);
        }
        if (i >= t.Length) throw new FormatException("Unterminated string");
        i++;
        return sb.ToString();
    }
}
