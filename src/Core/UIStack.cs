using System.Runtime.CompilerServices;

namespace Cosmiria;

/// <summary>
/// The element builder passed to <see cref="IComponent.Render"/>. Elements live in a flat
/// arena that is reused between rebuilds, so building the tree does not allocate.
/// </summary>
public sealed partial class UIStack
{
    private struct Frame
    {
        public int Table, Slot, StableId, FirstNode;
    }

    private readonly UIContext context;
    private readonly EventStack ev;

    internal NodeData[] Nodes = new NodeData[256];
    internal Style[] Styles = new Style[256];
    internal int NodeCount;

    private Style[] extraStyles = new Style[64];
    private int extraCount;

    internal char[] Chars = new char[2048];
    internal int CharCount;

    private Frame[] frames = new Frame[16];
    private int depth;

    internal UIStack(UIContext context, EventStack ev)
    {
        this.context = context;
        this.ev = ev;
    }

    // ------------------------------------------------------------------ builders

    /// <summary>An empty style to start a fluent chain: <c>ui.Style().Flex().Padding(2)</c>.</summary>
    public Style Style() => default;

    /// <summary>
    /// Several elements as one: <see cref="Node.None"/> for none, the element itself for one, and a
    /// column (<c>flex-col</c>) holding them for more. None entries are skipped. For a component
    /// with several top-level elements: <c>return ui.Fragment([header, body]);</c>
    /// </summary>
    public Node Fragment(params ReadOnlySpan<Node> nodes)
    {
        var f = new FragmentBuilder(this);
        for (int i = 0; i < nodes.Length; i++) f.Child(nodes[i]);
        return f.Result;
    }

    /// <summary>A <see cref="Fragment(ReadOnlySpan{Node})"/> filled one element at a time (from loops and branches).</summary>
    public FragmentBuilder Fragment() => new(this);

    /// <summary>A flex container (GPUI's <c>div()</c>).</summary>
    public Node View() => new Node(this, CreateNode(NodeKind.View, default(Style)));

    /// <inheritdoc cref="View()"/>
    public Node View(in Style style) => new Node(this, CreateNode(NodeKind.View, style));

    public Node Text(string text) => Text(text.AsSpan());

    public Node Text(ReadOnlySpan<char> text)
    {
        int start = CharCount;
        AppendChars(text);
        return CreateText(start);
    }

    public Node Text(int value)
    {
        int start = CharCount;
        int written;
        while (!value.TryFormat(FreeChars, out written)) GrowChars();
        CharCount += written;
        return CreateText(start);
    }

    public Node Text(float value, ReadOnlySpan<char> format = default)
    {
        int start = CharCount;
        int written;
        while (!value.TryFormat(FreeChars, out written, format)) GrowChars();
        CharCount += written;
        return CreateText(start);
    }

    /// <summary>Interpolated text written straight into the arena: <c>ui.Text($"Count: {Num}")</c>.</summary>
    public Node Text([InterpolatedStringHandlerArgument("")] ref UITextHandler text) => CreateText(text.Start);

    /// <summary>
    /// An SVG icon, <paramref name="size"/> pixels tall (width follows the icon's aspect ratio).
    /// Icons drawn with currentColor take the inherited text color: <c>.Styled(ui.Style().TextColorOf(...))</c>.
    /// </summary>
    public Node Icon(SvgIcon icon, float size = 16) =>
        Icon(icon, size * (icon?.AspectRatio ?? 1), size);

    public Node Icon(SvgIcon icon, float width, float height)
    {
        int index = CreateNode(NodeKind.Icon, default(Style).W(width).H(height).Shrink(0));
        Nodes[index].Icon = icon;
        return new Node(this, index);
    }

    /// <summary>A clickable box with the theme's button styling and an optional label.</summary>
    public Node Button(string label = null) => Button(label, default(Style));

    public Node Button(string label, in Style style)
    {
        Theme theme = context.Theme;
        int index = CreateNode(NodeKind.View, theme.Button);
        Styles[index].Merge(style);
        var node = new Node(this, index).Hover(theme.ButtonHover).Active(theme.ButtonActive).Focusable();
        if (label != null)
        {
            node.Child(Text(label));
        }
        return node;
    }

