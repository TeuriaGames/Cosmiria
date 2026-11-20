using System.Runtime.CompilerServices;

namespace Cosmiria;

/// <summary>Assigns every component type a small dense id, used to index table arrays.</summary>
internal static class ComponentTypeId
{
    private static int next;
    internal static int Count => Volatile.Read(ref next);
    internal static int Allocate() => Interlocked.Increment(ref next) - 1;
}

internal static class ComponentTypeId<T> where T : struct, IComponent
{
    public static readonly int Id = ComponentTypeId.Allocate();
}

/// <summary>Type-erased access to a component table so the context can dispatch without boxing.</summary>
internal abstract class ComponentTable
{
    public int TypeId;
    public abstract Node Render(int slot, UIStack ui, EventStack ev);
    public abstract void Sweep(int build);

    /// <summary>Unmount tokens of instances freed by <see cref="Sweep"/>: cancelled after the build (not during it).</summary>
    internal static readonly List<CancellationTokenSource> Unmounting = new();
    /// <summary>Mark a slot as used in this build without rendering it through Mount.</summary>
    public abstract void Touch(int slot, int build);

    // Keep-alive bookkeeping (UIContext.KeepAlive decides which unrendered slots survive).
    public abstract int SlotCount { get; }
    public abstract bool SlotInfo(int slot, out int stableId, out int parentId, out bool keepAlive, out int lastBuild);
    public abstract void SetKeepAlive(int stableId, bool keepAlive);
}

/// <summary>
/// Dense storage for every live instance of component type <typeparamref name="T"/>.
/// Instances are identified by a stable id (hash of parent id, type and key) so state
/// survives across rebuilds; instances that were not mounted in a build are freed.
/// </summary>
internal sealed class ComponentTable<T> : ComponentTable where T : struct, IComponent
{
    private T[] items = new T[8];
    private int[] lastBuild = new int[8];
    private int[] stableIds = new int[8];
    private bool[] alive = new bool[8];
    private int[] parents = new int[8];
    private bool[] keepAlive = new bool[8];
    private int count;
    private CancellationTokenSource[] unmount = new CancellationTokenSource[8]; // created on first use
    private readonly Stack<int> free = new();
    private readonly Dictionary<int, int> lookup = new();

    public ComponentTable() { TypeId = ComponentTypeId<T>.Id; }

    public int GetOrCreate(int stableId, in T initial, int build, out bool created, int parentId = 0, bool keep = false)
    {
        if (lookup.TryGetValue(stableId, out int slot))
        {
            lastBuild[slot] = build;
            parents[slot] = parentId;
            keepAlive[slot] = keep;
            created = false;
            return slot;
        }

        if (!free.TryPop(out slot))
        {
            if (count == items.Length)
            {
                int n = items.Length * 2;
                Array.Resize(ref items, n);
                Array.Resize(ref lastBuild, n);
                Array.Resize(ref stableIds, n);
                Array.Resize(ref alive, n);
                Array.Resize(ref parents, n);
                Array.Resize(ref keepAlive, n);
                Array.Resize(ref unmount, n);
            }
            slot = count++;
        }

        parents[slot] = parentId;
        keepAlive[slot] = keep;
        items[slot] = initial;
        lastBuild[slot] = build;
        stableIds[slot] = stableId;
        alive[slot] = true;
        lookup.Add(stableId, slot);
        created = true;
        return slot;
    }

    public ref T Get(int slot) => ref items[slot];

    public override Node Render(int slot, UIStack ui, EventStack ev)
    {
        // Constrained call on the stored struct: no copy, no boxing.
        return items[slot].Render(ui, ev);
    }

    public bool IsAlive(int slot) => (uint)slot < (uint)count && alive[slot];

    /// <summary>The slot still holds the instance with this stable id (not unmounted, not reused).</summary>
    public bool IsInstance(int slot, int stableId) => IsAlive(slot) && stableIds[slot] == stableId;

    /// <summary>A token cancelled when this instance unmounts (already cancelled if it's gone).</summary>
    public CancellationToken UnmountToken(int slot, int stableId)
    {
        if (!IsInstance(slot, stableId)) return new CancellationToken(true);
        return (unmount[slot] ??= new CancellationTokenSource()).Token;
    }

    public override void Touch(int slot, int build) => lastBuild[slot] = build;

    public override int SlotCount => count;

    public override bool SlotInfo(int slot, out int stableId, out int parentId, out bool keep, out int build)
    {
        stableId = stableIds[slot]; parentId = parents[slot]; keep = keepAlive[slot]; build = lastBuild[slot];
        return alive[slot];
    }

    public override void SetKeepAlive(int stableId, bool keep)
    {
        if (lookup.TryGetValue(stableId, out int slot)) keepAlive[slot] = keep;
    }

    public override void Sweep(int build)
    {
        for (int i = 0; i < count; i++)
        {
            if (alive[i] && lastBuild[i] != build)
            {
                alive[i] = false;
                lookup.Remove(stableIds[i]);
                if (unmount[i] != null) { Unmounting.Add(unmount[i]); unmount[i] = null; }
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                {
                    items[i] = default;
                }
                free.Push(i);
            }
        }
    }
}
