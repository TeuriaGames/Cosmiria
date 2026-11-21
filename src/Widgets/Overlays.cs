using System.Numerics;
using System.Runtime.CompilerServices;

namespace Cosmiria;

public sealed partial class UIStack
{
    // ------------------------------------------------------------------ modal

    /// <summary>
    /// A modal dialog over a dimmed backdrop. While open, everything behind it ignores the pointer
    /// and Tab stays inside it. <paramref name="open"/> is set to false by the close button, a click
    /// on the backdrop or Escape (unless <paramref name="dismissable"/> is false). Put content in
    /// <see cref="Cosmiria.Modal.Body"/> only when <see cref="Cosmiria.Modal.IsOpen"/>, and attach the modal
    /// anywhere in your tree (it's placed over the window):
    /// <code>
    /// var modal = ui.Modal(ShowAbout, "About");
    /// root.Child(modal);
    /// if (modal.IsOpen) modal.Body.Child(ui.Text("Cosmiria 1.0"));
    /// </code>
    /// </summary>
    public Modal Modal(BindableValue<bool> open, string title = null, bool dismissable = true, in Style style = default)
    {
        Modal modal = Modal(open.Value, title, style, open, dismissable);
        if (open.Value && dismissable) modal.Backdrop.OwnClick(WidgetListener(Close, open, 0));
        return modal;
    }

    /// <summary>A controlled modal: shown while <paramref name="open"/>; close it from <see cref="Cosmiria.Modal.OnDismiss"/>.</summary>
    public Modal Modal(bool open, string title = null, in Style style = default) => Modal(open, title, style, null, true);

    private Modal Modal(bool open, string title, in Style style, BindableValue<bool> box, bool dismissable)
    {
        Theme t = context.Theme;
        // The backdrop always exists (hidden when closed) so siblings keep their identities.
        Node backdrop = View(open ? t.ModalBackdrop : t.ModalBackdrop.Hidden());
        if (!open) return new Modal(backdrop, default, default, false);
        backdrop.Blocker();
        Node dialog = View(t.ModalBox).Styled(style).OwnClick(WidgetListener(Nothing, null, 0)); // clicks inside don't dismiss
        if (title != null || (box != null && dismissable))
        {
            Node header = View(t.ModalHeader);
            header.Child(Text(title ?? "").Styled(t.ModalTitle));
            if (box != null && dismissable)
            {
                header.Child(View(t.ModalClose).Hover(t.ModalCloseHover).Focusable().Cursor(CursorShape.Pointer)
                    .OwnClick(WidgetListener(Close, box, 0)).Child(Icon(Icons.X, 16)));
            }
            dialog.Child(header);
        }
        Node body = View(t.ModalBody);
        dialog.Child(body);
        backdrop.Child(dialog);
        return new Modal(backdrop, dialog, body, true);
    }

    private static readonly WidgetAction<int> Nothing = static (object box, int _, in PointerEvent e) => { };

    // ------------------------------------------------------------------ menus

    /// <summary>
    /// A menu bar. <paramref name="open"/> holds the index of the open menu (-1: none); pass
    /// <c>ui.Own(-1)</c> to let it manage itself. Click a title to open it; while one is open,
    /// hovering another switches to it; a click outside or on an item closes it.
    /// </summary>
    public MenuBar MenuBar(BindableValue<int> open, in Style style = default)
    {
        Theme t = context.Theme;
        Node root = View(t.MenuBar).Styled(style);
        if (open.Value >= 0) root.OwnOutside(WidgetListener(SetValue<int>, open, -1));
        return new MenuBar(this, root, open);
    }

    internal Menu MenuTitle(Node bar, BindableValue<int> open, int index, string title)
    {
        Theme t = context.Theme;
        bool isOpen = open.Value == index;
        Node node = View(t.MenuTitle).Hover(t.MenuTitleHover).Focusable().Child(Text(title));
        if (isOpen) node.Styled(t.MenuTitleOpen);
        node.OwnClick(WidgetListener(ToggleMenu, open, index));
        node.OnHover(WidgetListener(SwitchMenu, open, index));
        bar.Child(node);
        Node popup = View(isOpen ? t.MenuPopup.Absolute().Left(0).Top(Length.Full).Mt(2).Overlay() : t.MenuPopup.Hidden());
        node.Child(popup);
        return new Menu(this, popup, isOpen, open, -1, Own(HashCode.Combine(index, 0x3E7), -1));
    }

