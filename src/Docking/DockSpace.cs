using System.Numerics;

namespace Cosmiria;

/// <summary>
/// Internal component drawing a <see cref="DockLayout"/>: splits with draggable dividers, tab
/// bars, floating windows, and (while a tab is dragged) drop targets. It owns the docking
/// listeners; panel content is added afterwards by the user's component through <see cref="DockSpace.Panel"/>.
/// </summary>
internal struct DockHost : IComponent
{
    public DockLayout Layout;
    /// <summary>Panel whose tab is being dragged out (null when none).</summary>
    public string Dragging;
    /// <summary>Group under the pointer while dragging: it shows the anchor cross.</summary>
    public DockGroup HoverGroup;
    /// <summary>The anchor under the pointer: where the panel lands on release (null: nowhere).</summary>
    public DockGroup TargetGroup;
    public DockZone TargetZone;
    /// <summary>Tab bar under the pointer, and the tab position under it (-1: past the last tab).</summary>
    public DockGroup BarGroup, TabGroup;
    public int TabIndex;
    /// <summary>Pointer x on the previous drag move: tabs reorder only in the direction it moves.</summary>
    public float LastDragX;
    /// <summary>Floating window being moved by its tab bar, and where it was grabbed.</summary>
    public DockFloat MovingFloat;
    public float GrabX, GrabY, PressX, PressY;
    /// <summary>Floating window being resized by its grip, and the grab point's offset from its corner.</summary>
    public DockFloat ResizingFloat;
    public float ResizeGrabX, ResizeGrabY;
    /// <summary>The moving window has left its spot: it lets the pointer through and shows drop targets.</summary>
    public bool WindowDrag;

    /// <summary>How far above or below its tab row a dragged tab goes before it tears off into a window.</summary>
    private const float TearOffMargin = 6;

    public Node Render(UIStack ui, EventStack ev)
    {
        DockLayout layout = Layout;
        layout.Build++;
        Node root = ui.View(ui.Style().FlexCol().Grow().MinW(0).MinH(0));
        // Overlays are layered in tree order. A window being dragged goes first, so the drop
        // targets of the docked groups (overlays too) are drawn over it; the other windows follow,
        // back to front. Explicit ids keep every part's state when this order changes.
        var floats = layout.Floating;
        DockFloat moving = WindowDrag ? MovingFloat : null;
        if (moving != null) root.Child(RenderFloat(ui, ev, moving));
        root.Child(RenderNode(ui, ev, layout.Root).Id(0x0D0C));
        for (int i = 0; i < floats.Count; i++)
            if (floats[i] != moving) root.Child(RenderFloat(ui, ev, floats[i]));
        return root;
    }

    private Node RenderNode(UIStack ui, EventStack ev, DockNode node) => node switch
    {
        DockSplit s => RenderSplit(ui, ev, s),
        DockGroup g => RenderGroup(ui, ev, g),
    };

    private Node RenderSplit(UIStack ui, EventStack ev, DockSplit s)
    {
        var split = ui.Split(Length.Percent(s.Fraction * 100), s.Vertical, ui.Style().Grow());
        split.Handle.OnDrag(ev.Listener(static (ref DockHost h, in PointerEvent e, DockSplit sp) =>
            sp.Fraction = h.Layout.DragFraction(sp, e), s));
        // The panels' minimums hold as the window shrinks too: a pane stops there, its sibling gives way.
        float handle = ui.Theme.SplitHandleSize;
        var first = Layout.MinSize(s.First, handle);
        var second = Layout.MinSize(s.Second, handle);
        if (s.Vertical)
        {
            if (first.Y > 0) split.First.Styled(ui.Style().MinH(first.Y));
            if (second.Y > 0) split.Second.Styled(ui.Style().MinH(second.Y));
        }
        else
        {
            if (first.X > 0) split.First.Styled(ui.Style().MinW(first.X));
            if (second.X > 0) split.Second.Styled(ui.Style().MinW(second.X));
        }
        split.First.Child(RenderNode(ui, ev, s.First));
        split.Second.Child(RenderNode(ui, ev, s.Second));
        return split;
    }

