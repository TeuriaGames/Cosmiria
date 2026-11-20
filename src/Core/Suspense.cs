using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Cosmiria;

public enum ResourceStatus : byte { Idle, Loading, Ready, Failed }

/// <summary>
/// Thrown by <see cref="Resource{T}.Read"/> while the data is loading, and caught by the nearest
/// <see cref="UIStack.Suspense(Func{UIStack, Node}, Func{UIStack, Node}, Func{UIStack, Exception, Node})"/>
/// boundary (CML <c>&lt;Suspense&gt;</c>), which shows its fallback until the data arrives.
/// </summary>
public sealed class SuspendedException : Exception
{
    public IResource Resource { get; }
    internal SuspendedException(IResource resource) : base("A resource is still loading (caught by <Suspense>).") { Resource = resource; }
}

/// <summary>Thrown by <see cref="Resource{T}.Read"/> when loading failed; <see cref="Exception.InnerException"/> is why.</summary>
public sealed class ResourceFailedException : Exception
{
    internal ResourceFailedException(Exception inner) : base("Loading a resource failed: " + inner.Message, inner) { }
}

/// <summary>A <see cref="Resource{T}"/> of any type.</summary>
public interface IResource : IReactive
{
    ResourceStatus Status { get; }
    Exception Error { get; }
    void Reload();
}

/// <summary>
/// Data loaded asynchronously for the UI, read with Suspense (as with React's <c>use</c>):
/// <code>
/// // In Render, inside a Suspense boundary:
/// User user = Profile.Read();                 // suspends (the boundary shows its fallback) while loading
/// ui.Text(user.Name);
/// </code>
/// Loading starts on the first read (or <see cref="Load"/>), runs off the UI thread, and the UI
/// rebuilds when it finishes, like any reactive value. Outside a boundary use <see cref="TryGet"/>
/// or <see cref="Status"/>. <see cref="Reload"/> starts again; with <c>keepValue</c>, reads keep
/// returning the old value meanwhile instead of suspending.
/// </summary>
public class Resource<T> : IResource
{
    private readonly Func<CancellationToken, Task<T>> load;
    private CancellationTokenSource cancel;
    private int generation;
    private volatile ResourceStatus status;
    private T value;
    private bool hasValue, keepStale;
    private Exception error;
    private int version;

    /// <param name="load">Gets the data. It runs on the thread pool; the token is cancelled by <see cref="Reload"/>.</param>
    public Resource(Func<CancellationToken, Task<T>> load) { this.load = load ?? throw new ArgumentNullException(nameof(load)); }

    /// <summary>For subclasses that load from their own state (see <see cref="StartLoad"/>).</summary>
    protected Resource() { }

    public ResourceStatus Status { get { Reactive.Track(this); return status; } }
    public bool IsLoading => Status == ResourceStatus.Loading;
    public bool IsReady => Status == ResourceStatus.Ready;
    public bool IsFailed => Status == ResourceStatus.Failed;
    /// <summary>Why loading failed (null otherwise).</summary>
    public Exception Error => error;
    public int Version => version;

    /// <summary>
    /// The data. While loading it suspends: inside a Suspense boundary, the boundary shows its
    /// fallback; elsewhere it's an error (check <see cref="Status"/> or use <see cref="TryGet"/>).
    /// If loading failed it throws <see cref="ResourceFailedException"/> (a boundary with a failed
    /// view shows it).
    /// </summary>
    public T Read()
    {
        Reactive.Track(this);
        if (status == ResourceStatus.Idle) Load();
        switch (status)
        {
            case ResourceStatus.Ready: return value;
            case ResourceStatus.Failed: throw new ResourceFailedException(error);
            default:
                if (keepStale && hasValue) return value; // reloading: keep showing what we had
                if (UIStack.Building is { SuspenseDepth: > 0 }) throw new SuspendedException(this);
                throw new InvalidOperationException(UIStack.Building != null
                    ? "This resource is still loading: read it inside a <Suspense> boundary (ui.Suspense), or check Status / use TryGet."
                    : "This resource is still loading: use TryGet or Status outside of Render.");
        }
    }

    /// <summary>Shorthand for <see cref="Read"/>.</summary>
    public T Value => Read();

    /// <summary>The data if it's there (starts loading if it hasn't); never suspends or throws.</summary>
    public bool TryGet(out T result)
    {
        Reactive.Track(this);
        if (status == ResourceStatus.Idle) Load();
        result = hasValue ? value : default;
        return hasValue && (status == ResourceStatus.Ready || keepStale);
    }

