using System.Runtime.CompilerServices;

namespace Cosmiria;

/// <summary>
/// A value a widget reads and writes in place, like a <see cref="TextBuffer"/> for text. Give
/// one to a widget and it's two-way without any handler:
/// <code>
/// public BindableValue&lt;bool&gt; Remember;                     // in your state (create it once)
/// ui.Checkbox(Remember, "Remember me");                       // clicking toggles Remember.Value
/// &lt;Checkbox checked="Remember"&gt;Remember me&lt;/Checkbox&gt;           // the same in CML
/// </code>
/// A widget given no value at all uses one owned by the framework (<see cref="UIStack.Own{T}(T, string, int)"/>),
/// so it still works on its own. It's reactive: changing <see cref="Value"/> from anywhere (game
/// code included) repaints the UI, <see cref="Computed{T}"/> values that read it recompute, and
/// <see cref="Changed"/> fires. Set it from the main thread.
/// </summary>
public sealed class BindableValue<T> : IReactive
{
    private T value;

    public BindableValue(T initial = default) { value = initial; }

    public T Value
    {
        get
        {
            Reactive.Track(this);
            return value;
        }
        set
        {
            if (EqualityComparer<T>.Default.Equals(this.value, value)) return;
            this.value = value;
            Version++;
            Reactive.Changed();
            Changed?.Invoke(value);
        }
    }

    /// <summary>Raised after every change, with the new value.</summary>
    public event Action<T> Changed;

    /// <summary>Incremented on every change (to detect edits cheaply).</summary>
    public int Version { get; private set; }

    public static implicit operator T(BindableValue<T> bindable) => bindable.value;
    public override string ToString() => value?.ToString() ?? "";
}

/// <summary>A widget's built-in action (toggle, select, drag…) on its value box.</summary>
internal delegate void WidgetAction<TArg>(object box, TArg arg, in PointerEvent e);

internal static class WidgetBinderTypeId
{
    private static int next;
    internal static int Allocate() => Interlocked.Increment(ref next) - 1;
}

internal static class WidgetBinderTypeId<TArg>
{
    public static readonly int Id = WidgetBinderTypeId.Allocate();
}

/// <summary>Listeners for widgets' own behavior: not bound to a component, they act on a value box.</summary>
internal sealed class WidgetBinder<TArg> : EventBinder
{
    private WidgetAction<TArg>[] actions = new WidgetAction<TArg>[16];
    private object[] boxes = new object[16];
    private TArg[] args = new TArg[16];
    private int count;

    public int Add(WidgetAction<TArg> action, object box, TArg arg)
    {
        if (count == actions.Length)
        {
            Array.Resize(ref actions, count * 2);
            Array.Resize(ref boxes, count * 2);
            Array.Resize(ref args, count * 2);
        }
        actions[count] = action;
        boxes[count] = box;
        args[count] = arg;
        return count++;
    }

    public override void Invoke(int index, UIContext context, in PointerEvent e)
    {
        if ((uint)index < (uint)count) actions[index](boxes[index], args[index], e);
    }

    public override void Reset()
    {
        Array.Clear(boxes, 0, count);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TArg>()) Array.Clear(args, 0, count);
        count = 0;
    }
}