    private static readonly WidgetAction<int> ToggleMenu = static (object box, int index, in PointerEvent e) =>
    {
        var open = (BindableValue<int>)box;
        open.Value = open.Value == index ? -1 : index;
    };

    private static readonly WidgetAction<int> SwitchMenu = static (object box, int index, in PointerEvent e) =>
    {
        var open = (BindableValue<int>)box;
        if (e.Hovered && open.Value >= 0) open.Value = index;
    };

    /// <summary>
    /// A context menu for <paramref name="target"/>: a right click opens it at the pointer.
    /// Add items only when <see cref="Menu.IsOpen"/>, and attach
    /// <see cref="Menu.Popup"/> anywhere (it's placed at the pointer, over everything).
    /// </summary>
    public Menu ContextMenu(Node target)
    {
        return ContextMenu(target, Own(new Vector2(float.NaN)));
    }

    /// <summary>
    /// A context menu for <paramref name="target"/>: a right click opens it at the pointer.
    /// <paramref name="position"/> holds where it's open (NaN: closed); pass <c>ui.Own(new Vector2(float.NaN))</c>
    /// to let it manage itself. Add items only when <see cref="Menu.IsOpen"/>, and attach
    /// <see cref="Menu.Popup"/> anywhere (it's placed at the pointer, over everything).
    /// </summary>
    public Menu ContextMenu(Node target, BindableValue<Vector2> position)
    {
        Theme t = context.Theme;
        target.OwnContextMenu(WidgetListener(OpenAt, position, 0));
        bool open = !float.IsNaN(position.Value.X);
        Node popup = View(open
            ? t.MenuPopup.Fixed().Left(position.Value.X).Top(position.Value.Y).Overlay()
            : t.MenuPopup.Hidden());
        if (open) popup.OwnOutside(WidgetListener(SetValue<Vector2>, position, new Vector2(float.NaN)));
        return new Menu(this, popup, open, position, -2, Own(HashCode.Combine(position.GetHashCode(), 0xC7), -1));
    }

    private static readonly WidgetAction<int> OpenAt = static (object box, int _, in PointerEvent e) =>
        ((BindableValue<Vector2>)box).Value = e.Position;

    internal Node MenuSeparatorNode() => View(context.Theme.MenuSeparator);

    internal MenuItem MenuItemNode(Node popup, string label, string shortcut, object closeBox, int closeKind, bool submenu, BindableValue<int> subOpen, int subIndex)
    {
        Theme t = context.Theme;
        Node row = View(t.MenuItem).Hover(t.MenuItemHover).Focusable();
        Node content = View(ui_Style().FlexRow().ItemsCenter().Gap(6).Grow());
        if (label != null) content.Child(Text(label));
        row.Child(content);
        if (shortcut != null) row.Child(Text(shortcut).Styled(t.MenuShortcut));
        if (submenu) row.Child(Icon(Icons.ChevronRight, 14));
        // Hovering an item opens its submenu (or closes another one).
        row.OnHover(WidgetListener(HoverSubmenu, subOpen, submenu ? subIndex : -1));
        if (!submenu) row.OwnClick(CloseMenu(closeBox, closeKind));
        popup.Child(row);
        return new MenuItem(row, content);
    }

    private Style ui_Style() => default;

    private static readonly WidgetAction<int> HoverSubmenu = static (object box, int index, in PointerEvent e) =>
    {
        if (e.Hovered) ((BindableValue<int>)box).Value = index;
    };

    internal EventHandle CloseMenu(object closeBox, int closeKind) => closeKind == -2
        ? WidgetListener(SetValue<Vector2>, closeBox, new Vector2(float.NaN))
        : WidgetListener(SetValue<int>, closeBox, -1);
}

/// <summary>Returned by <see cref="UIStack.Modal(BindableValue{bool}, string, bool, in Style)"/>.</summary>
public readonly struct Modal
{
    /// <summary>The full-window backdrop: attach this (or the modal itself) to your tree.</summary>
    public readonly Node Backdrop;
    public readonly Node Dialog;
    /// <summary>Where the content goes (only when <see cref="IsOpen"/>).</summary>
    public readonly Node Body;
    public readonly bool IsOpen;

    internal Modal(Node backdrop, Node dialog, Node body, bool open)
    {
        Backdrop = backdrop;
        Dialog = dialog;
        Body = body;
        IsOpen = open;
    }

    /// <summary>Backdrop click or Escape (controlled modals: close it here).</summary>
    public Modal OnDismiss(EventHandle listener)
    {
        if (IsOpen) Backdrop.OnClick(listener);
        return this;
    }

    public static implicit operator Node(Modal modal) => modal.Backdrop;
}

