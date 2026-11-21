using Cosmiria;
using Riateu.Graphics;

namespace Cosmiria.Sample;

/// <summary>The component from the original design sketch; markup in MyButtonComponent.cml.</summary>
[Cml("MyButtonComponent.cml")]
public partial struct MyButtonComponent
{
    public int Num;

    // onclick="Increment" in the markup calls this on the stored component state.
    public void Increment() => Num++;
}

/// <summary>A self-contained counter; several are mounted with different keys.</summary>
public struct Counter : IComponent
{
    public int Value;

    private static readonly Style Negative = default(Style).TextColorOf(0xFF8A80FF);
    private static readonly Style Positive = default(Style).TextColorOf(0xA3BE8CFF);

    public void Add(int amount) => Value += amount;
    public void Reset() => Value = 0;

    public Node Render(UIStack ui, EventStack ev)
    {
        return ui.View(ui.Style().FlexRow().ItemsCenter().Gap(8))
            .Child([
                ui.View(ui.Style().MinW(32).JustifyBetween().ItemsCenter().Gap(8)).Child([
                    ui.Button("-", ui.Style().Shadow2Xl().Shadow(0, 2, 5, 1, Color.Black)).OnClick(ev.Listener(static (ref Counter self) => self.Add(-1))),
                    ui.Text(Value).Styled(Value < 0 ? Negative : Positive),
                    ui.Button("+", ui.Style().Shadow2Xl().Shadow(0, 2, 5, 1, Color.Black)).OnClick(ev.Listener(static (ref Counter self) => self.Add(1)))
                ]),
                ui.Button("Reset", style: ui.Style().Bg(0x5E3A3AFF)).OnClick(ev.Listener(static (ref Counter self) => self.Reset()))
            ]);
    }
}

/// <summary>Per-item events: every row binds its own item id to Select/Remove/Hover.</summary>
[Cml("ListPanel.cml")]
public partial struct ListPanel
{
    public record struct Item(int Id, int Value);

    public List<Item> Items;
    public int NextId;
    public int SelectedId;
    public int HoveredId;
    /// <summary>An element after the last row (CML ref="End" creates it).</summary>
    public ElementRef End;

    private static readonly Style Selected = default(Style).Border(1, 0x88C0D0FFu).Bg(0x3B4252FFu);
    private static readonly Style Normal = default(Style);

    public static ListPanel Create(int count)
    {
        var panel = new ListPanel { Items = [with(capacity: count * 2)], SelectedId = -1, HoveredId = -1 }; // C# 15: constructor arguments in a collection expression
        for (int i = 0; i < count; i++) panel.Add();
        return panel;
    }

    public void Add()
    {
        NextId++;
        Items.Add(new Item(NextId, NextId * 37 % 100));
    }

    /// <summary>Add a row and scroll down to it (once the next build has laid it out).</summary>
    public void AddAndShow()
    {
        Add();
        End?.ScrollIntoView();
    }

    public void Remove(int id)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Id == id) { Items.RemoveAt(i); break; }
        }
        if (SelectedId == id) SelectedId = -1;
        if (HoveredId == id) HoveredId = -1;
    }

    public void Select(int id) => SelectedId = SelectedId == id ? -1 : id;

    // onhover="Hover(item.Id, $e)": $e is the PointerEvent (enter or leave).
    public void Hover(int id, PointerEvent e)
    {
        if (e.Hovered) HoveredId = id;
        else if (HoveredId == id) HoveredId = -1;
    }
}

/// <summary>Every built-in widget, in CML. Values live here; widgets only render them.</summary>
[Cml("WidgetsPanel.cml")]
public partial struct WidgetsPanel
{
    private static readonly string[] Sizes = ["Small", "Medium", "Large"];
    private static readonly string[] Fruits = ["Apple", "Banana", "Cherry", "Dragonfruit", "Elderberry", "Fig", "Grape"];