    /// <summary>A floating window: its group, plus a corner grip to resize it.</summary>
    private Node RenderFloat(UIStack ui, EventStack ev, DockFloat f)
    {
        Theme theme = ui.Theme;
        // An explicit id: raising a window reorders the list, and its state must follow it.
        Node window = ui.View(theme.DockFloat.Fixed().Left(f.X).Top(f.Y).W(f.W).H(f.H).Overlay()).Id(f.Id);
        // While dragged, the window is see-through to the pointer: the anchors and tab bars under it
        // light up, and it docks where it's released.
        if (WindowDrag && MovingFloat == f) window.IgnorePointer().Styled(theme.DockFloatDragging);
        // Drawn last in the window, so it's on top: it wins over the content's scrollbar there.
        Node grip = ui.View(theme.DockFloatGrip.Absolute().Right(0).Bottom(0))
            .Hover(theme.DockFloatGripHover)
            .Cursor(CursorShape.ResizeDiagonal)
            .Tooltip("Drag to resize")
            .OnDrag(ev.Listener(static (ref DockHost h, in PointerEvent e, DockFloat w) => h.ResizeFloat(e, w), f));
        return window.Child([RenderGroup(ui, ev, f.Group), grip]);
    }

    private Node RenderGroup(UIStack ui, EventStack ev, DockGroup g)
    {
        Theme theme = ui.Theme;
        bool dragging = Dragging != null || WindowDrag;
        Node group = ui.View(theme.DockGroup);
        Node bar = ui.View(theme.DockTabBar).OnHover(ev.Listener(static (ref DockHost h, in PointerEvent e, DockGroup group) =>
        {
            // Entering a tab bar: no anchor target, and the tab under the pointer is found by moves.
            if (e.Hovered) { h.BarGroup = group; h.TargetGroup = null; h.TabGroup = null; h.TabIndex = -1; }
            else if (h.BarGroup == group) h.BarGroup = null;
        }, g));
        if (dragging)
        {
            // Over the bar but not over a tab: past the last tab.
            bar.OnMove(ev.Listener(static (ref DockHost h, in PointerEvent e, DockGroup group) =>
            {
                h.BarGroup = group; h.TabGroup = group; h.TabIndex = -1;
            }, g));
        }
        if (g.Float != null)
        {
            // A floating window moves by its tab bar (the space next to the tabs).
            bar.Cursor(CursorShape.Move).OnDrag(ev.Listener(static (ref DockHost h, in PointerEvent e, DockFloat f) => h.MoveFloat(e, f), g.Float));
        }
        Node content = ui.View(theme.DockContent);
        group.Child(bar).Child(content);
        if (g.Panels.Count > 0) g.Active = Math.Clamp(g.Active, 0, g.Panels.Count - 1);

        // A tab dragged over this bar from another group: mark where it would be inserted.
        bool foreign = Dragging != null ? !g.Panels.Contains(Dragging) : g != MovingFloat?.Group;
        int marker = dragging && BarGroup == g && foreign && g.Panels.Count > 0
            ? (TabGroup == g && TabIndex >= 0 ? TabIndex : g.Panels.Count) : -1;

        for (int i = 0; i < g.Panels.Count; i++)
        {
            string panel = g.Panels[i];
            bool active = i == g.Active;
            // The id follows the panel, not its position: a tab keeps its press (and its drag)
            // when it's reordered under the pointer.
            Node header = ui.TabHeader(active, null).Styled(ui.Style().Hidden().Relative()).Id(panel.GetHashCode()); // shown by DockSpace.Panel
            header.OnClick(ev.Listener(static (ref DockHost h, (DockGroup group, int index) a) =>
            {
                a.group.Active = a.index;
                if (a.group.Float != null) h.Layout.BringToFront(a.group.Float);
            }, (g, i)));
            if (dragging)
            {
                // Move events give the tab under the pointer by its current position, every move.
                header.OnMove(ev.Listener(static (ref DockHost h, in PointerEvent e, (DockGroup group, int index) a) =>
                {
                    h.BarGroup = a.group; h.TabGroup = a.group; h.TabIndex = a.index;
                }, (g, i)));
            }
            if (marker == i) header.Child(ui.View(theme.DockTabMarker.Left(-3)));
            if (marker == g.Panels.Count && i == g.Panels.Count - 1) header.Child(ui.View(theme.DockTabMarker.Right(-3)));
            header.OnDrag(ev.Listener(static (ref DockHost h, in PointerEvent e, string id) => h.DragTab(e, id), panel));
            header.Cursor(Dragging == panel ? CursorShape.Move : CursorShape.Pointer);
            bar.Child(header);
            Layout.Slots[panel] = (header, active ? content : default, active, Layout.Build);
        }
        if (g.Float != null)
        {
            bar.Child(ui.View(ui.Style().Grow().MinW(32))); // room to grab the window
            // A close button for the whole window, only if none of its panels has to stay open.
            if (Layout.AllClosable(g))
            {
                bar.Child(ui.View(theme.DockFloatClose).Hover(theme.DockFloatCloseHover).Tooltip("Close window")
                    .OnClick(ev.Listener(static (ref DockHost h, DockFloat f) => h.Layout.Close(f), g.Float))
                    .Child(ui.Icon(Icons.X, 12)));
            }
        }

        if (dragging) AddDropZones(ui, ev, group, g);
        return group;
    }

