namespace Cosmiria;

/// <summary>
/// A UI component. Implement it on a <c>struct</c>: the framework owns the struct's storage
/// (like GPUI's <c>Entity&lt;T&gt;</c>) and calls <see cref="Render"/> to build its element tree,
/// which it returns. Interactions are bound with typed listeners from <see cref="EventStack"/>:
/// <code>
/// public Node Render(UIStack ui, EventStack ev) =>
///     ui.View(ui.Style().FlexRow().Gap(8)).Child([
///         ui.Text(Title),
///         Editable ? ui.Button("Edit").OnClick(ev.Listener(static (ref Card c) => c.Edit())) : Node.None,
///     ]);
/// </code>
/// Running a listener automatically schedules a re-render.
/// </summary>
public interface IComponent
{
    /// <summary>
    /// Build and return this component's element (<see cref="Node.None"/> renders nothing; several
    /// top-level elements go through <see cref="UIStack.Fragment(System.ReadOnlySpan{Node})"/>).
    /// Only the returned tree is shown: elements created but never attached to it are dropped.
    /// Must not mutate component state.
    /// </summary>
    Node Render(UIStack ui, EventStack ev);
}
