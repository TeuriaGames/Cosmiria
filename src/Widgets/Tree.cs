using System.Numerics;

namespace Cosmiria;

public sealed partial class UIStack
{
    /// <summary>
    /// A tree view. <paramref name="selected"/> holds the selected item's value (clicking a row
    /// selects it); pass <c>ui.Own((string)null)</c> to let it manage itself. Add items with
    /// <see cref="Cosmiria.Tree.Item"/> and their children with <see cref="TreeItem.Item"/> while
    /// <see cref="TreeItem.IsExpanded"/>:
    /// <code>
    /// var tree = ui.Tree(Selected);
    /// var assets = tree.Item("Assets", expanded: AssetsOpen);
    /// if (assets.IsExpanded) assets.Item("logo.svg", leaf: true);
    /// </code>
    /// </summary>
    public Tree Tree(BindableValue<string> selected, in Style style = default) =>
        new(this, View(context.Theme.Tree).Styled(style), selected, 0);

    internal TreeItem TreeItemNode(Node parent, BindableValue<string> selected, int depth, string label, string value,
        BindableValue<bool> expanded, bool leaf)
    {
        Theme t = context.Theme;
        value ??= label;
        bool isSelected = selected != null && selected.Value == value;
        bool isExpanded = !leaf && expanded != null && expanded.Value;

        Node wrapper = View(Style().FlexCol());
        Node row = View(t.TreeRow.Pl(4 + depth * t.TreeIndent)).Focusable();
        if (isSelected) row.Styled(t.TreeRowSelected); else row.Hover(t.TreeRowHover);
        if (selected != null) row.OwnClick(WidgetListener(SetValue<string>, selected, value));

        if (leaf || expanded == null) row.Child(View(Style().Size(14).Shrink(0)));
        else
        {
            // The chevron toggles; so does Enter on a focused row via its own click (selection).
            Node chevron = Icon(isExpanded ? Icons.ChevronDown : Icons.ChevronRight, 14).Cursor(CursorShape.Pointer);
            chevron.OwnClick(WidgetListener(ToggleExpanded, expanded, 0));
            row.Child(chevron);
        }
        Node content = View(Style().FlexRow().ItemsCenter().Gap(6).Grow());
        if (label != null) content.Child(Text(label));
        row.Child(content);
        wrapper.Child(row);

        Node children = isExpanded ? View(Style().FlexCol()) : default;
        if (isExpanded) wrapper.Child(children);
        parent.Child(wrapper);
        return new TreeItem(this, row, content, children, selected, value, depth, isExpanded);
    }

    private static readonly WidgetAction<int> ToggleExpanded = static (object box, int _, in PointerEvent e) =>
        ((BindableValue<bool>)box).Value = !((BindableValue<bool>)box).Value;
}

/// <summary>Returned by <see cref="UIStack.Tree"/>.</summary>
public readonly struct Tree
{
    private readonly UIStack ui;
    public readonly Node Root;
    public readonly BindableValue<string> Selected;
    private readonly int depth;

    internal Tree(UIStack ui, Node root, BindableValue<string> selected, int depth)
    {
        this.ui = ui;
        Root = root;
        Selected = selected;
        this.depth = depth;
    }

    /// <summary>
    /// A top-level item. <paramref name="value"/> (default: the label) is what selection stores.
    /// <paramref name="expanded"/> is its open state (<c>ui.Own(false)</c> for its own); <paramref name="leaf"/> hides the chevron.
    /// </summary>
    public TreeItem Item(string label, string value = null, BindableValue<bool> expanded = null, bool leaf = false) =>
        ui.TreeItemNode(Root, Selected, depth, label, value, expanded, leaf);

    public static implicit operator Node(Tree tree) => tree.Root;
}

/// <summary>A tree row: add children with <see cref="Item"/> while <see cref="IsExpanded"/>.</summary>
public readonly struct TreeItem
{
    private readonly UIStack ui;
    /// <summary>The clickable row (selection); attach listeners here.</summary>
    public readonly Node Row;
    /// <summary>The label area (icons, extra text).</summary>
    public readonly Node Content;
    /// <summary>Where children go (only when expanded).</summary>
    public readonly Node Children;
    public readonly bool IsExpanded;
    /// <summary>What selecting this item stores (its label unless given).</summary>
    public readonly string Value;
    private readonly BindableValue<string> selected;
    private readonly int depth;

    internal TreeItem(UIStack ui, Node row, Node content, Node children, BindableValue<string> selected, string value, int depth, bool expanded)
    {
        this.ui = ui;
        Row = row;
        Content = content;
        Children = children;
        this.selected = selected;
        Value = value;
        this.depth = depth;
        IsExpanded = expanded;
    }

    /// <summary>A child item (call only when <see cref="IsExpanded"/>).</summary>
    public TreeItem Item(string label, string value = null, BindableValue<bool> expanded = null, bool leaf = false) =>
        ui.TreeItemNode(Children, selected, depth + 1, label, value, expanded, leaf);

    /// <summary>
    /// This item's own right-click menu (see <see cref="UIStack.ContextMenu"/>). A right click also
    /// selects the item, so the highlight shows what the menu acts on; it takes the place of an
    /// <see cref="Node.OnContextMenu"/> listener on <see cref="Row"/>, so set one of the two.
    /// </summary>
    public Menu ContextMenu(BindableValue<Vector2> position)
    {
        if (selected != null) Row.OnContextMenu(ui.WidgetListener(UIStack.SetValue<string>, selected, Value));
        return ui.ContextMenu(Row, position);
    }

    public static implicit operator Node(TreeItem item) => item.Row;
}
