using System.Runtime.CompilerServices;

namespace Cosmiria;

// Handler shapes for callbacks: the owning component by ref, the callback's argument, and
// optionally a value captured when the callback was created (like a listener's argument).
public delegate void CallbackHandler<T, TArg, TCapture>(ref T self, TArg arg, TCapture captured) where T : struct, IComponent;

/// <summary>
/// A custom event: a component exposes a <see cref="Callback{TArg}"/> field, its parent fills
/// it with a handler (<see cref="EventStack.Callback{T, TArg}(Listener{T, TArg})"/>, or
/// <c>&lt;Picker onpick="Pick($e)"/&gt;</c> in CML), and the component raises it with
/// <see cref="Invoke"/> whenever it wants: from a listener, an effect, a timer or game code.
/// <para>
/// The handler runs on the parent's stored state and schedules a rebuild, like a listener. A
/// callback stays valid as long as the component that created it is mounted, across builds;
/// after that, or when it was never set, invoking it does nothing. Main thread only.
/// </para>
/// </summary>
public readonly struct Callback<TArg>
{
    private readonly CallbackInvoker<TArg> invoker; // per (owner type, TArg, capture type) singleton
    internal readonly UIContext Context;
    internal readonly Delegate Handler;
    internal readonly int Slot, StableId;
    internal readonly CapturedValue Captured;

    internal Callback(CallbackInvoker<TArg> invoker, UIContext context, Delegate handler, int slot, int stableId, CapturedValue captured)
    {
        this.invoker = invoker;
        Context = context;
        Handler = handler;
        Slot = slot;
        StableId = stableId;
        Captured = captured;
    }

    /// <summary>True once a handler was assigned.</summary>
    public bool IsSet => invoker != null;

    /// <summary>
    /// Run the handler with <paramref name="arg"/>. Returns false if there is none, or its
    /// component is no longer mounted. Called while the UI is being built (from a Render), it
    /// runs right after that build instead, since Render must not change state.
    /// </summary>
    public bool Invoke(TArg arg)
    {
        if (invoker == null) return false;
        if (Context.IsBuilding)
        {
            Callback<TArg> self = this;
            Context.Defer(() => self.Invoke(arg));
            return true;
        }
        if (!invoker.Invoke(this, arg)) return false;
        Context.Notify();
        return true;
    }

    /// <summary>
    /// Run the handler and wait for it: an async handler's task, or a finished task for a plain
    /// one. The result is false if there's no handler or its component is gone. Exceptions from
    /// the handler come out of the await (they're not reported elsewhere).
    /// </summary>
    public Task<bool> InvokeAsync(TArg arg)
    {
        if (invoker == null) return Task.FromResult(false);
        if (Context.IsBuilding)
        {
            var done = new TaskCompletionSource<bool>();
            Callback<TArg> self = this;
            Context.Defer(() => self.InvokeAsync(arg).ContinueWith(t =>
            {
                if (t.IsFaulted) done.SetException(t.Exception.InnerExceptions);
                else if (t.IsCanceled) done.SetCanceled();
                else done.SetResult(t.Result);
            }, TaskScheduler.Default));
            return done.Task;
        }
        Task<bool> task = invoker.InvokeAsync(this, arg);
        Context.Notify();
        return task;
    }
}

/// <summary>A <see cref="Callback{TArg}"/> without an argument.</summary>
public readonly struct Callback
{
    internal readonly Callback<NoArgs> Inner;

    internal Callback(Callback<NoArgs> inner) { Inner = inner; }

    /// <inheritdoc cref="Callback{TArg}.IsSet"/>
    public bool IsSet => Inner.IsSet;

    /// <inheritdoc cref="Callback{TArg}.Invoke"/>
    public bool Invoke() => Inner.Invoke(default);

    /// <inheritdoc cref="Callback{TArg}.InvokeAsync"/>
    public Task<bool> InvokeAsync() => Inner.InvokeAsync(default);
}

/// <summary>
/// A captured value stored without boxing when it fits: small unmanaged values (ids, enums,
/// floats) go in <see cref="Bits"/>, references in <see cref="Ref"/>; larger structs are boxed.
/// </summary>
internal readonly struct CapturedValue
{
    public readonly long Bits;
    public readonly object Ref;

    private CapturedValue(long bits, object reference) { Bits = bits; Ref = reference; }

    public static CapturedValue From<T>(T value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>() && Unsafe.SizeOf<T>() <= sizeof(long))
        {
            long bits = 0;
            Unsafe.As<long, T>(ref bits) = value;
            return new CapturedValue(bits, null);
        }
        return new CapturedValue(0, value); // a reference, or a boxed large struct
    }

    public T As<T>()
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>() && Unsafe.SizeOf<T>() <= sizeof(long))
        {
            long bits = Bits;
            return Unsafe.As<long, T>(ref bits);
        }
        return Ref is null ? default : (T)Ref;
    }
}

/// <summary>Calls a callback's handler with its static types restored (one instance per type combination).</summary>
internal abstract class CallbackInvoker<TArg>
{
    public abstract bool Invoke(in Callback<TArg> callback, TArg arg);

    /// <summary>Run and return its task (plain handlers: already finished).</summary>
    public virtual Task<bool> InvokeAsync(in Callback<TArg> callback, TArg arg) =>
        Invoke(callback, arg) ? CompletedTrue : CompletedFalse;

    protected static readonly Task<bool> CompletedTrue = Task.FromResult(true), CompletedFalse = Task.FromResult(false);
}