    // The widgets use bind:..., which generates their handlers; no Toggle/Pick methods needed.
    public bool Agree, Updates, Dark, Liked, Starred, FruitsOpen;
    public int Size, Fruit, Quantity;
    public TextBuffer Name;
    public string Greeting;

    public static WidgetsPanel Create() => new() { Size = 1, Dark = true, Quantity = 3, Name = new TextBuffer() };

    // Changing a bound field from the parent pushes it into the child Counter on the next build.
    public void Double() => Quantity *= 2;
    public void Submit() => Greeting = Name.Length > 0 ? Name.ToString() : null;
}

/// <summary>A tree with a context menu, a scrollbar, tooltips and an effect.</summary>
[Cml("ExplorerPanel.cml")]
public partial struct ExplorerPanel
{
    public BindableValue<string> Selected;
    public List<string> Textures;
    public int Changes;
    /// <summary>What the last item menu command did.</summary>
    public string Status;
    /// <summary>The texture being renamed (null: none), its new name, and the box it's typed in.</summary>
    public string Renaming;
    public TextBuffer RenameText;
    public ElementRef RenameBox;

    private static readonly string[] Sounds = ["click.wav", "music.ogg"];

    public static ExplorerPanel Create()
    {
        var p = new ExplorerPanel { Selected = new(), Textures = new(), RenameText = new() };
        for (int i = 1; i <= 24; i++) p.Textures.Add($"texture{i:00}.png");
        return p;
    }

    public void SelectionChanged() => Changes++;
    public void AddTexture() => Textures.Add($"texture{Textures.Count + 1:00}.png");
    public void ClearSelection() => Selected.Value = null;
    public void SortAscending() => Textures.Sort(string.CompareOrdinal);
    public void SortDescending() => Textures.Sort((a, b) => string.CompareOrdinal(b, a));

    // Item menu commands.
    public void CopyName(string name) { SDL3.SDL.SDL_SetClipboardText(name); Status = $"Copied \"{name}\""; }
    public void Play(string sound) => Status = $"Playing {sound}";
    public void PlayAll() => Status = $"Playing {Sounds.Length} sounds";
    public void Open(string scene) => Status = $"Opened {scene}";

    public void Duplicate(string name)
    {
        int i = Textures.IndexOf(name);
        if (i < 0) return;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        string copy = $"{stem} copy{ext}";
        for (int n = 2; Textures.Contains(copy); n++) copy = $"{stem} copy {n}{ext}";
        Textures.Insert(i + 1, copy);
        Selected.Value = copy;
        Status = $"Duplicated {name}";
    }

    public void StartRename(string name)
    {
        Renaming = name;
        RenameText.Set(name);
        RenameText.SelectAll();   // typing replaces the whole name
        RenameBox.Focus();        // the box appears on the next build; it's focused right after
    }

    public void FinishRename()
    {
        string name = RenameText.ToString().Trim();
        int i = Textures.IndexOf(Renaming);
        if (i >= 0 && name.Length > 0 && !Textures.Contains(name))
        {
            Textures[i] = name;
            if (Selected.Value == Renaming) Selected.Value = name;
            Status = $"Renamed {Renaming} to {name}";
        }
        CancelRename();
    }

    public void CancelRename()
    {
        RenameBox.Blur();
        Renaming = null;
    }

    public void Delete(string name)
    {
        if (!Textures.Remove(name)) return;
        if (Selected.Value == name) Selected.Value = null;
        Status = $"Deleted {name}";
    }
}

/// <summary>Widgets with their own values, and BindableValue fields shared between widgets.</summary>
[Cml("OwnValuesPanel.cml")]
public partial struct OwnValuesPanel
{
    private static readonly string[] Fruits = ["Apple", "Banana", "Cherry"];

    // Widgets edit these in place (no handlers); code reads .Value.
    public BindableValue<float> Level;
    public BindableValue<bool> Muted;
    public BindableValue<int> Fruit;

