using System.Numerics;

namespace Cosmiria;

/// <summary>
/// A handle on one element, like a React ref: keep it in a field of your component, attach it to
/// an element with <see cref="Node.Ref"/> (CML <c>ref="NameBox"</c>) on every build, and use it
/// from listeners, effects or game code to read where the element was laid out, focus it or
/// scroll it into view.
/// <code>
/// public ElementRef NameBox;                       // CML creates it; in C#, new() it once
/// ...
/// ui.TextInput(Name).Ref(NameBox);
/// ...
/// public void Rename() { Editing = true; NameBox.Focus(); }   // shown by the next build, then focused
/// </code>
/// The handle follows the element across builds (it's re-attached each build). Attached to
/// several elements in one build, it ends up on the last one.
/// </summary>
public sealed class ElementRef
{
    private UIContext context;
    private int index = -1;     // node index in the build it was attached in
    internal int NodeIndex => index;
    private int build = -1;     // that build
    private int stableId;
    private Vector4 bounds;
    private bool pendingFocus, pendingRing, pendingScroll;

    /// <summary>
    /// True when the element is in the UI: attached in the latest build (during a build: in this
    /// one or the one before, as it may not have been reached yet).
    /// </summary>
    public bool IsMounted => context != null && build >= 0 && build >= context.Build - (context.IsBuilding ? 1 : 0);

    /// <summary>
    /// Where the element was laid out on screen, as (x, y, width, height) in window pixels, after
    /// scrolling. Read during a build it's the previous build's layout; once the element is gone,
    /// its last known place.
    /// </summary>
    public Vector4 Bounds => bounds;
    public Vector2 Position => new(bounds.X, bounds.Y);
    public Vector2 Size => new(bounds.Z, bounds.W);

    /// <summary>Whether <paramref name="point"/> (window pixels) is inside <see cref="Bounds"/>.</summary>
    public bool Contains(Vector2 point) =>
        point.X >= bounds.X && point.Y >= bounds.Y && point.X < bounds.X + bounds.Z && point.Y < bounds.Y + bounds.W;

    /// <summary>The element has keyboard focus.</summary>
    public bool HasFocus => IsMounted && stableId != 0 && context.FocusedId == stableId;

    /// <summary>
    /// Give the element keyboard focus after the next build (so it sees the changes made with
    /// it, like showing the element), scrolling it into view. A text input gets its caret at the
    /// end (or keeps the selection it has, so typing replaces it); anything else must be focusable (<see cref="Node.Focusable"/>; buttons and other widgets
    /// are). If the element isn't in that build, it happens once it's attached again.
    /// <paramref name="showRing"/> draws the focus ring, as for Tab.
    /// </summary>
    public void Focus(bool showRing = false)
    {
        pendingFocus = true;
        pendingRing = showRing;
        context?.Notify();
    }

    /// <summary>Remove keyboard focus from the element, if it has it.</summary>
    public void Blur()
    {
        pendingFocus = false;
        if (HasFocus) context.Blur();
    }

    /// <summary>Scroll the scroll containers around the element so it's visible, after the next build (as for <see cref="Focus"/>).</summary>
    public void ScrollIntoView()
    {
        pendingScroll = true;
        context?.Notify();
    }

    internal void Attach(UIContext ctx, int nodeIndex)
    {
        context = ctx;
        index = nodeIndex;
        build = ctx.Build;
        ctx.AttachRef(this);
    }

    /// <summary>After layout: record where the element is, and run what was asked for before it was ready.</summary>
    internal void Resolved(in NodeData n, bool inTree)
    {
        if (!inTree) { build = -1; index = -1; return; } // created but never put in the tree
        stableId = n.StableId;
        bounds = new Vector4(n.X, n.Y, n.W, n.H);
    }

    internal void RunPending()
    {
        if (!IsMounted) return;
        if (pendingFocus) { pendingFocus = false; context.FocusElement(index, pendingRing); }
        if (pendingScroll) { pendingScroll = false; context.ScrollElementIntoView(index); }
    }
}
