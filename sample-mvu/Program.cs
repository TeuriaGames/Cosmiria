using Cosmiria;
using Cosmiria.Mvu;
using Cosmiria.Sample.Mvu;
using Riateu;
using Riateu.Content;
using Riateu.Graphics;

/// <summary>
/// Model-View-Update sample: a todo list whose whole state is one immutable <see cref="Model"/>,
/// changed only by <see cref="TodoApp.Update"/> in response to <see cref="Msg"/>s.
/// COSMIRIA_SELFTEST=1 drives it with a script and checks the results.
/// <para>
/// It renders on demand, like a desktop app: the loop sleeps until input, a caret blink, a tooltip
/// or a finished command, and draws only frames that changed (COSMIRIA_TRACE=1 prints how often).
/// The self-test uses the fixed-step loop, which its frame-counted script needs.
/// </para>
/// </summary>
public class Program : GameApp
{
    private SpriteFont font;
    private UIContext ui;
    private Store<Model, Msg> store;

    public Program(WindowSettings settings, GraphicsSettings graphicsSettings) : base(settings, graphicsSettings) { }

    public override void LoadContent(AssetStorage storage)
    {
        font ??= storage.LoadFont(Path.Combine(AppContext.BaseDirectory, "Assets", "Carlito-Regular.ttf"), 18,
            SpriteFont.DefineCharset(0x20, 0x100));
    }

    public override void Initialize()
    {
        store = new Store<Model, Msg>(TodoApp.Init(), TodoApp.Update, static m => m.Describe());
        ui = new UIContext(GraphicsDevice, MainWindow, font);

        // On-demand rendering: wake on state changes from any thread (reactive values, finished
        // commands), sleep until the UI's next deadline, draw only what changed.
        RenderOnDemand = !selfTest;
        ui.Invalidated += Wake;
        store.Wake = Wake;
        if (Environment.GetEnvironmentVariable("COSMIRIA_TRACE") == "1")
        {
            // Two seconds in, a message arrives from another thread (as from a server); the async
            // command it starts finishes 0.8 s later. Both wake the loop.
            Task.Delay(2000).ContinueWith(_ => store.Post(new Msg.LoadExamples()));
            Task.Delay(3500).ContinueWith(_ => { focusDraft = true; Wake(); }); // then the caret blinks
            Task.Delay(6000).ContinueWith(_ => { Quit(); Wake(); });
            MaxIdleWait = Timeout.InfiniteTimeSpan; // measure true idle (no asset-server polling)
            traceClock.Start();
        }
        ui.SetRoot(new TodoView { Store = store, Draft = new TextBuffer() });
    }

    protected override TimeSpan NextUpdateIn()
    {
        double s = ui.SecondsUntilUpdate;
        return double.IsInfinity(s) ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(s);
    }

    protected override bool ShouldRender() => ui.NeedsRender;

    private readonly System.Diagnostics.Stopwatch traceClock = new();
    private volatile bool focusDraft;

    public override void Update(float delta)
    {
        if (Environment.GetEnvironmentVariable("COSMIRIA_TRACE") == "1") Trace();
        store.Pump(); // messages from finished commands (timers, async work)
        if (selfTest) SelfTestStep();
        else ui.Update();
    }

    /// <summary>One line per tick: when it ran, and whether it drew (COSMIRIA_TRACE=1).</summary>
    private void Trace()
    {
        if (traceClock.IsRunning) Console.WriteLine($"[trace] {traceClock.Elapsed.TotalSeconds,6:0.000} s tick");
        if (focusDraft) { focusDraft = false; ui.Root<TodoView>().DraftBox.Focus(); }
    }

    public override void Render()
    {
        if (traceClock.IsRunning) Console.WriteLine($"[trace] {traceClock.Elapsed.TotalSeconds,6:0.000} s   render ({store.Model.Todos.Length} todos)");
        CommandBuffer cmd = GraphicsDevice.AcquireCommandBuffer();
        RenderTarget swapchain = cmd.AcquireSwapchainTarget(MainWindow);
        if (swapchain != null)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            ui.Render(cmd, swapchain, LoadOp.Clear, Color.Black);
            if (selfTest && frame > 30 && !ui.RebuiltLastFrame) idleBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        GraphicsDevice.Submit(cmd);
    }

    public override void Destroy() => ui.Dispose();