    public static OwnValuesPanel Create() => new() { Level = new(40), Muted = new(), Fruit = new(1) };
}

/// <summary>Text area, transitions and a resizable split.</summary>
[Cml("NotesPanel.cml")]
public partial struct NotesPanel
{
    public TextBuffer Notes;
    public int SavedLines;
    public bool Expanded;
    public float LeftWidth;
    public float Tilt;   // drives w-[@(60 + Tilt)] rotate-[@Tilt] translate-x-[@(Tilt / 3)]

    private static readonly Style Small = default(Style).W(40).H(40).Bg(0x5E81ACFFu).Rotate(0);
    private static readonly Style Big = default(Style).W(120).H(60).Bg(0xA3BE8CFFu).Rotate(8);
    private static readonly Style Faded = default(Style).Opacity(0.15f);
    private static readonly Style Shown = default(Style).Opacity(1f);

    public static NotesPanel Create() => new() { Notes = new TextBuffer("Line one\nLine two"), LeftWidth = 140, Tilt = 20 };

    public void Toggle() => Expanded = !Expanded;
    public void Save() { SavedLines = 1; foreach (char c in Notes.Text) if (c == '\n') SavedLines++; }
}

/// <summary>The game screen the sample's Program renders into every frame.</summary>
public static class Game
{
    public const int Width = 128, Height = 72;
    public static Texture Screen;
    /// <summary>Where the screen is shown: while it's mounted, an on-demand loop keeps drawing every frame.</summary>
    public static ElementRef ScreenRef;

    // Reactive game state: Program changes Score; the UI repaints by itself and Level follows.
    public static readonly BindableValue<int> Score = new(0);
    public static readonly Computed<int> Level = new(() => Score.Value / 10);
}

/// <summary>Tabs and sliders: bind: for most values, ondrag for the explicit form.</summary>
[Cml("SettingsPanel.cml")]
public partial struct SettingsPanel
{
    public int Page;
    public float Volume;     // 0..1, snapped to 0.05
    public int Music;        // 0..100, integers snap to 1
    public int Brightness;   // -50..50
    public bool VSync;

    public void SetBrightness(PointerEvent e) => Brightness = Slider.ValueAt(e, -50, 50);

    public int MovedClicks;
    public void Nudge() => MovedClicks++;

    // Game tab: map the click back to game pixels through fit="contain"'s letterboxing.
    public System.Numerics.Vector2 GamePixel;
    public void PokeGame(PointerEvent e) => GamePixel = ImageFit.Contain.ToPixel(e, Game.Width, Game.Height);

    // onmove: the pointer over the game screen, in game pixels (button up or down).
    public System.Numerics.Vector2 PointerPixel;
    public void TrackGame(PointerEvent e) => PointerPixel = ImageFit.Contain.ToPixel(e, Game.Width, Game.Height);
}

/// <summary>
/// A C# component built from returned nodes: branches produce <see cref="Node.None"/>, a loop
/// fills a row, and an element that isn't returned or attached simply isn't shown.
/// </summary>
public struct StepList : IComponent
{
    public int Done; // steps completed (0..3)
    public EventHandle OnStep;

    private static readonly string[] steps = ["Plan", "Build", "Ship"];

    public Node Render(UIStack ui, EventStack ev)
    {
        Node row = ui.View(ui.Style().FlexRow().Gap(6));
        for (int i = 0; i < steps.Length; i++) row.Child(Step(ui, steps[i], i < Done));

        return ui.View(ui.Style().FlexCol().ItemsStart().Gap(4)).Child([
            "Steps (a C# component)",  // strings and elements mix: Child takes Content, a Node | string union
            row,
            Done == steps.Length
                ? ui.Text("All steps done").Styled(ui.Style().TextColorOf(Tailwind.Emerald400))
                : ui.Button("Next step").OnClick(ev.Listener(static (ref StepList s) => s.Done++)),
            Done > 0 ? ui.Button("Restart").OnClick(ev.Listener(static (ref StepList s) => s.Done = 0)) : Node.None,
        ]);
    }

