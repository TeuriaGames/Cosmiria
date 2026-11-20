using Riateu.Graphics;

namespace Cosmiria;

public enum NodeKind : byte { View, Text, Icon, TextInput, Image }

/// <summary>Mouse cursor shapes (<see cref="Node.Cursor"/>).</summary>
public enum CursorShape : byte { Default, Pointer, Text, ResizeX, ResizeY, Move, NotAllowed, Crosshair, ResizeDiagonal }

[Flags]
internal enum NodeFlags : uint
{
    None = 0,
    Hover = 1 << 0,
    Active = 1 << 1,
    Click = 1 << 2,
    ExplicitId = 1 << 3,
    MouseDown = 1 << 4,
    MouseUp = 1 << 5,
    HoverListener = 1 << 6,
    ClickOutside = 1 << 7,
    TextInput = 1 << 8,
    Drag = 1 << 9,
    Move = 1 << 10,
    Cursor = 1 << 11,
    /// <summary>Takes keyboard focus (Tab, click); Enter/Space activate it.</summary>
    Focusable = 1 << 12,
    Tooltip = 1 << 13,
    ContextMenu = 1 << 14,
    /// <summary>A modal backdrop: everything painted before it ignores the pointer and keyboard focus.</summary>
    Blocker = 1 << 15,
    /// <summary>Ignored by hit testing, with its subtree (CSS pointer-events: none); see <see cref="Node.IgnorePointer"/>.</summary>
    PassThrough = 1 << 16,
    /// <summary>Reached from the root in this build (set while resolving); unattached elements aren't shown.</summary>
    InTree = 1u << 31,
    /// <summary>Can become the pressed element (gets the press, tracks release).</summary>
    Pressable = Click | Active | MouseDown | MouseUp | TextInput | Drag,
    Interactive = Hover | Active | Click | MouseDown | MouseUp | HoverListener | ClickOutside | TextInput | Drag | Move | Cursor | Focusable | Tooltip | ContextMenu | Blocker,
}

/// <summary>Per-node storage in the <see cref="UIStack"/> arena (rebuilt each time the UI is dirty).</summary>
internal struct NodeData
{
    public NodeKind Kind;
    public NodeFlags Flags;
    public int Parent, FirstChild, LastChild, NextSibling;
    public int TextStart, TextLength;
    public int HoverStyle, ActiveStyle;
    public EventHandle Click, MouseDown, MouseUp, HoverChanged, Outside, Change, Submit, Drag, Move;
    // Widgets' built-in actions (on their value box), separate from user listeners so both run.
    public EventHandle OwnClick, OwnDrag, OwnOutside, OwnContext;
    public EventHandle ContextMenu;
    public string Tooltip;
    /// <summary>Resolve (paint) order, for modal blocking.</summary>
    public int Order;
    /// <summary>Sliders: current position (0..1) so arrow keys can nudge it; NaN otherwise.</summary>
    public float KeyFraction;
    public SvgIcon Icon;      // Icon, or an SVG Image
    public ImageSource Image; // raster Image
    public Texture Texture;   // GPU texture Image (e.g. a game's render target)
    public ImageFit Fit;
    public ImageSampling Sampling;
    public CursorShape Cursor;
    public bool Multiline;  // TextInput: a text area
    public int Rows;        // text area height in lines when no height is set
    public TextBuffer Buffer; // TextInput: the edited text (TextStart/TextLength hold the placeholder)
    public int StableId;

    // Layout results (absolute window coordinates).
    public float MeasuredW, MeasuredH;
    public float MinContentW;     // narrowest width without overflowing (longest word, etc.)
    public float MinContentH;     // shortest height without overflowing; nested scroll containers count as 0
    public float MeasuredAvail;   // available width the cached measurement was made for
    public bool MeasureValid;
    public bool WidthDependent;   // height changes with width (wrapping text / flex-wrap inside)
    public float X, Y, W, H;
    public float ClipX0, ClipY0, ClipX1, ClipY1;
    public float ScrollMax; // max scroll offset along the main axis (scroll containers)