    /// <summary>
    /// While a tab is dragged: a transparent layer over the group tracks whether the pointer is
    /// over it. The group under the pointer shows a cross of five anchors in its middle (split
    /// left, right, top, bottom, or join as a tab; a floating window only takes tabs); hovering
    /// one previews the area the panel will take, and releasing on it docks the panel there.
    /// </summary>
    private readonly void AddDropZones(UIStack ui, EventStack ev, Node group, DockGroup g)
    {
        Theme theme = ui.Theme;
        // The preview and the cross are overlays: above the panels, and above a window being dragged.
        if (TargetGroup == g) group.Child(ui.View(PreviewStyle(theme.DockDropPreview.Absolute().Overlay(), TargetZone)));

        Node layer = ui.View(ui.Style().Absolute().Inset(0).FlexCol().ItemsCenter().JustifyCenter().Gap(theme.DockAnchorGap))
            .OnHover(ev.Listener(static (ref DockHost host, in PointerEvent e, DockGroup group) =>
            {
                if (e.Hovered) host.HoverGroup = group;
                else if (host.HoverGroup == group) host.HoverGroup = null;
            }, g));
        group.Child(layer);

        // Dragging the only panel of a group onto that group would change nothing: no anchors.
        // Over a tab bar, the tab goes into the bar instead.
        bool self = Dragging != null ? g.Panels.Count == 1 && g.Panels[0] == Dragging : g == MovingFloat?.Group;
        if (HoverGroup != g || self || BarGroup != null) return;

        Node cross = ui.View(ui.Style().FlexCol().ItemsCenter().Gap(theme.DockAnchorGap).Overlay());
        layer.Child(cross);
        if (g.Float != null)
        {
            cross.Child(Anchor(ui, ev, g, DockZone.Center));
            return;
        }
        Node middle = ui.View(ui.Style().FlexRow().Gap(theme.DockAnchorGap));
        cross.Child([
            Anchor(ui, ev, g, DockZone.Top),
            middle.Child([Anchor(ui, ev, g, DockZone.Left), Anchor(ui, ev, g, DockZone.Center), Anchor(ui, ev, g, DockZone.Right)]),
            Anchor(ui, ev, g, DockZone.Bottom),
        ]);
    }

    /// <summary>One anchor: a button-like square with a diagram of where the panel goes.</summary>
    private static Node Anchor(UIStack ui, EventStack ev, DockGroup g, DockZone zone)
    {
        Theme theme = ui.Theme;
        Node fill = ui.View(PreviewStyle(theme.DockAnchorFill, zone));
        return ui.View(theme.DockAnchor)
            .Hover(theme.DockAnchorHover)
            .Tooltip(zone switch
            {
                DockZone.Center => "Dock as tab",
                DockZone.Left => "Dock left",
                DockZone.Right => "Dock right",
                DockZone.Top => "Dock top",
                _ => "Dock bottom",
            })
            .OnHover(ev.Listener(static (ref DockHost host, in PointerEvent e, (DockGroup group, DockZone zone) t) =>
            {
                if (e.Hovered) { host.TargetGroup = t.group; host.TargetZone = t.zone; }
                else if (host.TargetGroup == t.group && host.TargetZone == t.zone) host.TargetGroup = null;
            }, (g, zone)))
            .Child(ui.View(theme.DockAnchorFrame).Child(fill));
    }

    /// <summary>Place an absolutely positioned style on the part of its parent that <paramref name="zone"/> covers.</summary>
    private static Style PreviewStyle(Style s, DockZone zone)
    {
        Length half = Length.Percent(50);
        return zone switch
        {
            DockZone.Left => s.Left(0).Top(0).Bottom(0).W(half),
            DockZone.Right => s.Right(0).Top(0).Bottom(0).W(half),
            DockZone.Top => s.Left(0).Right(0).Top(0).H(half),
            DockZone.Bottom => s.Left(0).Right(0).Bottom(0).H(half),
            _ => s.Inset(0),
        };
    }