    private static Node Step(UIStack ui, string name, bool done) =>
        ui.View(ui.Style().FlexRow().ItemsCenter().Gap(4).Px(8).Py(2).RoundedFull().Bg(done ? 0x4C566AFFu : 0x2E3440FFu)).Child([
            done ? ui.Icon(Icons.Check, 12) : Node.None,
            name,
        ]);
}

/// <summary>
/// A component with custom events. The parent fills the Callback fields (onpick="..." in CML, or
/// ev.Callback(...) in C#); this component invokes them whenever it likes, here from its listeners.
/// </summary>
[Cml("MoodPicker.cml")]
public partial struct MoodPicker
{
    public static readonly string[] Names = ["Meh", "Good", "Great"];

    public Callback<int> OnPick; // raised with the picked index
    public Callback OnReset;     // raised without an argument
    public int Selected;

    private static readonly Style On = default(Style).Bg(0x5E81ACFFu);
    private static readonly Style Off = default;

    public static MoodPicker Create() => new() { Selected = -1 };

    public void Pick(int index)
    {
        Selected = index;
        OnPick.Invoke(index); // the parent's handler runs now, on the parent's state
    }

    public void Reset()
    {
        Selected = -1;
        OnReset.Invoke();
    }
}

/// <summary>Uses MoodPicker's events, with and without captured arguments.</summary>
[Cml("EventsPanel.cml")]
public partial struct EventsPanel
{
    private static readonly string[] TeamNames = ["Ada", "Linus"];

    public int Mood, Changes;
    public int[] Team;

    public static EventsPanel Create() => new() { Mood = -1, Team = [-1, -1] };

    private static string Name(int mood) => mood < 0 ? "?" : MoodPicker.Names[mood];

    public void MoodPicked(int mood) { Mood = mood; Changes++; }
    public void MoodReset() { Mood = -1; Changes++; }
    public void TeamMood(int member, int mood) => Team[member] = mood;
}

public sealed record Profile(string Name, string Role, int Loads);

/// <summary>Async listeners, Suspense with resources, and an async callback.</summary>
[Cml("AsyncPanel.cml")]
public partial struct AsyncPanel
{
    private static readonly string[] Quotes =
        ["Simplicity is prerequisite for reliability.", "Premature optimization is the root of all evil.", "Make it work, make it right, make it fast."];
    private static int profileLoads;

    public string Quote;
    public bool LoadingQuote;
    public int Fetches, Saves;
    public Resource<Profile> Profile;
    public Resource<string> Broken;

    public static AsyncPanel Create() => new()
    {
        Profile = new Resource<Profile>(LoadProfile),
        Broken = new Resource<string>(LoadBroken),
    };

    // Stand-ins for a web request: they run off the UI thread.
    private static async Task<Profile> LoadProfile(CancellationToken ct)
    {
        await Task.Delay(700, ct);
        return new Profile("Ada Lovelace", "Analytical engine programmer", Interlocked.Increment(ref profileLoads));
    }

    private static async Task<string> LoadBroken(CancellationToken ct)
    {
        await Task.Delay(400, ct);
        throw new IOException("503 Service Unavailable");
    }

    /// <summary>
    /// An async listener (onclick="FetchQuote"): `this` is the state as it was on the click; the
    /// handle writes back after the await, which resumes on the UI thread.
    /// </summary>
    public async Task FetchQuote(ComponentHandle<AsyncPanel> self)
    {
        if (LoadingQuote) return;
        self.Update(static (ref AsyncPanel p) => p.LoadingQuote = true);
        await Task.Delay(500, self.Unmounted); // cancelled if the panel closes
        string quote = Quotes[Fetches % Quotes.Length];
        self.Update(static (ref AsyncPanel p, string q) => { p.Quote = q; p.LoadingQuote = false; p.Fetches++; }, quote);
    }