    /// <summary>Start loading if it hasn't started (before the first read, to load early).</summary>
    public void Load()
    {
        if (status == ResourceStatus.Idle) Start(keepValue: false);
    }

    /// <summary>Load again. With <paramref name="keepValue"/>, reads return the current data until the new data arrives.</summary>
    public void Reload(bool keepValue)
    {
        Start(keepValue);
        Changed();
    }

    /// <summary>Load again; reads suspend until the new data arrives.</summary>
    public void Reload() => Reload(false);

    /// <summary>Set the data directly (an optimistic update, or data that arrived another way).</summary>
    public void Set(T data)
    {
        cancel?.Cancel();
        generation++;
        value = data;
        hasValue = true;
        error = null;
        status = ResourceStatus.Ready;
        Changed();
    }

    /// <summary>The load to run (subclasses with their own inputs override it).</summary>
    protected virtual Task<T> StartLoad(CancellationToken token) => load(token);

    private void Start(bool keepValue)
    {
        cancel?.Cancel();
        cancel = new CancellationTokenSource();
        CancellationToken token = cancel.Token;
        int gen = ++generation;
        keepStale = keepValue;
        if (!keepValue) hasValue = false;
        error = null;
        status = ResourceStatus.Loading;
        Task.Run(() => StartLoad(token), token).ContinueWith(t => Finish(t, gen), TaskScheduler.Default);
    }

    private void Finish(Task<T> task, int gen)
    {
        if (gen != Volatile.Read(ref generation)) return; // superseded by a reload
        if (task.IsCanceled) return;
        if (task.IsFaulted)
        {
            error = task.Exception.InnerExceptions.Count == 1 ? task.Exception.InnerException : task.Exception;
            status = ResourceStatus.Failed;
        }
        else
        {
            value = task.Result;
            hasValue = true;
            status = ResourceStatus.Ready;
        }
        Changed();
    }

    private void Changed()
    {
        Interlocked.Increment(ref version);
        Reactive.Changed(); // rebuild (and wake) every UI
    }
}

/// <summary>A resource whose loader takes inputs; <see cref="UIStack.Resource{T, TDeps}"/> reloads it when they change.</summary>
internal sealed class DepsResource<T, TDeps> : Resource<T>
{
    private readonly Func<TDeps, CancellationToken, Task<T>> load;
    public TDeps Deps;

    public DepsResource(Func<TDeps, CancellationToken, Task<T>> load, TDeps deps) { this.load = load; Deps = deps; }

    protected override Task<T> StartLoad(CancellationToken token) => load(Deps, token);
}

/// <summary>Where a Suspense boundary started: what to cut back to when its content suspends.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct SuspenseScope
{
    internal readonly int Nodes, Extra, Chars, Undo;
    internal readonly (int effects, int shortcuts, int deferred, int refs) Lists;

    internal SuspenseScope(int nodes, int extra, int chars, int undo, (int, int, int, int) lists)
    {
        Nodes = nodes; Extra = extra; Chars = chars; Undo = undo; Lists = lists;
    }
}

public sealed partial class UIStack
{
    /// <summary>The UI being built on this thread (null outside a build): resources check it for a boundary.</summary>
    [ThreadStatic] internal static UIStack Building;

    /// <summary>Suspense boundaries the build is inside.</summary>
    internal int SuspenseDepth;

    /// <summary>Changes made inside boundaries that a rollback undoes (effects marked as run).</summary>
    private readonly List<Action> suspenseUndo = new();

    internal void RecordUndo(Action undo)
    {
        if (SuspenseDepth > 0) suspenseUndo.Add(undo);
    }

    /// <summary>
    /// A Suspense boundary (React's <c>&lt;Suspense&gt;</c>): builds <paramref name="content"/>, and if a
    /// <see cref="Resource{T}"/> read inside it is still loading, throws away what was built and
    /// shows <paramref name="fallback"/> instead, until the data arrives (the UI rebuilds by itself).
    /// Components mounted inside keep their state meanwhile. <paramref name="failed"/>, if given,
    /// shows a resource's loading error; without it the error goes up to an outer boundary.
    /// Use static lambdas; mount a component in <paramref name="content"/> to reach its state:
    /// <code>
    /// ui.Suspense(static ui =&gt; ui.Mount&lt;Profile&gt;(), static ui =&gt; ui.Text("Loading..."));
    /// </code>
    /// The result is a column holding the content or the fallback.
    /// </summary>
    public Node Suspense(Func<UIStack, Node> content, Func<UIStack, Node> fallback, Func<UIStack, Exception, Node> failed = null) =>
        Suspense(content, static (ui, c) => c(ui), fallback, failed);

