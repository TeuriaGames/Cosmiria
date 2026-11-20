using System.Collections.Concurrent;
using Cosmiria;

namespace Cosmiria.Mvu;

/// <summary>
/// A side effect for the runtime to perform, described by <c>Update</c> instead of done by it, so
/// <c>Update</c> stays a pure function. When the work finishes, its message is dispatched.
/// </summary>
public readonly struct Cmd<TMsg>
{
    private readonly Func<Task<TMsg>> run;
    private readonly Cmd<TMsg>[] batch;

    private Cmd(Func<Task<TMsg>> run, Cmd<TMsg>[] batch) { this.run = run; this.batch = batch; }

    /// <summary>No side effect.</summary>
    public static Cmd<TMsg> None => default;

    /// <summary>Run async work; its result is the next message.</summary>
    public static Cmd<TMsg> Of(Func<Task<TMsg>> run) => new(run, null);

    /// <summary>Dispatch <paramref name="msg"/> after <paramref name="delay"/> (a timer).</summary>
    public static Cmd<TMsg> After(TimeSpan delay, TMsg msg) => Of(async () =>
    {
        await Task.Delay(delay).ConfigureAwait(false);
        return msg;
    });

    /// <summary>Several commands at once.</summary>
    public static Cmd<TMsg> Batch(params Cmd<TMsg>[] commands) => new(null, commands);

    internal void Start(Action<TMsg> deliver)
    {
        if (batch != null)
        {
            foreach (var cmd in batch) cmd.Start(deliver);
            return;
        }
        if (run == null) return;
        Func<Task<TMsg>> work = run;
        Task.Run(async () =>
        {
            try { deliver(await work().ConfigureAwait(false)); }
            catch (Exception e) { Console.Error.WriteLine($"[mvu] command failed: {e}"); }
        });
    }
}

/// <summary>
/// The Model-View-Update loop. Holds the one immutable <typeparamref name="TModel"/>, feeds every
/// message through <c>update</c>, runs the returned commands and queues their results for
/// <see cref="Pump"/>. Views read <see cref="Model"/>, which is reactive: a new model repaints them.
/// Every (message, model) pair is kept, so the app can travel back to any earlier state.
/// </summary>
public sealed class Store<TModel, TMsg>
{
    public readonly record struct Entry(string Label, TModel Model);

    private readonly Func<TModel, TMsg, (TModel, Cmd<TMsg>)> update;
    private readonly Func<TMsg, string> describe;
    private readonly BindableValue<TModel> model;
    private readonly BindableValue<int> cursor = new(0); // history index being shown
    private readonly List<Entry> history = new();
    private readonly ConcurrentQueue<TMsg> inbox = new();
    private readonly int maxHistory;

    public Store((TModel model, Cmd<TMsg> cmd) init, Func<TModel, TMsg, (TModel, Cmd<TMsg>)> update,
        Func<TMsg, string> describe = null, int maxHistory = 200)
    {
        this.update = update;
        this.describe = describe ?? (m => m.ToString());
        this.maxHistory = maxHistory;
        model = new BindableValue<TModel>(init.model);
        history.Add(new Entry("init", init.model));
        deliver = Post;
        init.cmd.Start(deliver);
    }

    private readonly Action<TMsg> deliver;

    /// <summary>
    /// Called (on any thread) when a message is queued for <see cref="Pump"/>: an app that sleeps
    /// while idle wakes its loop here, so finished commands are handled right away.
    /// </summary>
    public Action Wake;

    /// <summary>Queue a message from any thread (background work, a timer); <see cref="Pump"/> dispatches it.</summary>
    public void Post(TMsg msg)
    {
        inbox.Enqueue(msg);
        Wake?.Invoke();
    }

    /// <summary>The current model. Reading it while rendering repaints the view when it changes.</summary>
    public TModel Model => model.Value;

    public int HistoryCount => history.Count;
    public Entry History(int index) => history[index];

    /// <summary>The history entry on screen (the last one unless travelling back in time).</summary>
    public int Cursor => cursor.Value;
    public bool IsLive => cursor.Value == history.Count - 1;

    /// <summary>
    /// Send a message (main thread). While viewing an older state the newer entries are dropped
    /// first, so the app branches off from what's on screen, like undo followed by an edit.
    /// </summary>
    public void Dispatch(TMsg msg)
    {
        if (!IsLive) history.RemoveRange(cursor.Value + 1, history.Count - cursor.Value - 1);
        var (next, cmd) = update(model.Value, msg);
        history.Add(new Entry(describe(msg), next));
        if (history.Count > maxHistory) history.RemoveAt(1); // keep "init"
        model.Value = next;
        cursor.Value = history.Count - 1;
        cmd.Start(deliver);
    }

    /// <summary>
    /// Dispatch the messages finished commands produced. Call once per frame on the main thread.
    /// Paused while time travelling, so timers don't overwrite the state being inspected.
    /// </summary>
    public void Pump()
    {
        if (!IsLive) return;
        while (inbox.TryDequeue(out TMsg msg)) Dispatch(msg);
    }

    /// <summary>Show the model as it was after history entry <paramref name="index"/>.</summary>
    public void TravelTo(int index)
    {
        index = Math.Clamp(index, 0, history.Count - 1);
        model.Value = history[index].Model;
        cursor.Value = index;
    }
}
