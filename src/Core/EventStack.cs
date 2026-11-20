namespace Cosmiria;

/// <summary>
/// Binds listeners to the component currently being rendered (GPUI's <c>cx.listener</c>).
/// <code>
/// ui.Button("+").OnClick(ev.Listener(static (ref Counter c) => c.Value++));
/// ui.Button("x").OnClick(ev.Listener(static (ref TodoList t, int id) => t.Remove(id), item.Id));
/// row.OnHover(ev.Listener(static (ref TodoList t, in PointerEvent e, int id) => t.Hover(id, e.Hovered), item.Id));
/// </code>
/// Prefer <c>static</c> lambdas: capturing lambdas allocate a closure on every render.
/// Pass per-item data as the argument instead; it's stored without boxing.
/// </summary>
public sealed class EventStack
{
    private readonly UIContext context;
    private int table = -1;
    private int slot;

    internal EventStack(UIContext context) => this.context = context;

    internal void Set(int table, int slot)
    {
        this.table = table;
        this.slot = slot;
    }

    internal (int table, int slot) Current => (table, slot);

    public EventHandle Listener<T>(Listener<T> handler) where T : struct, IComponent =>
        Bind<T, NoArgs>(handler, EventBinder<T, NoArgs>.KindPlain, default);

    public EventHandle Listener<T, TArg>(Listener<T, TArg> handler, TArg arg) where T : struct, IComponent =>
        Bind<T, TArg>(handler, EventBinder<T, TArg>.KindArg, arg);

    public EventHandle Listener<T>(PointerListener<T> handler) where T : struct, IComponent =>
        Bind<T, NoArgs>(handler, EventBinder<T, NoArgs>.KindPointer, default);

    public EventHandle Listener<T, TArg>(PointerListener<T, TArg> handler, TArg arg) where T : struct, IComponent =>
        Bind<T, TArg>(handler, EventBinder<T, TArg>.KindPointerArg, arg);

    // ------------------------------------------------------------------ async listeners

    /// <summary>
    /// An async listener: it gets a <see cref="ComponentHandle{T}"/> instead of the state by ref,
    /// awaits whatever it needs (code after <c>await</c> runs on the UI thread), and writes back
    /// with <see cref="ComponentHandle{T}.Update(Listener{T})"/>. Exceptions are reported through
    /// <see cref="UIContext.UnhandledException"/>; pass <see cref="ComponentHandle{T}.Unmounted"/>
    /// to the work so it stops when the component goes away.
    /// <code>
    /// ui.Button("Load").OnClick(ev.AsyncListener(static async (ComponentHandle&lt;Quotes&gt; self) =&gt;
    /// {
    ///     self.Update(static (ref Quotes q) =&gt; q.Loading = true);
    ///     string quote = await Api.Fetch(self.Unmounted);
    ///     self.Update(static (ref Quotes q, string text) =&gt; { q.Text = text; q.Loading = false; }, quote);
    /// }));
    /// </code>
    /// </summary>
    public EventHandle AsyncListener<T>(AsyncListener<T> handler) where T : struct, IComponent =>
        BindAsync<T, NoArgs>(handler, 0, default);

    /// <summary>An async listener with an argument captured now (an item id): stored without boxing.</summary>
    public EventHandle AsyncListener<T, TArg>(AsyncListener<T, TArg> handler, TArg arg) where T : struct, IComponent =>
        BindAsync<T, TArg>(handler, 1, arg);

    /// <summary>An async listener that gets the pointer event (a copy: it outlives the dispatch).</summary>
    public EventHandle AsyncListener<T>(AsyncPointerListener<T> handler) where T : struct, IComponent =>
        BindAsync<T, NoArgs>(handler, 2, default);

    /// <summary>An async listener with the pointer event and a captured argument.</summary>
    public EventHandle AsyncListener<T, TArg>(AsyncPointerListener<T, TArg> handler, TArg arg) where T : struct, IComponent =>
        BindAsync<T, TArg>(handler, 3, arg);

    /// <summary>
    /// A handle to the component that is rendering, for work that outlives this build (a task
    /// started from an effect, a timer, a subscription).
    /// </summary>
    public ComponentHandle<T> Handle<T>() where T : struct, IComponent
    {
        CheckOwner<T>("Handle");
        context.GetTable<T>().SlotInfo(slot, out int stableId, out _, out _, out _);
        return new ComponentHandle<T>(context, slot, stableId);
    }