    public void Reload() => Profile.Reload();
    public void Refresh() => Profile.Reload(keepValue: true);
    public void Retry() => Broken.Reload();

    /// <summary>The handler of SaveButton's OnSave: the button awaits it.</summary>
    public static async Task Save(ComponentHandle<AsyncPanel> self)
    {
        await Task.Delay(600, self.Unmounted);
        self.Update(static (ref AsyncPanel p) => p.Saves++);
    }
}

/// <summary>A button that raises OnSave and shows "Saving..." until the parent's handler finishes.</summary>
[Cml("SaveButton.cml")]
public partial struct SaveButton
{
    public Callback OnSave;
    public bool Saving;

    private static readonly Style Idle = default;
    private static readonly Style Busy = default(Style).Opacity(0.6f);

    public async Task Click(ComponentHandle<SaveButton> self)
    {
        if (Saving) return;
        self.Update(static (ref SaveButton b) => b.Saving = true);
        await OnSave.InvokeAsync();
        self.Update(static (ref SaveButton b) => b.Saving = false);
    }
}

/// <summary>Root component, written directly in C# (no CML) to show the listener API and docking.</summary>
public struct App : IComponent
{
    public int SelectedTag;
    /// <summary>Panel arrangement; drag tabs onto other groups' edges or centers to re-dock them.</summary>
    public DockLayout Layout;
    public BindableValue<bool> ShowAbout;
    // Panels are mounted with keepAlive: true, so a hidden dock tab keeps its components' state.

    private static DockLayout CreateLayout() => new(
        DockLayout.Row(0.27f,
            DockLayout.Tabs("showcase", "explorer", "events"),
            DockLayout.Row(0.32f,
                DockLayout.Tabs("widgets", "notes", "own"),
                DockLayout.Row(0.6f, DockLayout.Tabs("settings", "async"), DockLayout.Tabs("list")))));

