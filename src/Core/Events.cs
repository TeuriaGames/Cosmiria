using System.Numerics;
using System.Runtime.CompilerServices;

namespace Cosmiria;

public enum PointerEventKind : byte { Click, Down, Up, HoverEnter, HoverLeave, ClickOutside, TextChanged, Submit, Drag, Move, DragEnd, ContextMenu, Key, Effect }

/// <summary>Information about the pointer event that triggered a listener.</summary>
public readonly struct PointerEvent
{
    public readonly PointerEventKind Kind;
    /// <summary>Pointer position in window coordinates.</summary>
    public readonly Vector2 Position;
    /// <summary>Pointer position relative to the element's top-left corner.</summary>
    public readonly Vector2 LocalPosition;
    /// <summary>Size of the element the listener is attached to.</summary>
    public readonly Vector2 Size;
    /// <summary>Pointer position relative to the parent element's content box (e.g. for a divider resizing its pane).</summary>
    public readonly Vector2 ParentPosition;
    /// <summary>Size of the parent element's content box.</summary>
    public readonly Vector2 ParentSize;

    internal PointerEvent(PointerEventKind kind, Vector2 position, Vector2 local, Vector2 size, Vector2 parentPosition, Vector2 parentSize)
    {
        Kind = kind;
        Position = position;
        LocalPosition = local;
        Size = size;
        ParentPosition = parentPosition;
        ParentSize = parentSize;
    }

    /// <summary>For hover listeners: true when the pointer entered, false when it left.</summary>
    public bool Hovered => Kind != PointerEventKind.HoverLeave;
}

// Listener shapes. The component is passed by ref so the handler mutates the stored state.
// Use static lambdas (or static methods): they're cached by the compiler, so binding a
// listener allocates nothing. Per-item data goes through the TArg argument (no boxing).

public delegate void Listener<T>(ref T self) where T : struct, IComponent;
public delegate void Listener<T, TArg>(ref T self, TArg arg) where T : struct, IComponent;
public delegate void PointerListener<T>(ref T self, in PointerEvent e) where T : struct, IComponent;
public delegate void PointerListener<T, TArg>(ref T self, in PointerEvent e, TArg arg) where T : struct, IComponent;

/// <summary>
/// A bound listener: "call this handler with this argument on that component instance".
/// Create one with <see cref="EventStack.Listener{T}(Listener{T})"/> and friends. Valid for
/// the build that created it.
/// </summary>
public readonly struct EventHandle
{
    internal readonly EventBinder Binder;
    internal readonly int Index;

    internal EventHandle(EventBinder binder, int index)
    {
        Binder = binder;
        Index = index;
    }

    public bool IsValid => Binder != null;
}

internal struct NoArgs { }

/// <summary>Type-erased storage of bound listeners for one (component, argument) type pair.</summary>
internal abstract class EventBinder
{
    public abstract void Invoke(int index, UIContext context, in PointerEvent e);
    public abstract void Reset();

    /// <summary>A copy of listener <paramref name="index"/> that survives later builds (null if this kind can't).</summary>
    public virtual DetachedListener Detach(int index, UIContext context) => null;
}

/// <summary>
/// A listener kept past its build (an effect's cleanup). If its component is about to unmount,
/// <see cref="Prepare"/> keeps a copy of the state for it to run on afterwards.
/// </summary>
internal abstract class DetachedListener : EventBinder
{
    public abstract void Prepare(UIContext context, int build);
    public override void Reset() { }
}

internal sealed class DetachedListener<T, TArg>(Delegate handler, byte kind, TArg arg, int slot, int stableId) : DetachedListener
    where T : struct, IComponent
{
    private T snapshot;
    private bool unmounted;

    public override void Prepare(UIContext context, int build)
    {
        ComponentTable<T> table = context.GetTable<T>();
        if (table.IsInstance(slot, stableId) && table.SlotInfo(slot, out _, out _, out _, out int last) && last != build)
        {
            snapshot = table.Get(slot);
            unmounted = true;
        }
    }

    public override void Invoke(int index, UIContext context, in PointerEvent e)
    {
        if (unmounted)
        {
            EventBinder<T, TArg>.Call(handler, kind, ref snapshot, e, arg);
            return;
        }
        ComponentTable<T> table = context.GetTable<T>();
        if (table.IsInstance(slot, stableId)) EventBinder<T, TArg>.Call(handler, kind, ref table.Get(slot), e, arg);
    }
}

internal static class EventBinderTypeId
{
    private static int next;
    internal static int Allocate() => Interlocked.Increment(ref next) - 1;
}

internal static class EventBinderTypeId<T, TArg> where T : struct, IComponent
{
    public static readonly int Id = EventBinderTypeId.Allocate();
}

/// <summary>
/// Parallel arrays of (handler, argument, component slot), reused across builds. Arguments are
/// stored as <typeparamref name="TArg"/>, so value types are never boxed.
/// </summary>
internal sealed class EventBinder<T, TArg> : EventBinder where T : struct, IComponent
{
    internal const byte KindPlain = 0, KindArg = 1, KindPointer = 2, KindPointerArg = 3;

    private Delegate[] handlers = new Delegate[16];
    private TArg[] args = new TArg[16];
    private int[] slots = new int[16];
    private byte[] kinds = new byte[16];
    private int count;

    public int Add(Delegate handler, byte kind, int slot, TArg arg)
    {
        if (count == handlers.Length)
        {
            int n = count * 2;
            Array.Resize(ref handlers, n);
            Array.Resize(ref args, n);
            Array.Resize(ref slots, n);
            Array.Resize(ref kinds, n);
        }
        handlers[count] = handler;
        args[count] = arg;
        slots[count] = slot;
        kinds[count] = kind;
        return count++;
    }

    public override void Invoke(int index, UIContext context, in PointerEvent e)
    {
        if ((uint)index >= (uint)count) return;
        ComponentTable<T> table = context.GetTable<T>();
        int slot = slots[index];
        if (!table.IsAlive(slot)) return;

        Call(handlers[index], kinds[index], ref table.Get(slot), e, args[index]);
    }

    internal static void Call(Delegate h, byte kind, ref T self, in PointerEvent e, TArg arg)
    {
        // The kind byte was written together with the delegate, so the casts are safe.
        switch (kind)
        {
            case KindPlain: Unsafe.As<Listener<T>>(h)(ref self); break;
            case KindArg: Unsafe.As<Listener<T, TArg>>(h)(ref self, arg); break;
            case KindPointer: Unsafe.As<PointerListener<T>>(h)(ref self, e); break;
            case KindPointerArg: Unsafe.As<PointerListener<T, TArg>>(h)(ref self, e, arg); break;
        }
    }

    public override DetachedListener Detach(int index, UIContext context)
    {
        if ((uint)index >= (uint)count) return null;
        int slot = slots[index];
        context.GetTable<T>().SlotInfo(slot, out int stableId, out _, out _, out _);
        return new DetachedListener<T, TArg>(handlers[index], kinds[index], args[index], slot, stableId);
    }

    public override void Reset()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TArg>())
        {
            Array.Clear(args, 0, count); // don't keep last build's objects alive
        }
        count = 0;
    }
}