    private EventHandle BindAsync<T, TArg>(Delegate handler, byte shape, TArg arg) where T : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(handler);
        CheckOwner<T>("AsyncListener");
        var call = new AsyncCall<T, TArg>(handler, Handle<T>(), arg, shape);
        // A regular listener whose argument is the call: it starts the task when the event fires.
        return Bind<T, AsyncCall<T, TArg>>(StartAsync<T, TArg>.Handler, EventBinder<T, AsyncCall<T, TArg>>.KindPointerArg, call);
    }

    private static class StartAsync<T, TArg> where T : struct, IComponent
    {
        public static readonly PointerListener<T, AsyncCall<T, TArg>> Handler =
            static (ref T _, in PointerEvent e, AsyncCall<T, TArg> call) => call.Start(call.Handle.Context, e);
    }

    // ------------------------------------------------------------------ callbacks

    /// <summary>
    /// A <see cref="Cosmiria.Callback"/> running <paramref name="handler"/> on the rendering
    /// component. Give it to a child (a field of its state) for the child to invoke:
    /// <code>
    /// ref var picker = ref ui.State&lt;Picker&gt;(0, out _);
    /// picker.OnClose = ev.Callback(static (ref Parent p) =&gt; p.Open = false);
    /// node.Child(ui.Mount&lt;Picker&gt;(0));
    /// </code>
    /// </summary>
    public Callback Callback<T>(Listener<T> handler) where T : struct, IComponent =>
        new(MakeCallback<T, NoArgs>(PlainInvoker<T>.Instance, handler, default));

    /// <summary>A <see cref="Cosmiria.Callback"/> with a value captured now (like a listener's argument, e.g. an item id).</summary>
    public Callback Callback<T, TCapture>(Listener<T, TCapture> handler, TCapture captured) where T : struct, IComponent =>
        new(MakeCallback<T, NoArgs>(CapturedInvoker<T, TCapture>.Instance, handler, CapturedValue.From(captured)));

    /// <summary>A <see cref="Callback{TArg}"/>: the child passes the argument when it invokes it.</summary>
    public Callback<TArg> Callback<T, TArg>(Listener<T, TArg> handler) where T : struct, IComponent =>
        MakeCallback<T, TArg>(ArgInvoker<T, TArg>.Instance, handler, default);

    /// <summary>A <see cref="Callback{TArg}"/> with a value captured now, passed to the handler after the argument.</summary>
    public Callback<TArg> Callback<T, TArg, TCapture>(CallbackHandler<T, TArg, TCapture> handler, TCapture captured) where T : struct, IComponent =>
        MakeCallback<T, TArg>(ArgCapturedInvoker<T, TArg, TCapture>.Instance, handler, CapturedValue.From(captured));

    /// <summary>
    /// An async <see cref="Cosmiria.Callback"/>: the handler gets a <see cref="ComponentHandle{T}"/>
    /// (see <see cref="AsyncListener{T}(AsyncListener{T})"/>). The child can <c>await</c> it with
    /// <see cref="Callback.InvokeAsync"/>, e.g. to show a spinner until the parent's save is done.
    /// </summary>
    public Callback Callback<T>(AsyncListener<T> handler) where T : struct, IComponent =>
        new(MakeCallback<T, NoArgs>(AsyncPlainInvoker<T>.Instance, handler, default));

    /// <summary>An async <see cref="Cosmiria.Callback"/> with a value captured now.</summary>
    public Callback Callback<T, TCapture>(AsyncListener<T, TCapture> handler, TCapture captured) where T : struct, IComponent =>
        new(MakeCallback<T, NoArgs>(AsyncCapturedInvoker<T, TCapture>.Instance, handler, CapturedValue.From(captured)));

    /// <summary>An async <see cref="Callback{TArg}"/>: the child passes the argument, and can await the handler.</summary>
    public Callback<TArg> Callback<T, TArg>(AsyncListener<T, TArg> handler) where T : struct, IComponent =>
        MakeCallback<T, TArg>(AsyncArgInvoker<T, TArg>.Instance, handler, default);

    /// <summary>An async <see cref="Callback{TArg}"/> with a value captured now, passed after the argument.</summary>
    public Callback<TArg> Callback<T, TArg, TCapture>(AsyncCallbackHandler<T, TArg, TCapture> handler, TCapture captured) where T : struct, IComponent =>
        MakeCallback<T, TArg>(AsyncArgCapturedInvoker<T, TArg, TCapture>.Instance, handler, CapturedValue.From(captured));

    private Callback<TArg> MakeCallback<T, TArg>(CallbackInvoker<TArg> invoker, Delegate handler, CapturedValue captured) where T : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(handler);
        CheckOwner<T>("Callback");
        context.GetTable<T>().SlotInfo(slot, out int stableId, out _, out _, out _);
        return new Callback<TArg>(invoker, context, handler, slot, stableId, captured);
    }

    private void CheckOwner<T>(string what) where T : struct, IComponent
    {
        if (table < 0)
            throw new InvalidOperationException($"{what}s can only be created during a component's Render.");
        if (ComponentTypeId<T>.Id != table)
            throw new InvalidOperationException(
                $"{what}<{typeof(T).Name}> must target the component that is rendering. " +
                $"A component can only bind {what.ToLowerInvariant()}s to its own state.");
    }

    private EventHandle Bind<T, TArg>(Delegate handler, byte kind, TArg arg) where T : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(handler);
        CheckOwner<T>("Listener");

        EventBinder<T, TArg> binder = context.GetBinder<T, TArg>();
        return new EventHandle(binder, binder.Add(handler, kind, slot, arg));
    }
}