    public Node Render(UIStack ui, EventStack ev)
    {
        Layout ??= CreateLayout(); // Render runs on the stored state, so this persists

        ShowAbout ??= new();

        var dock = ui.DockSpace(Layout, style: ui.Style().Grow().Rounded(6).OverflowHidden().Margin(10));
        // A modal from the Help menu: everything behind it is blocked while it's open.
        var about = ui.Modal(ShowAbout, "About Cosmiria");
        Node root = ui.View(ui.Style().FlexCol().SizeFull().Bg(0x242933FF)).Child([Menus(ui, ev), dock, about]);
        if (about.IsOpen)
        {
            about.Body.Child([
                ui.Text("Cosmiria is a GPU UI framework for Riateu with CML markup.").Styled(ui.Style().MaxW(360)),
                ui.Text("Tab moves focus, Enter activates, Escape closes this.").Styled(ui.Style().TextColorOf(Tailwind.Slate400)),
                ui.Button("Close").Styled(ui.Style().SelfEnd()).OnClick(ev.Listener(static (ref App a) => a.ShowAbout.Value = false)),
            ]);
        }

        if (dock.Panel("showcase", "Showcase", out Node showcase))
        {
            showcase.Child([
                ui.Text("Cosmiria UI").Styled(ui.Style().TextColorOf(0x88C0D0FF)),
                ui.Mount<MyButtonComponent>(key: 0, keepAlive: true),
                ui.View(ui.Style().H(1).Bg(0x434C5EFF)),
                ui.Text("Counters (independent state)"),
                ui.Mount<Counter>(key: 0, keepAlive: true),
                ui.Mount<Counter>(key: 1, new Counter { Value = 10 }, keepAlive: true),
                ui.Mount<StepList>(key: 0, keepAlive: true),
                ui.View(ui.Style().H(1).Bg(0x434C5EFF)),
                ui.Text("Text wraps at word boundaries to fit its container, and its height grows with the number of lines. Long words like Supercalifragilisticexpialidocious break too.")
                    .Styled(ui.Style().TextColorOf(0xD8DEE9FF)),
                // Shaped by HarfBuzz: the "ffi"/"ffl" ligatures, kerned AV/Wa pairs, and an accent typed
                // as a separate combining mark (e + U+0301) drawn as one é.
                ui.Text("Shaping: office, waffle · AVATAR, Wave · cafe\u0301").Styled(ui.Style().TextXl().TextColorOf(0xEBCB8BFF)),
                ui.View(ui.Style().Grow()),
                ui.Text($"Flex wrap (selected: {tagNames[SelectedTag]})"),
                Tags(ui, ev, SelectedTag),
                ui.View(ui.Style().H(1).Bg(0x434C5EFF)),
                ui.Text("Blur and backdrop blur"),
                BlurDemo(ui),
                ui.Text("Grid"),
                GridDemo(ui),
            ]);
        }
        if (dock.Panel("explorer", "Explorer", out Node explorer))
            explorer.Child(ui.Mount<ExplorerPanel>(key: 0, static () => ExplorerPanel.Create(), keepAlive: true));
        if (dock.Panel("widgets", "Widgets", out Node widgets, closable: true))
            widgets.Child(ui.Mount<WidgetsPanel>(key: 0, static () => WidgetsPanel.Create(), keepAlive: true));
        if (dock.Panel("own", "Own values", out Node own, closable: true))
            own.Child(ui.Mount<OwnValuesPanel>(key: 0, static () => OwnValuesPanel.Create(), keepAlive: true));
        if (dock.Panel("async", "Async", out Node asyncPanel, closable: true))
            asyncPanel.Child(ui.Mount<AsyncPanel>(key: 0, static () => AsyncPanel.Create(), keepAlive: true));
        if (dock.Panel("events", "Events", out Node events, closable: true))
            events.Child(ui.Mount<EventsPanel>(key: 0, static () => EventsPanel.Create(), keepAlive: true));
        if (dock.Panel("notes", "Notes", out Node notes, closable: true))
            notes.Child(ui.Mount<NotesPanel>(key: 0, static () => NotesPanel.Create(), keepAlive: true));
        if (dock.Panel("settings", "Settings", out Node settings))
            settings.Child(ui.Mount<SettingsPanel>(key: 0, new SettingsPanel { Volume = 0.8f, Music = 60, VSync = true }, keepAlive: true));
        if (dock.Panel("list", "List", out Node list, closable: true))
        {
            list.Child([
                ui.Text("Scrollable list (mouse wheel)"),
                ui.Mount<ListPanel>(key: 0, static () => ListPanel.Create(30), keepAlive: true),
            ]);
        }
        return root;
    }

    private static readonly (string id, string title)[] panels =
        [("showcase", "Show Showcase"), ("explorer", "Show Explorer"), ("widgets", "Show Widgets"), ("notes", "Show Notes"),
         ("own", "Show Own values"), ("events", "Show Events"), ("async", "Show Async"), ("settings", "Show Settings"), ("list", "Show List")];

    /// <summary>The menu bar (C# API): opens and closes itself through its own value.</summary>
    private static Node Menus(UIStack ui, EventStack ev)
    {
        var bar = ui.MenuBar(ui.Own(-1));

        // Items with a handler are called even while their menu is closed: that's how their shortcuts work.
        var file = bar.Menu("File");
        file.Item("Reset layout", ev.Listener(static (ref App a) => a.Layout = CreateLayout()), "Ctrl+R");
        file.Item("Copy layout", ev.Listener(static (ref App a) => SDL3.SDL.SDL_SetClipboardText(a.Layout.Serialize())), "Ctrl+Shift+C");
        var view = bar.Menu("View");
        if (view.IsOpen)
        {
            var show = view.Submenu("Show panel");
            if (show.IsOpen)
                foreach (var (id, title) in panels)
                    show.Item(title, ev.Listener(static (ref App a, string id) => a.Layout.Activate(id), id));
        }
        var help = bar.Menu("Help");
        help.Item("About Cosmiria...", ev.Listener(static (ref App a) => a.ShowAbout.Value = true), "F1");
        return bar;
    }