    /// <summary>
    /// A tab's drag: it starts once the pointer leaves the tab. Within its own bar the tab
    /// reorders as the pointer moves; leaving the bar tears it off into a window that follows the
    /// pointer (see <see cref="TearOff"/>), which docks or floats on release like a dragged window.
    /// </summary>
    private void DragTab(in PointerEvent e, string panel)
    {
        // Torn off: the rest of this drag moves the panel's new window.
        if (WindowDrag && MovingFloat != null && MovingFloat.Group.Panels.Contains(panel))
        {
            MoveFloat(e, MovingFloat);
            return;
        }
        if (e.Kind == PointerEventKind.DragEnd)
        {
            if (Dragging == panel) Drop(panel, e.Position);
            Dragging = null;
            TargetGroup = null;
            HoverGroup = null;
            BarGroup = TabGroup = null;
            return;
        }
        Vector2 p = e.LocalPosition;
        bool outside = p.X < 0 || p.Y < 0 || p.X > e.Size.X || p.Y > e.Size.Y;
        if (Dragging == null && outside) { Dragging = panel; LastDragX = e.Position.X; }
        if (Dragging != panel) return;

        // Within its own tab bar the tab follows the pointer: it takes the position of the tab under
        // it, but only in the direction the pointer moves, so a wider neighbor that slides under the
        // pointer after the swap doesn't swap straight back.
        float dx = e.Position.X - LastDragX;
        LastDragX = e.Position.X;
        DockGroup own = Layout.FindGroup(panel);
        if (own == null) return;
        // Off its bar: above or below the tab row, or over another tab bar.
        if (p.Y < -TearOffMargin || p.Y > e.Size.Y + TearOffMargin || (BarGroup != null && BarGroup != own))
        {
            TearOff(e, panel, own);
            return;
        }
        if (TabGroup != own || TabIndex < 0) return;
        int from = own.Panels.IndexOf(panel);
        if ((TabIndex > from && dx > 0) || (TabIndex < from && dx < 0)) Layout.MoveTab(panel, own, TabIndex);
    }

    /// <summary>
    /// The tab has left its bar: the panel goes into its own floating window under the pointer (a
    /// panel alone in a window takes the window along), and the drag carries on as a window drag.
    /// </summary>
    private void TearOff(in PointerEvent e, string panel, DockGroup own)
    {
        float grabX = Math.Clamp(e.LocalPosition.X, 8, Math.Max(8, e.Size.X - 8));
        float grabY = own.Float != null && own.Panels.Count == 1 ? e.Position.Y - own.Float.Y : 16;
        if (own.Float != null && own.Panels.Count == 1) grabX = e.Position.X - own.Float.X;
        DockFloat window = Layout.Float(panel, e.Position.X - grabX, e.Position.Y - grabY, own.Float?.W ?? 360, own.Float?.H ?? 260);
        if (window == null) return;
        Dragging = null;
        TargetGroup = HoverGroup = BarGroup = TabGroup = null;
        MovingFloat = window;
        GrabX = e.Position.X - window.X;
        GrabY = e.Position.Y - window.Y;
        WindowDrag = true;
    }

    private readonly void Drop(string panel, Vector2 at)
    {
        DockGroup source = Layout.FindGroup(panel);
        if (BarGroup != null)
        {
            // Into a tab bar (its own bar: already reordered while dragging).
            if (BarGroup != source)
                Layout.MoveTab(panel, BarGroup, TabGroup == BarGroup && TabIndex >= 0 ? TabIndex : BarGroup.Panels.Count);
        }
        else if (TargetGroup != null)
        {
            Layout.Move(panel, TargetGroup, TargetZone);
        }
        else
        {
            // Nowhere to dock: float it, with its tab under the pointer. A window keeps its size.
            DockFloat from = source?.Float;
            Layout.Float(panel, at.X - 40, at.Y - 16, from?.W ?? 360, from?.H ?? 260);
        }
    }

