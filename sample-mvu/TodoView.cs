using Cosmiria.Mvu;

namespace Cosmiria.Sample.Mvu;

/// <summary>
/// The view. Reads <see cref="Store"/>'s model and turns user input into messages; the store's
/// model is reactive, so every new model (from a click, a timer or an async command) repaints it.
/// </summary>
[Cml("TodoView.cml")]
public partial struct TodoView
{
    public Store<Model, Msg> Store;

    /// <summary>
    /// What's being typed. View state, not model state: the text box edits it in place, and its
    /// text enters the model only as the Add message. (Mirroring every keystroke into the model
    /// would work too, at the cost of a string per keystroke.)
    /// </summary>
    public TextBuffer Draft;

    /// <summary>The text box (focused from code by the trace run).</summary>
    public ElementRef DraftBox;

    private readonly Model M => Store.Model;

    private static readonly Style Normal = default;
    private static readonly Style Highlight = default(Style).Bg(0x5E81ACFFu);
    private static readonly Style DoneText = default(Style).TextColorOf(0x6B7385FFu);
    private static readonly Style Current = default(Style).Bg(0x3B4252FFu).TextColorOf(0x88C0D0FFu);
    private static readonly Style Future = default(Style).TextColorOf(0x5C6370FFu);

    public readonly void Dispatch(Msg msg) => Store.Dispatch(msg);

    public readonly void Submit()
    {
        if (Draft.Length == 0) return;
        Store.Dispatch(new Msg.Add(Draft.ToString()));
        Draft.Clear();
    }

    public readonly void TravelTo(int index) => Store.TravelTo(index);
    public readonly void Step(int delta) => Store.TravelTo(Store.Cursor + delta);
    public readonly void Live() => Store.TravelTo(Store.HistoryCount - 1);
}