/// <summary>Invokers of async handlers: the handler gets a <see cref="ComponentHandle{T}"/> and returns a task.</summary>
internal abstract class AsyncOwnerInvoker<T, TArg> : CallbackInvoker<TArg> where T : struct, IComponent
{
    public sealed override bool Invoke(in Callback<TArg> callback, TArg arg)
    {
        Task task = Start(callback, arg, out bool mounted);
        if (!mounted) return false;
        callback.Context.Observe(task); // fire and forget: failures are reported
        return true;
    }

    public sealed override Task<bool> InvokeAsync(in Callback<TArg> callback, TArg arg)
    {
        Task task = Start(callback, arg, out bool mounted);
        if (!mounted) return CompletedFalse;
        return Completion(task);
    }

    private static async Task<bool> Completion(Task task)
    {
        await task;
        return true;
    }

    private Task Start(in Callback<TArg> callback, TArg arg, out bool mounted)
    {
        mounted = callback.Context.GetTable<T>().IsInstance(callback.Slot, callback.StableId);
        if (!mounted) return Task.CompletedTask;
        var handle = new ComponentHandle<T>(callback.Context, callback.Slot, callback.StableId);
        try { return Call(handle, callback, arg) ?? Task.CompletedTask; }
        catch (Exception e) { return Task.FromException(e); }
    }

    protected abstract Task Call(ComponentHandle<T> self, in Callback<TArg> callback, TArg arg);
}

internal sealed class AsyncPlainInvoker<T> : AsyncOwnerInvoker<T, NoArgs> where T : struct, IComponent
{
    public static readonly AsyncPlainInvoker<T> Instance = new();
    protected override Task Call(ComponentHandle<T> self, in Callback<NoArgs> c, NoArgs arg) => Unsafe.As<AsyncListener<T>>(c.Handler)(self);
}

internal sealed class AsyncCapturedInvoker<T, TCapture> : AsyncOwnerInvoker<T, NoArgs> where T : struct, IComponent
{
    public static readonly AsyncCapturedInvoker<T, TCapture> Instance = new();
    protected override Task Call(ComponentHandle<T> self, in Callback<NoArgs> c, NoArgs arg) =>
        Unsafe.As<AsyncListener<T, TCapture>>(c.Handler)(self, c.Captured.As<TCapture>());
}

internal sealed class AsyncArgInvoker<T, TArg> : AsyncOwnerInvoker<T, TArg> where T : struct, IComponent
{
    public static readonly AsyncArgInvoker<T, TArg> Instance = new();
    protected override Task Call(ComponentHandle<T> self, in Callback<TArg> c, TArg arg) => Unsafe.As<AsyncListener<T, TArg>>(c.Handler)(self, arg);
}

internal sealed class AsyncArgCapturedInvoker<T, TArg, TCapture> : AsyncOwnerInvoker<T, TArg> where T : struct, IComponent
{
    public static readonly AsyncArgCapturedInvoker<T, TArg, TCapture> Instance = new();
    protected override Task Call(ComponentHandle<T> self, in Callback<TArg> c, TArg arg) =>
        Unsafe.As<AsyncCallbackHandler<T, TArg, TCapture>>(c.Handler)(self, arg, c.Captured.As<TCapture>());
}

// One invoker per handler shape; each restores its delegate type. Singletons: nothing allocates per render.

internal abstract class OwnerInvoker<T, TArg> : CallbackInvoker<TArg> where T : struct, IComponent
{
    public sealed override bool Invoke(in Callback<TArg> callback, TArg arg)
    {
        ComponentTable<T> table = callback.Context.GetTable<T>();
        if (!table.IsInstance(callback.Slot, callback.StableId)) return false; // unmounted (or slot reused)
        Call(ref table.Get(callback.Slot), callback, arg);
        return true;
    }

    protected abstract void Call(ref T self, in Callback<TArg> callback, TArg arg);
}

internal sealed class PlainInvoker<T> : OwnerInvoker<T, NoArgs> where T : struct, IComponent
{
    public static readonly PlainInvoker<T> Instance = new();
    protected override void Call(ref T self, in Callback<NoArgs> c, NoArgs arg) => Unsafe.As<Listener<T>>(c.Handler)(ref self);
}

internal sealed class CapturedInvoker<T, TCapture> : OwnerInvoker<T, NoArgs> where T : struct, IComponent
{
    public static readonly CapturedInvoker<T, TCapture> Instance = new();
    protected override void Call(ref T self, in Callback<NoArgs> c, NoArgs arg) =>
        Unsafe.As<Listener<T, TCapture>>(c.Handler)(ref self, c.Captured.As<TCapture>());
}

internal sealed class ArgInvoker<T, TArg> : OwnerInvoker<T, TArg> where T : struct, IComponent
{
    public static readonly ArgInvoker<T, TArg> Instance = new();
    protected override void Call(ref T self, in Callback<TArg> c, TArg arg) => Unsafe.As<Listener<T, TArg>>(c.Handler)(ref self, arg);
}

internal sealed class ArgCapturedInvoker<T, TArg, TCapture> : OwnerInvoker<T, TArg> where T : struct, IComponent
{
    public static readonly ArgCapturedInvoker<T, TArg, TCapture> Instance = new();
    protected override void Call(ref T self, in Callback<TArg> c, TArg arg) =>
        Unsafe.As<CallbackHandler<T, TArg, TCapture>>(c.Handler)(ref self, arg, c.Captured.As<TCapture>());
}
