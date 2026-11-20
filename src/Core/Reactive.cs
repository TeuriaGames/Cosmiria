namespace Cosmiria;

/// <summary>Something whose changes can be observed: a <see cref="BindableValue{T}"/> or a <see cref="Computed{T}"/>.</summary>
public interface IReactive
{
    /// <summary>Incremented on every change.</summary>
    int Version { get; }
}

/// <summary>
/// The reactive runtime: dependency tracking for <see cref="Computed{T}"/>, and automatic repaints
/// when a reactive value changes outside the UI (game code), so nobody calls <c>ui.Notify()</c>.
/// </summary>
public static class Reactive
{
    private static readonly List<WeakReference<UIContext>> contexts = new();
    private static int building;

    [ThreadStatic] private static List<(IReactive dep, int version)> tracking;

    internal static void Register(UIContext context)
    {
        lock (contexts) contexts.Add(new WeakReference<UIContext>(context));
    }

    internal static void Unregister(UIContext context)
    {
        lock (contexts) contexts.RemoveAll(w => !w.TryGetTarget(out var c) || c == context);
    }

    /// <summary>Changes made while a UI builds are already part of that build: no extra repaint.</summary>
    internal static void BeginBuild() => Interlocked.Increment(ref building);
    internal static void EndBuild() => Interlocked.Decrement(ref building);

    /// <summary>A reactive value changed: repaint every UI (on its next frame).</summary>
    internal static void Changed()
    {
        if (Volatile.Read(ref building) > 0) return;
        lock (contexts)
        {
            for (int i = 0; i < contexts.Count; i++)
                if (contexts[i].TryGetTarget(out var context)) context.Notify();
        }
    }

    /// <summary>Record a read for the <see cref="Computed{T}"/> being evaluated on this thread, if any.</summary>
    internal static void Track(IReactive value) => tracking?.Add((value, value.Version));

    /// <summary>Evaluate <paramref name="compute"/>, collecting the reactive values it reads into <paramref name="deps"/>.</summary>
    internal static T Collect<T>(Func<T> compute, List<(IReactive dep, int version)> deps)
    {
        var outer = tracking;
        deps.Clear();
        tracking = deps;
        try { return compute(); }
        finally { tracking = outer; }
    }
}

/// <summary>
/// A value derived from other reactive values, recomputed only when one of them changed:
/// <code>
/// public Computed&lt;float&gt; Total;   // in state: Total = new(() =&gt; Price.Value * Quantity.Value);
/// &lt;Text&gt;Total: @Total&lt;/Text&gt;
/// </code>
/// Dependencies are whatever <see cref="BindableValue{T}.Value"/> / <see cref="Value"/> reads the
/// function makes (tracked automatically, re-collected on every recompute). It's lazy: the function
/// runs when the value is read after a change. Computeds can depend on computeds.
/// </summary>
public sealed class Computed<T> : IReactive, IComputed
{
    private readonly Func<T> compute;
    private readonly List<(IReactive dep, int version)> deps = new();
    private T value;
    private bool valid;

    public Computed(Func<T> compute) => this.compute = compute ?? throw new ArgumentNullException(nameof(compute));

    /// <summary>Incremented when a recompute produced a different value.</summary>
    public int Version { get; private set; }

    public T Value
    {
        get
        {
            Refresh();
            Reactive.Track(this);
            return value;
        }
    }

    /// <summary>Recompute if a dependency changed since the last computation; returns the version.</summary>
    public int Refresh()
    {
        if (!valid || Stale()) Recompute();
        return Version;
    }

    private bool Stale()
    {
        foreach (var (dep, version) in deps)
        {
            int current = dep is IComputed c ? c.Refresh() : dep.Version; // bring computed deps up to date first
            if (current != version) return true;
        }
        return false;
    }

    private void Recompute()
    {
        T next = Reactive.Collect(compute, deps);
        bool changed = !valid || !EqualityComparer<T>.Default.Equals(value, next);
        value = next;
        valid = true;
        if (changed) Version++;
    }

    public static implicit operator T(Computed<T> computed) => computed.Value;
    public override string ToString() => Value?.ToString() ?? "";
}

/// <summary>Lets a Computed bring a Computed dependency up to date before comparing versions.</summary>
internal interface IComputed
{
    int Refresh();
}