    /// <summary>Resize a floating window by its corner grip, keeping the point where it was grabbed under the pointer.</summary>
    private void ResizeFloat(in PointerEvent e, DockFloat f)
    {
        if (e.Kind == PointerEventKind.DragEnd) { ResizingFloat = null; return; }
        if (ResizingFloat != f)
        {
            ResizingFloat = f;
            ResizeGrabX = f.X + f.W - e.Position.X;
            ResizeGrabY = f.Y + f.H - e.Position.Y;
            Layout.BringToFront(f);
        }
        f.W = Math.Max(DockLayout.MinFloatWidth, e.Position.X + ResizeGrabX - f.X);
        f.H = Math.Max(DockLayout.MinFloatHeight, e.Position.Y + ResizeGrabY - f.Y);
    }

    /// <summary>
    /// Drag a floating window by its tab bar; grabbing it raises it. Once it has moved a few pixels
    /// it shows the drop targets under the pointer, and on release it docks there (all its tabs,
    /// or its whole group beside an anchor's side), or stays floating where it is.
    /// </summary>
    private void MoveFloat(in PointerEvent e, DockFloat f)
    {
        if (e.Kind == PointerEventKind.DragEnd)
        {
            if (WindowDrag && MovingFloat == f)
            {
                if (BarGroup != null && BarGroup != f.Group)
                    Layout.Dock(f, BarGroup, TabGroup == BarGroup && TabIndex >= 0 ? TabIndex : BarGroup.Panels.Count);
                else if (BarGroup == null && TargetGroup != null)
                    Layout.Dock(f, TargetGroup, TargetZone);
            }
            MovingFloat = null;
            WindowDrag = false;
            TargetGroup = HoverGroup = BarGroup = TabGroup = null;
            return;
        }
        if (MovingFloat != f)
        {
            MovingFloat = f;
            GrabX = e.Position.X - f.X;
            GrabY = e.Position.Y - f.Y;
            PressX = e.Position.X;
            PressY = e.Position.Y;
            WindowDrag = false;
            Layout.BringToFront(f);
        }
        // A click on the bar only raises the window; targets appear once it really moves.
        if (!WindowDrag && MathF.Abs(e.Position.X - PressX) + MathF.Abs(e.Position.Y - PressY) > 4) WindowDrag = true;
        f.X = e.Position.X - GrabX;
        f.Y = Math.Max(0, e.Position.Y - GrabY);
    }
}

/// <summary>Returned by <see cref="UIStack.DockSpace"/>: put each panel's content in with <see cref="Panel"/>.</summary>
public readonly struct DockSpace
{
    private readonly UIStack ui;
    private readonly DockLayout layout;
    public readonly Node Root;

    internal DockSpace(UIStack ui, DockLayout layout, Node root)
    {
        this.ui = ui;
        this.layout = layout;
        Root = root;
    }

    /// <summary>
    /// Declare panel <paramref name="id"/> with its tab <paramref name="title"/>. Returns true with
    /// the node to put its content in when it's the visible tab of its group (only then build it).
    /// A <paramref name="closable"/> panel gets a close button on its tab (see <see cref="DockLayout.Close(string)"/>);
    /// a floating window gets one when all its panels are closable.
    /// </summary>
    public bool Panel(string id, string title, out Node content, bool closable = false)
    {
        content = default;
        if (!layout.Titles.TryGetValue(id, out var known) || known != title) layout.Titles[id] = title;
        // The dock host drew its windows before the panels were declared: redraw if this changed.
        if (closable ? layout.ClosablePanels.Add(id) : layout.ClosablePanels.Remove(id)) ui.NotifyRebuild();
        if (!layout.Slots.TryGetValue(id, out var slot) || slot.build != layout.Build)
        {
            if (!layout.Contains(id) && !layout.IsClosed(id))
            {
                layout.Add(id);
                ui.NotifyRebuild(); // its tab appears on the next build
            }
            return false;
        }
        slot.header.Styled(ui.Style().Flex()).Child(ui.Text(title));
        if (closable)
        {
            Theme theme = ui.Theme;
            slot.header.Child(ui.View(theme.DockTabClose).Hover(theme.DockTabCloseHover).Tooltip("Close panel")
                .OwnClick(ui.WidgetListener(ClosePanel, layout, id))
                .Child(ui.Icon(Icons.X, 10)));
        }
        if (!slot.active) return false;
        content = slot.content;
        return true;
    }

    private static readonly WidgetAction<string> ClosePanel = static (object box, string id, in PointerEvent e) =>
        ((DockLayout)box).Close(id);

    public static implicit operator Node(DockSpace dock) => dock.Root;
}
