using System.Collections.Concurrent;

namespace Cosmiria;

// Async handler shapes. They get a handle to the component, not its state by ref: a ref can't
// live across an await, and the state may move or go away while the task waits.
public delegate Task AsyncListener<T>(ComponentHandle<T> self) where T : struct, IComponent;
public delegate Task AsyncListener<T, TArg>(ComponentHandle<T> self, TArg arg) where T : struct, IComponent;
public delegate Task AsyncPointerListener<T>(ComponentHandle<T> self, PointerEvent e) where T : struct, IComponent;
public delegate Task AsyncPointerListener<T, TArg>(ComponentHandle<T> self, PointerEvent e, TArg arg) where T : struct, IComponent;
public delegate Task AsyncCallbackHandler<T, TArg, TCapture>(ComponentHandle<T> self, TArg arg, TCapture captured) where T : struct, IComponent;

/// <summary>Reads a component's state (see <see cref="ComponentHandle{T}.Read{TResult}"/>).</summary>
public delegate TResult StateReader<T, TResult>(in T state) where T : struct, IComponent;

/// <summary>
/// A handle to a mounted component, valid across builds and awaits (GPUI's <c>WeakEntity</c>):
/// async listeners and callbacks get one instead of the state by ref. Code after an
/// <c>await</c> runs on the UI thread (the UI installs a <see cref="SynchronizationContext"/>),
/// so it can read and update the state right there:
/// <code>
/// async Task Save(ComponentHandle&lt;Editor&gt; self)
/// {
///     self.Update(static (ref Editor e) =&gt; e.Saving = true);
///     await File.WriteAllTextAsync(path, text, self.Unmounted);
///     self.Update(static (ref Editor e) =&gt; e.Saving = false);
/// }
/// </code>
/// Once the component unmounts, updates do nothing and <see cref="Unmounted"/> is cancelled.
/// </summary>
public readonly struct ComponentHandle<T> where T : struct, IComponent
{
    private readonly UIContext context;
    private readonly int slot, stableId;

    internal ComponentHandle(UIContext context, int slot, int stableId)
    {
        this.context = context;
        this.slot = slot;
        this.stableId = stableId;
    }

    internal UIContext Context => context;

    /// <summary>The component is still mounted (this handle's instance, not a new one in its place).</summary>
    public bool IsMounted => context != null && context.GetTable<T>().IsInstance(slot, stableId);

    /// <summary>Cancelled when the component unmounts: pass it to the work the handler awaits.</summary>
    public CancellationToken Unmounted => context?.GetTable<T>().UnmountToken(slot, stableId) ?? new CancellationToken(true);

    /// <summary>
    /// Change the state and rebuild. Returns false once the component is gone. Called off the UI
    /// thread (after <c>ConfigureAwait(false)</c>), it's queued to the UI thread and returns true.
    /// </summary>
    public bool Update(Listener<T> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (context == null) return false;
        if (!context.IsUIThread)
        {
            ComponentHandle<T> self = this;
            context.Post(() => self.Update(change));
            return true;
        }
        ComponentTable<T> table = context.GetTable<T>();
        if (!table.IsInstance(slot, stableId)) return false;
        if (context.IsBuilding) throw new InvalidOperationException("State can't change while the UI is being built (from a Render).");
        change(ref table.Get(slot));
        context.Notify();
        return true;
    }

    /// <summary><see cref="Update(Listener{T})"/> with an argument (use a static lambda: nothing is captured).</summary>
    public bool Update<TArg>(Listener<T, TArg> change, TArg arg)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (context == null) return false;
        if (!context.IsUIThread)
        {
            ComponentHandle<T> self = this;
            context.Post(() => self.Update(change, arg));
            return true;
        }
        ComponentTable<T> table = context.GetTable<T>();
        if (!table.IsInstance(slot, stableId)) return false;
        if (context.IsBuilding) throw new InvalidOperationException("State can't change while the UI is being built (from a Render).");
        change(ref table.Get(slot), arg);
        context.Notify();
        return true;
    }

    /// <summary>Read from the current state (UI thread). Throws once the component is gone; check <see cref="IsMounted"/>.</summary>
    public TResult Read<TResult>(StateReader<T, TResult> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read(in State);
    }

    /// <summary>A copy of the current state (UI thread): what an async handler method sees as <c>this</c>.</summary>
    public T Snapshot() => State;

    private ref T State
    {
        get
        {
            if (context == null || !context.IsUIThread) throw new InvalidOperationException("Read component state on the UI thread.");
            ComponentTable<T> table = context.GetTable<T>();
            if (!table.IsInstance(slot, stableId)) throw new InvalidOperationException($"The {typeof(T).Name} is no longer mounted.");
            return ref table.Get(slot);
        }
    }
}

/// <summary>
/// Runs continuations on the UI thread: <c>await</c> in a listener comes back to the thread the UI
/// runs on, at the start of the next <see cref="UIContext.Update"/> or <see cref="UIContext.Render"/>.
/// Posting wakes an app that sleeps while idle (<see cref="UIContext.Invalidated"/>).
/// </summary>
internal sealed class UISynchronizationContext : SynchronizationContext
{
    private readonly UIContext context;
    private readonly ConcurrentQueue<(SendOrPostCallback callback, object state)> queue = new();

    public UISynchronizationContext(UIContext context) { this.context = context; }

    public bool HasWork => !queue.IsEmpty;

    public override void Post(SendOrPostCallback d, object state)
    {
        queue.Enqueue((d, state));
        context.Notify(); // a rebuild is likely, and an idle app must wake to run it
    }

    public override void Send(SendOrPostCallback d, object state)
    {
        if (context.IsUIThread) { d(state); return; }
        using var done = new ManualResetEventSlim();
        Post(_ => { try { d(state); } finally { done.Set(); } }, null);
        done.Wait();
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Run what was posted so far (continuations posted while running wait for the next call).</summary>
    public void Run()
    {
        int n = queue.Count;
        for (int i = 0; i < n && queue.TryDequeue(out var item); i++)
        {
            try { item.callback(item.state); }
            catch (Exception e) { context.ReportException(e); }
        }
    }
}

/// <summary>
/// One async listener binding: the handler, the component it runs for, and its argument, stored
/// in an <see cref="EventBinder{T, TArg}"/> like any listener argument (no boxing).
/// </summary>
internal readonly struct AsyncCall<T, TArg> where T : struct, IComponent
{
    public readonly Delegate Handler;
    public readonly ComponentHandle<T> Handle;
    public readonly TArg Arg;
    public readonly byte Shape; // 0 plain, 1 arg, 2 pointer, 3 pointer + arg

    public AsyncCall(Delegate handler, ComponentHandle<T> handle, TArg arg, byte shape)
    {
        Handler = handler;
        Handle = handle;
        Arg = arg;
        Shape = shape;
    }

    public void Start(UIContext context, in PointerEvent e)
    {
        Task task;
        try
        {
            task = Shape switch
            {
                0 => ((AsyncListener<T>)Handler)(Handle),
                1 => ((AsyncListener<T, TArg>)Handler)(Handle, Arg),
                2 => ((AsyncPointerListener<T>)Handler)(Handle, e),
                _ => ((AsyncPointerListener<T, TArg>)Handler)(Handle, e, Arg),
            };
        }
        catch (Exception ex)
        {
            context.ReportException(ex);
            return;
        }
        context.Observe(task);
    }
}