    public static void Main()
    {
        var program = new Program(
            new WindowSettings("Cosmiria — MVU", 1000, 680, WindowMode.Windowed, new Flags(Resizable: true)),
            GraphicsSettings.Vsync);
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; program.Quit(); });
        program.Run();
    }

    // ================================================================ self-test

    private readonly bool selfTest = Environment.GetEnvironmentVariable("COSMIRIA_SELFTEST") == "1";
    private int frame;
    private long idleBytes;
    private float px = -1, py = -1;
    private int pressFrame = -1;

    // (label, action): c click, t type into the focused input, k check found ('!' = missing), w wait.
    private static readonly (string label, char action)[] script =
    [
        ("Nothing to do. Add something above.", 'k'),
        ("What needs to be done?", 'c'), ("Buy bread\n", 't'),   // Enter submits: Add
        ("Learn MVU", 't'), ("Add", 'c'),                        // the Add button too
        ("2 left · 0 done", 'k'),
        ("Buy bread", 'c'),                                      // checkbox: Toggle
        ("1 left · 1 done", 'k'),
        ("Active", 'c'), ("!Buy bread", 'k'),                    // filter
        ("All", 'c'), ("Buy bread", 'k'),
        ("Load examples", 'c'), ("Loading examples...", 'k'),
        ("", 'w'), ("", 'w'), ("", 'w'), ("", 'w'), ("", 'w'),
        ("Water the plants", 'k'), ("5 left · 1 done", 'k'),     // async command arrived
        ("Delete", 'd'),                                         // trash on the first row
        ("Deleted \"Buy bread\"", 'k'),
        ("Undo", 'c'), ("Buy bread", 'k'), ("!Deleted \"Buy bread\"", 'k'),
        ("Delete", 'd'), ("Deleted \"Buy bread\"", 'k'),
        .. Enumerable.Repeat(("", 'w'), 24),                     // ~4.8 s at 60 fps: the 4 s toast timer fires
        ("!Deleted \"Buy bread\"", 'k'), ("DismissToast #2", 'k'), // the timer dismissed it
        ("Add \"Learn MVU\"", 'c'),                              // time travel to step 2
        ("Viewing step 2 of", 'p'), ("!Water the plants", 'k'), ("1 left · 1 done", '!'),
        ("Live", 'c'), ("Water the plants", 'k'),
    ];

    private void SelfTestStep()
    {
        frame++;
        bool press = false, release = false;
        int step = (frame - 40) / 12;
        if (frame >= 40 && step < script.Length && (frame - 40) % 12 == 0)
        {
            var (label, action) = script[step];
            switch (action)
            {
                case 't': ui.TypeText(label); break;
                case 'w': break;
                case 'k':
                case 'p':
                case '!':
                {
                    bool missing = label.StartsWith('!') && action == 'k';
                    string text = missing ? label[1..] : label;
                    bool found = action == 'p' ? FindPrefix(text) : ui.TryFindText(text, out _);
                    if (action == '!') missing = true;
                    Console.WriteLine($"[selftest] check {(missing ? "missing" : "found")} '{text}': {(found != missing ? "OK" : "FAIL")}");
                    break;
                }
                default:
                    if (action == 'd' ? FindTrash(out var r) : ui.TryFindText(label, out r))
                    {
                        px = r.X + r.Z * 0.5f; py = r.Y + r.W * 0.5f;
                        pressFrame = frame + 2;
                    }
                    else Console.WriteLine($"[selftest] '{label}' not found");
                    break;
            }
        }
        if (frame == pressFrame) press = true;
        if (frame == pressFrame + 1) release = true;
        ui.ProcessKeyboard();
        ui.ProcessPointer(px, py, press, release, 0);

        if (frame == 40 + script.Length * 12 + 10)
        {
            Console.WriteLine($"[selftest] history={store.HistoryCount} live={store.IsLive} idle-frame alloc={idleBytes} B");
            if (Environment.GetEnvironmentVariable("COSMIRIA_SCREENSHOT") is { Length: > 0 } shot) Screenshot(shot);
        }
        if (frame == 40 + script.Length * 12 + 20) Quit();
    }

    // The history label "Viewing step 2 of N · timers paused" ends in a number that depends on the run.
    private bool FindPrefix(string prefix)
    {
        for (int n = 0; n < 100; n++)
            if (ui.TryFindText($"{prefix} {n} · timers paused", out _)) return true;
        return false;
    }

    // The delete button (an icon, no text) on the "Buy bread" row. From the right window edge:
    // 16 padding + 300 history + 16 gap + 16 app padding + 6 list padding + 10 row padding,
    // then half of the 26px button.
    private bool FindTrash(out System.Numerics.Vector4 rect)
    {
        rect = default;
        if (!ui.TryFindText("Buy bread", out var row)) return false;
        float x = MainWindow.Width - (16 + 300 + 16 + 16 + 6 + 10 + 13);
        rect = new System.Numerics.Vector4(x - 1, row.Y + row.W * 0.5f - 1, 2, 2);
        return true;
    }

    private void Screenshot(string path)
    {
        uint w = (uint)MainWindow.Width, h = (uint)MainWindow.Height;
        using var target = new Texture(GraphicsDevice, w, h, MainWindow.SwapchainFormat, TextureUsageFlags.Sampler | TextureUsageFlags.ColorTarget);
        CommandBuffer cmd = GraphicsDevice.AcquireCommandBuffer();
        ui.Render(cmd, target, LoadOp.Clear, Color.Black);
        GraphicsDevice.Submit(cmd);
        var bytes = new byte[w * h * 4];
        target.Download(bytes);
        if (MainWindow.SwapchainFormat.ToString().StartsWith("B8G8R8A8"))
            for (int i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
        using var image = new Image(bytes, (int)w, (int)h);
        image.WritePNG(path);
        Console.WriteLine($"[selftest] screenshot -> {path}");
    }
}