    /// <summary>
    /// Mount a child component. Its state is owned by the framework and keyed by
    /// (this component, <typeparamref name="T"/>, <paramref name="key"/>), so use distinct keys for
    /// siblings of the same type. The component renders immediately and this returns the element its
    /// Render returned (<see cref="Node.None"/> if it renders nothing).
    /// </summary>
    public Node Mount<T>(int key = 0) where T : struct, IComponent => Mount<T>(key, default(T));

    /// <param name="initial">State used only when the component is created for the first time.</param>
    /// <param name="keepAlive">
    /// Keep the state while this parent lives, even in builds where the child isn't rendered (a
    /// hidden tab or dock panel); its own children and own values are kept with it. Without it, a
    /// component that isn't rendered is unmounted. <see cref="Forget{T}"/> drops a kept one.
    /// </param>
    public Node Mount<T>(int key, in T initial, bool keepAlive = false) where T : struct, IComponent
    {
        ComponentTable<T> table = context.GetTable<T>();
        int parentId = depth > 0 ? frames[depth - 1].StableId : UIContext.RootSeed;
        int stableId = Hash(parentId, table.TypeId + 1, key);
        int slot = table.GetOrCreate(stableId, initial, context.Build, out _, parentId, keepAlive);
        return RenderComponent(table, slot, stableId);
    }

    /// <param name="create">Called only when the component is created for the first time (use a
    /// static lambda). Prefer this over <c>initial</c> when building the state allocates.</param>
    /// <param name="keepAlive">See <see cref="Mount{T}(int, in T, bool)"/>.</param>
    public Node Mount<T>(int key, Func<T> create, bool keepAlive = false) where T : struct, IComponent
    {
        ComponentTable<T> table = context.GetTable<T>();
        int parentId = depth > 0 ? frames[depth - 1].StableId : UIContext.RootSeed;
        int stableId = Hash(parentId, table.TypeId + 1, key);
        int slot = table.GetOrCreate(stableId, default, context.Build, out bool created, parentId, keepAlive);
        if (created) table.Get(slot) = create();
        return RenderComponent(table, slot, stableId);
    }

    /// <summary>A keep-alive child with default initial state.</summary>
    public Node Mount<T>(int key, bool keepAlive) where T : struct, IComponent => Mount<T>(key, default(T), keepAlive);

    /// <summary>Drop the state of a kept-alive child (it's unmounted unless rendered in this build).</summary>
    public void Forget<T>(int key = 0) where T : struct, IComponent
    {
        ComponentTable<T> table = context.GetTable<T>();
        int parentId = depth > 0 ? frames[depth - 1].StableId : UIContext.RootSeed;
        table.SetKeepAlive(Hash(parentId, table.TypeId + 1, key), false);
    }

    // ------------------------------------------------------------------ internals

    internal void Reset()
    {
        SuspenseDepth = 0;
        suspenseUndo.Clear();
        NodeCount = 0;
        extraCount = 0;
        CharCount = 0;
        depth = 0;
    }

    internal Node RenderComponent(ComponentTable table, int slot, int stableId)
    {
        if (depth == frames.Length) Array.Resize(ref frames, depth * 2);
        int first = NodeCount;
        frames[depth++] = new Frame { Table = table.TypeId, Slot = slot, StableId = stableId, FirstNode = first };

        var prev = ev.Current;
        ev.Set(table.TypeId, slot);
        Node result;
        try
        {
            result = table.Render(slot, this, ev);
        }
        finally
        {
            ev.Set(prev.table, prev.slot);
            depth--;
        }

        if (!result.IsNone)
        {
            if (result.Index < first)
                throw new InvalidOperationException("Render must return an element it created itself (in this Render call).");
            if (Nodes[result.Index].Parent >= 0)
                throw new InvalidOperationException("Render must return its top element, not one already attached to a parent.");
        }
        return result;
    }

