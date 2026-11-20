using System.Collections.Immutable;
using Cosmiria.Mvu;

namespace Cosmiria.Sample.Mvu;

// ===================================================================== Model
// Immutable: Update builds a new one instead of editing it, which is what makes history
// (time travel) and undo free.

public enum Filter { All, Active, Done }

public sealed record Todo(int Id, string Title, bool Done);

/// <summary>"Deleted ... · Undo" notice, with what's needed to put the todo back.</summary>
public sealed record Toast(int Id, Todo Removed, int Index);

public sealed record Model(
    ImmutableArray<Todo> Todos,
    Filter Filter,
    int NextId,
    Toast Toast,
    int NextToastId,
    bool Loading)
{
    public static readonly Model Empty = new(ImmutableArray<Todo>.Empty, Filter.All, 1, null, 1, false);

    public int ActiveCount
    {
        get
        {
            int n = 0;
            foreach (var t in Todos) if (!t.Done) n++;
            return n;
        }
    }

    public int DoneCount => Todos.Length - ActiveCount;
    public bool AllDone => Todos.Length > 0 && ActiveCount == 0;

    public bool Shows(Todo t) => Filter switch
    {
        Filter.Active => !t.Done,
        Filter.Done => t.Done,
        _ => true,
    };
}

// ================================================================== Messages
// Everything that can happen: a C# 15 union. Each case is its own small record struct, and
// `msg switch { ... }` must handle every case (the compiler checks), so adding a message without
// handling it in Update is a warning.
//
// The markup creates cases, not Msg: onclick="Dispatch(new Msg.Toggle(todo.Id))" captures a
// Msg.Toggle struct at render time, stored without boxing. It only becomes a Msg (which boxes it,
// as generated unions store an object) when Dispatch is called, on the click.

public union Msg(Msg.Add, Msg.Toggle, Msg.Remove, Msg.ToggleAll, Msg.ClearDone, Msg.Show,
                 Msg.Undo, Msg.DismissToast, Msg.LoadExamples, Msg.Loaded)
{
    public readonly record struct Add(string Title);
    public readonly record struct Toggle(int Id);
    public readonly record struct Remove(int Id);
    public readonly record struct ToggleAll;
    public readonly record struct ClearDone;
    public readonly record struct Show(Filter Filter);
    public readonly record struct Undo;
    public readonly record struct DismissToast(int ToastId);
    public readonly record struct LoadExamples;
    public readonly record struct Loaded(string[] Items);

    /// <summary>Label for the history panel.</summary>
    public string Describe() => this switch
    {
        Add a => $"Add \"{a.Title}\"",
        Toggle t => $"Toggle #{t.Id}",
        Remove r => $"Remove #{r.Id}",
        Show s => $"Show {s.Filter}",
        DismissToast d => $"DismissToast #{d.ToastId}",
        Loaded l => $"Loaded {l.Items.Length} items",
        ToggleAll => "ToggleAll",
        ClearDone => "ClearDone",
        Undo => "Undo",
        LoadExamples => "LoadExamples",
        null => "(none)",
    };
}

// ==================================================================== Update
// Pure: (model, message) -> (new model, side effects to run). No I/O, no timers, no UI.

public static class TodoApp
{
    private static readonly TimeSpan ToastTime = TimeSpan.FromSeconds(4);
    private static Cmd<Msg> NoCmd => Cmd<Msg>.None;

    public static (Model, Cmd<Msg>) Init() => (Model.Empty, NoCmd);

    public static (Model, Cmd<Msg>) Update(Model m, Msg msg) => msg switch
    {
        Msg.Add a => (AddTodo(m, a.Title), NoCmd),
        Msg.Toggle t => (m with { Todos = Map(m.Todos, t.Id, static x => x with { Done = !x.Done }) }, NoCmd),
        Msg.ToggleAll => (SetAllDone(m, !m.AllDone), NoCmd),
        Msg.ClearDone => (m with { Todos = m.Todos.RemoveAll(static t => t.Done) }, NoCmd),
        Msg.Show s => (m with { Filter = s.Filter }, NoCmd),
        Msg.Remove r => RemoveTodo(m, r.Id),
        Msg.Undo => (UndoRemove(m), NoCmd),
        // Only the notice that started this timer: a newer one keeps its own.
        Msg.DismissToast d => (m.Toast?.Id == d.ToastId ? m with { Toast = null } : m, NoCmd),
        Msg.LoadExamples => m.Loading ? (m, NoCmd) : (m with { Loading = true }, Cmd<Msg>.Of(FetchExamples)),
        Msg.Loaded l => (AddLoaded(m, l.Items), NoCmd),
        null => (m, NoCmd),
    };

    private static Model AddTodo(Model m, string title)
    {
        title = title?.Trim();
        if (string.IsNullOrEmpty(title)) return m;
        return m with { Todos = m.Todos.Add(new Todo(m.NextId, title, false)), NextId = m.NextId + 1 };
    }

    private static Model SetAllDone(Model m, bool done)
    {
        var todos = m.Todos.ToBuilder();
        for (int i = 0; i < todos.Count; i++) todos[i] = todos[i] with { Done = done };
        return m with { Todos = todos.ToImmutable() };
    }

    private static (Model, Cmd<Msg>) RemoveTodo(Model m, int id)
    {
        int index = IndexOf(m.Todos, id);
        if (index < 0) return (m, NoCmd);
        var toast = new Toast(m.NextToastId, m.Todos[index], index);
        var next = m with { Todos = m.Todos.RemoveAt(index), Toast = toast, NextToastId = m.NextToastId + 1 };
        // The notice hides itself: a timer, described here and run by the runtime.
        return (next, Cmd<Msg>.After(ToastTime, new Msg.DismissToast(toast.Id)));
    }

    private static Model UndoRemove(Model m)
    {
        if (m.Toast == null) return m;
        int at = Math.Min(m.Toast.Index, m.Todos.Length);
        return m with { Todos = m.Todos.Insert(at, m.Toast.Removed), Toast = null };
    }

    private static Model AddLoaded(Model m, string[] items)
    {
        var todos = m.Todos.ToBuilder();
        int id = m.NextId;
        foreach (string item in items) todos.Add(new Todo(id++, item, false));
        return m with { Todos = todos.ToImmutable(), NextId = id, Loading = false };
    }

    /// <summary>Stands in for a web request or a file read.</summary>
    private static async Task<Msg> FetchExamples()
    {
        await Task.Delay(800).ConfigureAwait(false);
        return new Msg.Loaded(["Water the plants", "Write the release notes", "Call the dentist", "Fix the flaky test"]);
    }

    private static int IndexOf(ImmutableArray<Todo> todos, int id)
    {
        for (int i = 0; i < todos.Length; i++) if (todos[i].Id == id) return i;
        return -1;
    }

    private static ImmutableArray<Todo> Map(ImmutableArray<Todo> todos, int id, Func<Todo, Todo> change)
    {
        int i = IndexOf(todos, id);
        return i < 0 ? todos : todos.SetItem(i, change(todos[i]));
    }
}