    // Set while painting when this node or an ancestor has a transform: window -> layout space.
    public System.Numerics.Matrix3x2 Inverse;
    public bool Transformed;

    /// <summary>Point (window coordinates) in this node's layout space, undoing transforms.</summary>
    public readonly System.Numerics.Vector2 ToLocal(float px, float py)
    {
        var p = new System.Numerics.Vector2(px, py);
        return Transformed ? System.Numerics.Vector2.Transform(p, Inverse) : p;
    }

    public readonly bool Contains(float px, float py)
    {
        if ((Flags & NodeFlags.PassThrough) != 0) return false; // invisible to the pointer
        if (Transformed)
        {
            var p = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(px, py), Inverse);
            px = p.X; py = p.Y;
        }
        return px >= X && py >= Y && px < X + W && py < Y + H &&
            px >= ClipX0 && py >= ClipY0 && px < ClipX1 && py < ClipY1;
    }
}

/// <summary>
/// A handle to an element created by <see cref="UIStack"/>. Cheap to copy; valid only
/// during the Render pass that created it. All modifiers return the same node so they chain.
/// <para>
/// <see cref="None"/> (also <c>default</c>) is "no element": <see cref="Child(Node)"/> skips it
/// and modifiers do nothing, so branches can produce it: <c>cond ? ui.Text("Hi") : Node.None</c>.
/// </para>
/// </summary>
public readonly struct Node
{
    private readonly UIStack ui;
    internal readonly int Index;

    internal Node(UIStack ui, int index)
    {
        this.ui = ui;
        Index = index;
    }

    /// <summary>No element. Attaching it does nothing; a component returning it renders nothing.</summary>
    public static Node None => default;

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => ui is null;

    /// <summary>Append children (works with collection expressions: <c>.Child([a, b, c])</c>).</summary>
    public Node Child(params ReadOnlySpan<Node> children)
    {
        if (ui is null) return this;
        for (int i = 0; i < children.Length; i++)
        {
            if (!children[i].IsNone) ui.Attach(Index, children[i].Index);
        }
        return this;
    }

    /// <summary>
    /// Append elements and text: <c>.Child(["Name:", ui.Text(Name), ui.Button("Edit")])</c>.
    /// Strings become text elements; <see cref="None"/> entries are skipped. See <see cref="Content"/>.
    /// </summary>
    public Node Child(params ReadOnlySpan<Content> children)
    {
        if (ui is null) return this;
        for (int i = 0; i < children.Length; i++)
        {
            switch (children[i])
            {
                case Node node: ui.Attach(Index, node.Index); break;
                case string text: ui.Attach(Index, ui.Text(text).Index); break;
                case null: break; // Node.None or a null string
            }
        }
        return this;
    }

    public Node Child(Node child)
    {
        if (ui is null) return this;
        if (!child.IsNone) ui.Attach(Index, child.Index);
        return this;
    }

    /// <summary>
    /// Let the pointer pass through this element and everything in it (CSS <c>pointer-events: none</c>):
    /// no hover, clicks or occlusion, so what's underneath gets them. A drag that already started on
    /// an element inside keeps going; that's how a dragged window lets drop targets under it light up.
    /// </summary>
    public Node IgnorePointer()
    {
        if (ui is null) return this;
        ui.NodeRef(Index).Flags |= NodeFlags.PassThrough;
        return this;
    }

    /// <summary>Run <paramref name="listener"/> when this element is clicked (pressed and released on it).</summary>
    public Node OnClick(EventHandle listener)
    {
        if (ui is null) return this;
        ref NodeData n = ref ui.NodeRef(Index);
        n.Click = listener;
        n.Flags |= NodeFlags.Click;
        return this;
    }

    /// <summary>Run <paramref name="listener"/> when the left button is pressed on this element.</summary>
    public Node OnMouseDown(EventHandle listener)
    {
        if (ui is null) return this;
        ref NodeData n = ref ui.NodeRef(Index);
        n.MouseDown = listener;
        n.Flags |= NodeFlags.MouseDown;
        return this;
    }

    /// <summary>Run <paramref name="listener"/> when the left button is released over this element.</summary>
    public Node OnMouseUp(EventHandle listener)
    {
        if (ui is null) return this;
        ref NodeData n = ref ui.NodeRef(Index);
        n.MouseUp = listener;
        n.Flags |= NodeFlags.MouseUp;
        return this;
    }

    /// <summary>
    /// Run <paramref name="listener"/> when the left button is pressed on this element, and then on
    /// every pointer move until it's released, even outside the element (a drag). Use a
    /// <see cref="PointerListener{T}"/>: <see cref="PointerEvent.LocalPosition"/> is relative to
    /// this element and <see cref="PointerEvent.Size"/> is its size.
    /// </summary>
    public Node OnDrag(EventHandle listener)
    {
        if (ui is null) return this;
        ref NodeData n = ref ui.NodeRef(Index);
        n.Drag = listener;
        n.Flags |= NodeFlags.Drag;
        return this;
    }

    /// <summary>
    /// Run <paramref name="listener"/> whenever the pointer moves over this element (button up or
    /// down). Only the topmost element with a move listener under the pointer gets it. Use a
    /// <see cref="PointerListener{T}"/>: <see cref="PointerEvent.LocalPosition"/> is relative to
    /// this element (transforms undone). Each event rebuilds the UI, like any listener.
    /// </summary>
    public Node OnMove(EventHandle listener)
    {
        if (ui is null) return this;
        ref NodeData n = ref ui.NodeRef(Index);
        n.Move = listener;
        n.Flags |= NodeFlags.Move;
        return this;
    }

    /// <summary>
    /// Run <paramref name="listener"/> when the pointer enters or leaves this element. Use a
    /// <see cref="PointerListener{T}"/> and check <see cref="PointerEvent.Hovered"/>.
    /// </summary>
    public Node OnHover(EventHandle listener)
    {
        if (ui is null) return this;
        ref NodeData n = ref ui.NodeRef(Index);
        n.HoverChanged = listener;
        n.Flags |= NodeFlags.HoverListener;
        return this;
    }

    /// <summary>
    /// Run <paramref name="listener"/> when the left button is pressed outside this element and
    /// all of its descendants (including overlay children such as a dropdown list). Use it to close popups.
    /// </summary>
    public Node OnClickOutside(EventHandle listener)
    {
        if (ui is null) return this;
        ref NodeData n = ref ui.NodeRef(Index);
        n.Outside = listener;
        n.Flags |= NodeFlags.ClickOutside;
        return this;
    }

    /// <summary>TextInput: run <paramref name="listener"/> after the text changes.</summary>
    public Node OnChange(EventHandle listener)
    {
        if (ui is null) return this;
        ui.NodeRef(Index).Change = listener;
        return this;
    }

    /// <summary>TextInput: run <paramref name="listener"/> when Enter is pressed.</summary>
    public Node OnSubmit(EventHandle listener)
    {
        if (ui is null) return this;
        ui.NodeRef(Index).Submit = listener;
        return this;
    }

    /// <summary>
    /// Attach <paramref name="handle"/> to this element (do it on every build): it then reads the
    /// element's layout, focuses it and scrolls it into view. See <see cref="ElementRef"/>.
    /// </summary>
    public Node Ref(ElementRef handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (ui is null) return this;
        ui.AttachRef(handle, Index);
        return this;
    }

    /// <summary>
    /// Dim the element (<c>Theme.Disabled</c>) and make it ignore the pointer, so its listeners
    /// don't fire. With <paramref name="disabled"/> false it does nothing, for <c>.Disabled(!canSave)</c>.
    /// </summary>
    public Node Disabled(bool disabled = true) => disabled && ui is not null ? Styled(ui.Theme.Disabled).IgnorePointer() : this;

    /// <summary>Let this element take keyboard focus (Tab order follows paint order); Enter/Space click it.</summary>
    public Node Focusable()
    {
        if (ui is null) return this;
        ui.NodeRef(Index).Flags |= NodeFlags.Focusable;
        return this;
    }

    /// <summary>Show <paramref name="text"/> in a tooltip after the pointer rests on this element.</summary>
    public Node Tooltip(string text)
    {
        if (ui is null) return this;
        ref NodeData n = ref ui.NodeRef(Index);
        n.Tooltip = text;
        if (text != null) n.Flags |= NodeFlags.Tooltip;
        return this;
    }

    /// <summary>Run <paramref name="listener"/> on a right click on this element (<see cref="PointerEventKind.ContextMenu"/>).</summary>
    public Node OnContextMenu(EventHandle listener)
    {
        if (ui is null) return this;
        ref NodeData n = ref ui.NodeRef(Index);
        n.ContextMenu = listener;
        n.Flags |= NodeFlags.ContextMenu;
        return this;
    }

    internal Node OwnContextMenu(EventHandle action)
    {
        ref NodeData n = ref ui.NodeRef(Index);
        n.OwnContext = action;
        n.Flags |= NodeFlags.ContextMenu;
        return this;
    }

    internal Node Blocker()
    {
        ui.NodeRef(Index).Flags |= NodeFlags.Blocker;
        return this;
    }

    internal Node KeyFraction(float fraction)
    {
        ref NodeData n = ref ui.NodeRef(Index);
        n.KeyFraction = fraction;
        n.Flags |= NodeFlags.Focusable;
        return this;
    }

    internal Node OwnClick(EventHandle action)
    {
        ref NodeData n = ref ui.NodeRef(Index);
        n.OwnClick = action;
        n.Flags |= NodeFlags.Click;
        return this;
    }

    internal Node OwnDrag(EventHandle action)
    {
        ref NodeData n = ref ui.NodeRef(Index);
        n.OwnDrag = action;
        n.Flags |= NodeFlags.Drag;
        return this;
    }

    internal Node OwnOutside(EventHandle action)
    {
        ref NodeData n = ref ui.NodeRef(Index);
        n.OwnOutside = action;
        n.Flags |= NodeFlags.ClickOutside;
        return this;
    }

    /// <summary>Mouse cursor shown while the pointer is over this element (or dragging it).</summary>
    public Node Cursor(CursorShape cursor)
    {
        if (ui is null) return this;
        ref NodeData n = ref ui.NodeRef(Index);
        n.Cursor = cursor;
        n.Flags |= NodeFlags.Cursor;
        return this;
    }

    /// <summary>Image elements: how pixels are filtered when scaled (<see cref="ImageSampling.Nearest"/> for pixel art).</summary>
    public Node Sampling(ImageSampling sampling)
    {
        if (ui is null) return this;
        ui.NodeRef(Index).Sampling = sampling;
        return this;
    }

    /// <summary>Style overlay applied while the mouse is over this element.</summary>
    public Node Hover(in Style style)
    {
        if (ui is null) return this;
        ref NodeData n = ref ui.NodeRef(Index);
        n.HoverStyle = ui.AddExtraStyle(style);
        n.Flags |= NodeFlags.Hover;
        return this;
    }

    /// <summary>Style overlay applied while this element is being pressed.</summary>
    public Node Active(in Style style)
    {
        if (ui is null) return this;
        ref NodeData n = ref ui.NodeRef(Index);
        n.ActiveStyle = ui.AddExtraStyle(style);
        n.Flags |= NodeFlags.Active;
        return this;
    }

    /// <summary>Merge more style into this element's base style.</summary>
    public Node Styled(in Style style)
    {
        if (ui is null) return this;
        ui.StyleRef(Index).Merge(style);
        return this;
    }

    /// <summary>
    /// Give this element an explicit id (unique within its component). Hover, press and
    /// scroll state follow the id, which keeps them stable when list items move around.
    /// </summary>
    public Node Id(int id)
    {
        if (ui is null) return this;
        ui.SetExplicitId(Index, id);
        return this;
    }
}