    private static readonly string[] tagNames =
        ["layout", "flexbox", "wrap", "text", "SDF", "rounded", "scissor", "zero-alloc", "structs", "GPUI", "Riateu", "Vulkan"];
    private static readonly uint[] tagColors = [0xBF616AFF, 0xD08770FF, 0xEBCB8BFF, 0xA3BE8CFF, 0xB48EADFF, 0x88C0D0FF];

    /// <summary>A frosted card over colored dots (backdrop), and a blurred row of the same dots.</summary>
    private static Node BlurDemo(UIStack ui)
    {
        Node Dots()
        {
            var dots = ui.View(ui.Style().FlexRow().Gap(8).ItemsCenter());
            for (int i = 0; i < tagColors.Length; i++)
                dots.Child(ui.View(ui.Style().Size(28).RoundedFull().Bg(tagColors[i])));
            return dots;
        }
        return ui.View(ui.Style().FlexCol().Gap(8)).Child([
            ui.View(ui.Style().Relative().H(56).Px(6).JustifyCenter()).Child([
                Dots(),
                ui.Blur(6, backdrop: true, ui.Style().Absolute().Left(40).Top(8).W(160).H(40).Rounded(8)
                        .Bg(0xFFFFFF26).Border(1, 0xFFFFFF4D).ItemsCenter().JustifyCenter())
                    .Child(ui.Text("Backdrop")),
            ]),
            ui.Blur(4).Child(Dots()),
        ]);
    }

    /// <summary>Three columns: a wide cell spanning two, a tall one spanning two rows, and auto-placed fill.</summary>
    private static Node GridDemo(UIStack ui)
    {
        Node Cell(string label, uint color, in Style style = default) =>
            ui.View(ui.Style().Padding(6).Rounded(4).Bg(color).MergedWith(style))
                .Child(ui.Text(label).Styled(ui.Style().TextColorOf(0x2E3440FF)));
        return ui.View(ui.Style().FlexCol().Gap(6)).Child([
            ui.Grid(3, 6).Child([
                Cell("col-span-2", tagColors[0], ui.Style().ColSpanOf(2)),
                Cell("row-span-2", tagColors[1], ui.Style().RowSpanOf(2)),
                Cell("1", tagColors[2]),
                Cell("2", tagColors[3]),
                Cell("col-span-full", tagColors[4], ui.Style().ColSpanOf(short.MaxValue)),
            ]),
            ui.View(ui.Style().GridCols(Tracks).Gap(6)).Child([
                Cell("60px", tagColors[5]),
                Cell("auto", tagColors[0]),
                Cell("1fr wraps its text when the column is narrow", tagColors[2]),
            ]),
        ]);
    }

    private static readonly GridTrack[] Tracks = [GridTrack.Px(60), GridTrack.Auto, GridTrack.Fr()];

    private static Node Tags(UIStack ui, EventStack ev, int selected)
    {
        var tags = ui.View(ui.Style().FlexRow().FlexWrap().Gap(6));
        for (int i = 0; i < tagNames.Length; i++)
        {
            var tag = ui.View(ui.Style().Px(10).Py(3).Bg(tagColors[i % tagColors.Length]).RoundedFull())
                .Hover(ui.Style().Border(2, 0xECEFF4FF))
                // Static lambda + per-item argument: no closure, no boxing.
                .OnClick(ev.Listener(static (ref App app, int index) => app.SelectedTag = index, i))
                .Child(ui.Text(tagNames[i]).Styled(ui.Style().TextColorOf(0x2E3440FF)));
            if (i == selected) tag.Styled(ui.Style().Border(2, 0x2E3440FFu));
            tags.Child(tag);
        }
        return tags;
    }
}