    internal int CreateNode(NodeKind kind, in Style style)
    {
        if (NodeCount == Nodes.Length)
        {
            Array.Resize(ref Nodes, Nodes.Length * 2);
            Array.Resize(ref Styles, Styles.Length * 2);
        }

        int index = NodeCount++;
        // Stable ids are assigned structurally after the build (parent id + sibling position,
        // or an explicit .Id()), see UIContext.Resolve.

        Nodes[index] = new NodeData
        {
            Kind = kind,
            Parent = -1,
            FirstChild = -1,
            LastChild = -1,
            NextSibling = -1,
            HoverStyle = -1,
            ActiveStyle = -1,
            KeyFraction = float.NaN,
        };
        Styles[index] = style;
        return index;
    }

    private Node CreateText(int start)
    {
        int index = CreateNode(NodeKind.Text, default(Style));
        Nodes[index].TextStart = start;
        Nodes[index].TextLength = CharCount - start;
        return new Node(this, index);
    }

    internal void Attach(int parent, int child)
    {
        ref NodeData c = ref Nodes[child];
        if (c.Parent >= 0)
        {
            throw new InvalidOperationException("An element can only have one parent. Create a new element instead of reusing it.");
        }
        if (child == parent)
        {
            throw new InvalidOperationException("An element cannot be its own child.");
        }

        c.Parent = parent;
        ref NodeData p = ref Nodes[parent];
        if (p.LastChild < 0)
        {
            p.FirstChild = child;
        }
        else
        {
            Nodes[p.LastChild].NextSibling = child;
        }
        p.LastChild = child;
    }

    internal void AttachRef(ElementRef handle, int index) => handle.Attach(context, index);

    internal void SetExplicitId(int index, int id)
    {
        int owner = depth > 0 ? frames[depth - 1].StableId : UIContext.RootSeed;
        ref NodeData n = ref Nodes[index];
        n.StableId = Hash(owner, id, 0x1b873593);
        n.Flags |= NodeFlags.ExplicitId;
    }

    internal ref NodeData NodeRef(int index) => ref Nodes[index];
    internal ref Style StyleRef(int index) => ref Styles[index];

    internal int AddExtraStyle(in Style style)
    {
        if (extraCount == extraStyles.Length) Array.Resize(ref extraStyles, extraCount * 2);
        extraStyles[extraCount] = style;
        return extraCount++;
    }

    internal ref Style ExtraStyle(int index) => ref extraStyles[index];

    internal ReadOnlySpan<char> GetText(in NodeData n) => new ReadOnlySpan<char>(Chars, n.TextStart, n.TextLength);

    internal Span<char> FreeChars => Chars.AsSpan(CharCount);

    internal void GrowChars(int min = 0)
    {
        int size = Math.Max(Chars.Length * 2, CharCount + min);
        Array.Resize(ref Chars, size);
    }

    internal void AppendChars(ReadOnlySpan<char> text)
    {
        if (CharCount + text.Length > Chars.Length) GrowChars(text.Length);
        text.CopyTo(Chars.AsSpan(CharCount));
        CharCount += text.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Hash(int a, int b, int c)
    {
        // Deterministic 32-bit mix (murmur3-style finalizer).
        uint h = (uint)a * 0x85EBCA6Bu;
        h ^= (uint)b + 0x9E3779B9u + (h << 6) + (h >> 2);
        h ^= (uint)c + 0x9E3779B9u + (h << 6) + (h >> 2);
        h ^= h >> 16; h *= 0x85EBCA6Bu;
        h ^= h >> 13; h *= 0xC2B2AE35u;
        h ^= h >> 16;
        return h == 0 ? 1 : (int)h;
    }
}

/// <summary>
/// Collects elements added one at a time into a <see cref="UIStack.Fragment(ReadOnlySpan{Node})"/>:
/// no wrapper for zero or one element, a column once there are two.
/// </summary>
public struct FragmentBuilder
{
    private readonly UIStack ui;
    private Node first, column;

    internal FragmentBuilder(UIStack ui) { this.ui = ui; first = default; column = default; }

    /// <summary>Add an element (<see cref="Node.None"/> is skipped).</summary>
    public void Child(Node node)
    {
        if (node.IsNone) return;
        if (first.IsNone) { first = node; return; }
        if (column.IsNone) column = ui.View(default(Style).FlexCol()).Child(first);
        column.Child(node);
    }