    /// <summary>A Suspense boundary whose content takes an argument (passed without a closure).</summary>
    public Node Suspense<TArg>(TArg arg, Func<UIStack, TArg, Node> content, Func<UIStack, Node> fallback, Func<UIStack, Exception, Node> failed = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        Node box = View(Style().FlexCol());
        SuspenseScope scope = BeginSuspense();
        try
        {
            box.Child(content(this, arg));
            EndSuspense(scope);
        }
        catch (SuspendedException)
        {
            AbortSuspense(scope);
            if (fallback != null) box.Child(fallback(this));
        }
        catch (ResourceFailedException e) when (failed != null)
        {
            AbortSuspense(scope);
            box.Child(failed(this, e.InnerException));
        }
        return box;
    }

    /// <summary>Start a boundary (CML's generated code). Pair with <see cref="EndSuspense"/> or <see cref="AbortSuspense"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public SuspenseScope BeginSuspense()
    {
        SuspenseDepth++;
        return new SuspenseScope(NodeCount, extraCount, CharCount, suspenseUndo.Count, context.BuildListCounts());
    }

    /// <summary>The boundary's content was built without suspending.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void EndSuspense(SuspenseScope scope)
    {
        SuspenseDepth--;
    }

    /// <summary>
    /// The content suspended (or failed): forget everything built since <see cref="BeginSuspense"/>,
    /// as if it had never been rendered, so the fallback takes its place.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AbortSuspense(SuspenseScope scope)
    {
        SuspenseDepth--;
        int mark = scope.Nodes;
        // Elements made before the boundary may have been given children made after it: unlink them.
        for (int i = 0; i < mark; i++)
        {
            ref NodeData n = ref Nodes[i];
            if (n.Parent >= mark) { n.Parent = -1; n.NextSibling = -1; } // was put inside the boundary
            if (n.FirstChild < mark) // children before the mark stay; cut the list at the first later one
            {
                if (n.FirstChild < 0 || n.LastChild < mark) continue;
                int c = n.FirstChild;
                while (Nodes[c].NextSibling >= 0 && Nodes[c].NextSibling < mark) c = Nodes[c].NextSibling;
                Nodes[c].NextSibling = -1;
                n.LastChild = c;
            }
            else
            {
                n.FirstChild = n.LastChild = -1;
            }
        }
        NodeCount = mark;
        extraCount = scope.Extra;
        CharCount = scope.Chars;
        // Effects that became due inside run once the content really shows.
        for (int i = suspenseUndo.Count - 1; i >= scope.Undo; i--) suspenseUndo[i]();
        suspenseUndo.RemoveRange(scope.Undo, suspenseUndo.Count - scope.Undo);
        context.RollbackBuildLists(scope.Lists);
    }

    // ------------------------------------------------------------------ owned resources

    /// <summary>
    /// A <see cref="Resource{T}"/> owned by this call site (like <see cref="Own{T}(T, string, int)"/>):
    /// created and started on first use, kept while the call keeps happening.
    /// </summary>
    public Resource<T> Resource<T>(Func<CancellationToken, Task<T>> load, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        ResourceAt(HashCode.Combine(file, line), load);

    /// <summary>
    /// An owned <see cref="Resource{T}"/> loading from <paramref name="deps"/>: when they change
    /// (compared with Equals; use a tuple for several), it loads again. Use a static lambda.
    /// </summary>
    public Resource<T> Resource<T, TDeps>(TDeps deps, Func<TDeps, CancellationToken, Task<T>> load,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        ResourceAt(HashCode.Combine(file, line), deps, load);

    /// <summary>Owned resource for <paramref name="site"/> (CML's position).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Resource<T> ResourceAt<T>(int site, Func<CancellationToken, Task<T>> load)
    {
        BindableValue<Resource<T>> box = Own(site, (Resource<T>)null);
        if (box.Value == null) box.Value = new Resource<T>(load);
        return box.Value;
    }

    /// <inheritdoc cref="ResourceAt{T}(int, Func{CancellationToken, Task{T}})"/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Resource<T> ResourceAt<T, TDeps>(int site, TDeps deps, Func<TDeps, CancellationToken, Task<T>> load)
    {
        BindableValue<Resource<T>> box = Own(site, (Resource<T>)null);
        if (box.Value is not DepsResource<T, TDeps> r)
        {
            r = new DepsResource<T, TDeps>(load, deps);
            box.Value = r;
        }
        else if (!EqualityComparer<TDeps>.Default.Equals(r.Deps, deps))
        {
            r.Deps = deps;
            r.Reload(); // reads in this build already wait for the new data
        }
        return r;
    }
}