/// <summary>Returned by <see cref="UIStack.MenuBar"/>: add menus with <see cref="Menu(string)"/>.</summary>
public struct MenuBar
{
    private readonly UIStack ui;
    public readonly Node Root;
    private readonly BindableValue<int> open;
    private int count;

    internal MenuBar(UIStack ui, Node root, BindableValue<int> open)
    {
        this.ui = ui;
        Root = root;
        this.open = open;
        count = 0;
    }

    /// <summary>The next menu's title; add its items when the returned menu <see cref="Cosmiria.Menu.IsOpen"/>.</summary>
    public Menu Menu(string title) => ui.MenuTitle(Root, open, count++, title);

    public static implicit operator Node(MenuBar bar) => bar.Root;
}

/// <summary>A menu's popup (of a menu bar, a submenu or a context menu): add items while <see cref="IsOpen"/>.</summary>
public struct Menu
{
    private readonly UIStack ui;
    public readonly Node Popup;
    public readonly bool IsOpen;
    private readonly object closeBox;   // what closing sets: the bar's open index or the context menu's position
    private readonly int closeKind;
    private readonly BindableValue<int> subOpen; // which submenu of this menu is open
    private int count;

    internal Menu(UIStack ui, Node popup, bool open, object closeBox, int closeKind, BindableValue<int> subOpen)
    {
        this.ui = ui;
        Popup = popup;
        IsOpen = open;
        this.closeBox = closeBox;
        this.closeKind = closeKind;
        this.subOpen = subOpen;
        count = 0;
    }

    /// <summary>
    /// An item; clicking it closes the menu. Attach <c>.OnClick</c> to its <see cref="MenuItem.Row"/>,
    /// or put extra content (icons) in <see cref="MenuItem.Content"/>.
    /// </summary>
    public MenuItem Item(string label = null, string shortcut = null) =>
        ui.MenuItemNode(Popup, label, shortcut, closeBox, closeKind, false, subOpen, count++);

    /// <summary>
    /// A text item running <paramref name="onSelect"/>. Call it even while the menu is closed so its
    /// <paramref name="shortcut"/> works: closed, it only registers the shortcut (no elements).
    /// </summary>
    public Menu Item(string label, EventHandle onSelect, string shortcut = null)
    {
        if (shortcut != null) ui.Shortcut(shortcut, onSelect);
        if (IsOpen) Item(label, shortcut).Row.OnClick(onSelect);
        return this;
    }

    public Menu Separator()
    {
        Popup.Child(ui.MenuSeparatorNode());
        return this;
    }

    /// <summary>An item opening a submenu on hover; add its items when the returned menu is open.</summary>
    public Menu Submenu(string label)
    {
        int index = count++;
        // Closed: no elements, but items called on it still register their shortcuts.
        if (!IsOpen) return new Menu(ui, default, false, closeBox, closeKind, subOpen);
        MenuItem item = ui.MenuItemNode(Popup, label, null, closeBox, closeKind, true, subOpen, index);
        bool open = IsOpen && subOpen.Value == index;
        Theme t = ui.Theme;
        Node popup = ui.View(open ? t.MenuPopup.Absolute().Left(Length.Full).Top(-4).Ml(4).Overlay() : t.MenuPopup.Hidden());
        item.Row.Child(popup);
        return new Menu(ui, popup, open, closeBox, closeKind, ui.Own(HashCode.Combine(index, 0x5B, subOpen.GetHashCode()), -1));
    }

    public static implicit operator Node(Menu menu) => menu.Popup;
}

/// <summary>A menu item: <see cref="Row"/> takes listeners, <see cref="Content"/> holds the label.</summary>
public readonly struct MenuItem
{
    public readonly Node Row;
    public readonly Node Content;
    internal MenuItem(Node row, Node content) { Row = row; Content = content; }
    public static implicit operator Node(MenuItem item) => item.Row;
}