    /// <summary>The fragment's element: none, the single element, or the column.</summary>
    public readonly Node Result => column.IsNone ? first : column;

    public static implicit operator Node(in FragmentBuilder builder) => builder.Result;
}

/// <summary>Writes interpolated strings directly into the <see cref="UIStack"/> text arena.</summary>
[InterpolatedStringHandler]
public ref struct UITextHandler
{
    private readonly UIStack ui;
    internal readonly int Start;

    public UITextHandler(int literalLength, int formattedCount, UIStack ui)
    {
        this.ui = ui;
        Start = ui.CharCount;
        int need = literalLength + formattedCount * 11;
        if (ui.CharCount + need > ui.Chars.Length) ui.GrowChars(need);
    }

    public void AppendLiteral(string value) => ui.AppendChars(value);

    public void AppendFormatted(string value) => ui.AppendChars(value);

    public void AppendFormatted(ReadOnlySpan<char> value) => ui.AppendChars(value);

    public void AppendFormatted<T>(T value) => AppendFormatted(value, null);

    public void AppendFormatted<T>(T value, string format)
    {
        // Built-in numbers go through their own TryFormat. Calling it through the interface on a
        // generic T boxes the value when the JIT doesn't optimize (Debug builds, tier-0 code),
        // which cost an allocation per @int in CML text on every rebuild.
        if (typeof(T).IsValueType && TryFormatPrimitive(ref value, format)) return;
        if (value is ISpanFormattable)
        {
            int written;
            while (!((ISpanFormattable)value).TryFormat(ui.FreeChars, out written, format, null)) ui.GrowChars();
            ui.CharCount += written;
        }
        else if (value != null)
        {
            ui.AppendChars(value is IFormattable f ? f.ToString(format, null) : value.ToString());
        }
    }

    /// <summary>Format common value types without boxing. Returns false for other types.</summary>
    private bool TryFormatPrimitive<T>(ref T value, string format)
    {
        if (typeof(T) == typeof(int)) return Write(Unsafe.As<T, int>(ref value), format);
        if (typeof(T) == typeof(float)) return Write(Unsafe.As<T, float>(ref value), format);
        if (typeof(T) == typeof(double)) return Write(Unsafe.As<T, double>(ref value), format);
        if (typeof(T) == typeof(long)) return Write(Unsafe.As<T, long>(ref value), format);
        if (typeof(T) == typeof(uint)) return Write(Unsafe.As<T, uint>(ref value), format);
        if (typeof(T) == typeof(ulong)) return Write(Unsafe.As<T, ulong>(ref value), format);
        if (typeof(T) == typeof(short)) return Write(Unsafe.As<T, short>(ref value), format);
        if (typeof(T) == typeof(ushort)) return Write(Unsafe.As<T, ushort>(ref value), format);
        if (typeof(T) == typeof(byte)) return Write(Unsafe.As<T, byte>(ref value), format);
        if (typeof(T) == typeof(sbyte)) return Write(Unsafe.As<T, sbyte>(ref value), format);
        if (typeof(T) == typeof(decimal)) return Write(Unsafe.As<T, decimal>(ref value), format);
        if (typeof(T) == typeof(Half)) return Write(Unsafe.As<T, Half>(ref value), format);
        if (typeof(T) == typeof(char))
        {
            ui.AppendChars(new ReadOnlySpan<char>(in Unsafe.As<T, char>(ref value)));
            return true;
        }
        if (typeof(T) == typeof(bool))
        {
            ui.AppendChars(Unsafe.As<T, bool>(ref value) ? bool.TrueString : bool.FalseString);
            return true;
        }
        return false;
    }

    // Constrained generic call: TF is a concrete struct here, so TryFormat is called directly.
    private bool Write<TF>(TF value, string format) where TF : struct, ISpanFormattable
    {
        int written;
        while (!value.TryFormat(ui.FreeChars, out written, format, null)) ui.GrowChars();
        ui.CharCount += written;
        return true;
    }
}