public sealed partial class UIStack
{
    /// <summary>
    /// A value box owned by the framework for the widget at this call site, kept across rebuilds
    /// while the call keeps happening (like a React hook): <c>ui.Checkbox(ui.Own(false), "Remember me")</c>.
    /// Several calls from the same line (a loop) get one box each, in call order.
    /// </summary>
    public BindableValue<T> Own<T>(T initial = default, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Own(HashCode.Combine(file, line), initial);

    /// <summary>Own value for <paramref name="site"/> (CML uses the element's position); counted per occurrence.</summary>
    public BindableValue<T> Own<T>(int site, T initial)
    {
        int component = depth > 0 ? frames[depth - 1].StableId : UIContext.RootSeed;
        int occurrence = context.NextOccurrence(component, site);
        return context.OwnBox(component, site, occurrence, initial);
    }

    /// <summary>
    /// An own value identified by <paramref name="key"/> instead of call order, so items in a
    /// list keep their values when other items are inserted or removed:
    /// <c>ui.Checkbox(ui.OwnKeyed(item.Id, false), item.Name)</c>.
    /// </summary>
    public BindableValue<T> OwnKeyed<TKey, T>(TKey key, T initial = default, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        OwnKeyed(HashCode.Combine(file, line), key, initial);

    /// <summary>Keyed own value for <paramref name="site"/> (CML's <c>key="..."</c>).</summary>
    public BindableValue<T> OwnKeyed<TKey, T>(int site, TKey key, T initial)
    {
        int component = depth > 0 ? frames[depth - 1].StableId : UIContext.RootSeed;
        // Keys live in their own slot range (occurrences are small non-negative numbers).
        int slot = EqualityComparer<TKey>.Default.GetHashCode(key) | int.MinValue;
        return context.OwnBox(component, site, slot, initial);
    }

    /// <summary>Keyed own text buffer (a text input with <c>key="..."</c>).</summary>
    public TextBuffer OwnTextKeyed<TKey>(int site, TKey key, string initial = null)
    {
        int component = depth > 0 ? frames[depth - 1].StableId : UIContext.RootSeed;
        return context.OwnText(component, site, EqualityComparer<TKey>.Default.GetHashCode(key) | int.MinValue, initial);
    }

    // ------------------------------------------------------------------ effects

    /// <summary>
    /// A side effect: <paramref name="run"/> (a listener on this component) runs after the build in
    /// which this call first happens, and again after any build where <paramref name="deps"/>
    /// changed (compared with Equals; use a tuple for several values). Like React's useEffect:
    /// <code>
    /// ui.Effect(ev.Listener(static (ref Editor e) => e.LoadLevel()), LevelIndex);
    /// </code>
    /// Effects run like listeners (state changes rebuild). There's no cleanup callback.
    /// </summary>
    public void Effect<TDeps>(EventHandle run, TDeps deps, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        EffectAt(HashCode.Combine(file, line), deps).Run(run);

    /// <summary>An effect that runs once, after the build in which it first appears (on mount).</summary>
    public void Effect(EventHandle run, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        EffectAt(HashCode.Combine(file, line), 0).Run(run);

    /// <summary>Effect for <paramref name="site"/> (CML's &lt;Effect&gt;): call <see cref="EffectCall.Run"/> with the listener.</summary>
    public EffectCall EffectAt<TDeps>(int site, TDeps deps)
    {
        BindableValue<(bool ran, TDeps deps)> state = Own(site, (false, default(TDeps)));
        bool due = !state.Value.ran || !EqualityComparer<TDeps>.Default.Equals(state.Value.deps, deps);
        if (due)
        {
            var before = state.Value;
            state.Value = (true, deps);
            // Inside a Suspense boundary that gives up, the effect hasn't happened: it runs when the content shows.
            if (SuspenseDepth > 0) RecordUndo(() => state.Value = before);
        }
        return new EffectCall(context, due);
    }

    /// <summary>
    /// One box shared by every call with the same <paramref name="name"/> in this component, e.g.
    /// a radio group: <c>ui.RadioButton(ui.Shared("size", 0), 1, "Medium")</c>.
    /// </summary>
    public BindableValue<T> Shared<T>(string name, T initial = default)
    {
        int component = depth > 0 ? frames[depth - 1].StableId : UIContext.RootSeed;
        return context.OwnBox(component, HashCode.Combine(name, 0x51A7ED), -1, initial);
    }

    /// <summary>
    /// The own selection of radio group <paramref name="name"/> (CML's <c>group="name"</c>). Starts at
    /// <c>default(T)</c>, or at <paramref name="value"/> for the option marked <paramref name="initiallySelected"/>
    /// until the user picks something.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public BindableValue<T> RadioGroup<T>(string name, T value, bool initiallySelected)
    {
        BindableValue<T> box = Shared<T>(name);
        if (initiallySelected && box.Version == 0 && !EqualityComparer<T>.Default.Equals(box.Value, value))
        {
            box.Value = value;
            // Radios of this group drawn earlier in this build used the old value: build again.
            // (CML sets up groups at the top of Render when it can, so this is the rare case.)
            context.Notify();
        }
        return box;
    }

    /// <summary>An own <see cref="TextBuffer"/> for a text input given no buffer (CML's value-less &lt;TextInput&gt;).</summary>
    public TextBuffer OwnText(int site, string initial = null)
    {
        int component = depth > 0 ? frames[depth - 1].StableId : UIContext.RootSeed;
        int occurrence = context.NextOccurrence(component, site);
        return context.OwnText(component, site, occurrence, initial);
    }

    /// <summary>Bind a widget's built-in action on <paramref name="box"/>.</summary>
    internal EventHandle WidgetListener<TArg>(WidgetAction<TArg> action, object box, TArg arg)
    {
        WidgetBinder<TArg> binder = context.GetWidgetBinder<TArg>();
        return new EventHandle(binder, binder.Add(action, box, arg));
    }
}

/// <summary>Returned by <see cref="UIStack.EffectAt{TDeps}"/>.</summary>
public readonly struct EffectCall
{
    private readonly UIContext context;
    private readonly bool due;
    internal EffectCall(UIContext context, bool due) { this.context = context; this.due = due; }

    /// <summary>Queue <paramref name="run"/> if the effect is due.</summary>
    public void Run(EventHandle run)
    {
        if (due && run.IsValid) context.QueueEffect(run);
    }
}
