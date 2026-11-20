using System.Runtime.InteropServices;

namespace Cosmiria;

/// <summary>Assigns every bound value type a small dense id, used to index the binding stores.</summary>
internal static class BindingTypeId
{
    private static int next;
    internal static int Allocate() => Interlocked.Increment(ref next) - 1;
}

internal static class BindingTypeId<TValue>
{
    public static readonly int Id = BindingTypeId.Allocate();
}

internal abstract class BindingStore
{
    public abstract void Sweep(int build);
}

/// <summary>
/// Last value synced through each binding, keyed by (child stable id, binding index). Comparing
/// both sides against it tells which one changed since the previous build.
/// </summary>
internal sealed class BindingStore<TValue> : BindingStore
{
    public readonly Dictionary<long, (TValue Value, int Build)> Entries = new();
    private readonly List<long> dead = new();

    public override void Sweep(int build)
    {
        foreach (var pair in Entries)
        {
            if (pair.Value.Build != build) dead.Add(pair.Key);
        }
        foreach (long key in dead) Entries.Remove(key);
        dead.Clear();
    }
}

public sealed partial class UIStack
{
    /// <summary>
    /// The state of a child component, created if needed, without rendering it. Mount it with
    /// <see cref="Mount{T}(int)"/> and the same key afterwards. <paramref name="bindId"/>
    /// identifies the child for <see cref="Sync{TValue}"/>.
    /// </summary>
    public ref T State<T>(int key, out int bindId) where T : struct, IComponent => ref State<T>(key, default(T), out bindId);

    /// <inheritdoc cref="State{T}(int, out int)"/>
    public ref T State<T>(int key, scoped in T initial, out int bindId) where T : struct, IComponent
    {
        ComponentTable<T> table = context.GetTable<T>();
        bindId = ChildStableId(table, key);
        int parent = depth > 0 ? frames[depth - 1].StableId : UIContext.RootSeed;
        int slot = table.GetOrCreate(bindId, initial, context.Build, out _, parent);
        return ref table.Get(slot);
    }

    /// <inheritdoc cref="State{T}(int, out int)"/>
    public ref T State<T>(int key, Func<T> create, out int bindId) where T : struct, IComponent
    {
        ComponentTable<T> table = context.GetTable<T>();
        bindId = ChildStableId(table, key);
        int parent = depth > 0 ? frames[depth - 1].StableId : UIContext.RootSeed;
        int slot = table.GetOrCreate(bindId, default, context.Build, out bool created, parent);
        if (created) table.Get(slot) = create();
        return ref table.Get(slot);
    }

    /// <summary>
    /// Two-way binding between a parent value and a child component's field (CML's
    /// <c>&lt;Child bind:Field="ParentField"/&gt;</c>). Call before mounting the child. Whichever
    /// side changed since the last build wins: a parent change is pushed into the child; a child
    /// change is copied into the parent and a rebuild is requested, so parent elements built
    /// earlier in this pass show the new value. On first sync the parent's value wins.
    /// </summary>
    /// <param name="bindId">From <see cref="State{T}(int, out int)"/>.</param>
    /// <param name="index">Distinguishes several bindings on the same child.</param>
    public void Sync<TValue>(int bindId, int index, ref TValue parent, ref TValue child)
    {
        var entries = context.GetBindings<TValue>().Entries;
        long key = ((long)bindId << 32) | (uint)index;
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(entries, key, out bool exists);
        var comparer = EqualityComparer<TValue>.Default;
        if (!exists)
        {
            child = parent;
        }
        else if (!comparer.Equals(child, entry.Value))
        {
            parent = child;
            context.Notify();
        }
        else if (!comparer.Equals(parent, entry.Value))
        {
            child = parent;
        }
        entry = (parent, context.Build);
    }

    private int ChildStableId(ComponentTable table, int key)
    {
        int parentId = depth > 0 ? frames[depth - 1].StableId : UIContext.RootSeed;
        return Hash(parentId, table.TypeId + 1, key);
    }
}
