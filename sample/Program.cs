using Cosmiria;
using Cosmiria.Sample;
using Riateu;
using Riateu.Content;
using Riateu.Graphics;

public class Program : GameApp
{
    private SpriteFont font;
    private UIContext ui;
    private float titleTimer;

    // COSMIRIA_SELFTEST=1 drives the UI with scripted pointer input and reports allocations.
    private readonly bool selfTest = Environment.GetEnvironmentVariable("COSMIRIA_SELFTEST") == "1";
    private int frame;
    private long uiAllocIdle, uiAllocRebuild;
    private int idleFrames, rebuildFrames;

    public Program(WindowSettings settings, GraphicsSettings graphicsSettings) : base(settings, graphicsSettings) { }

    public override void LoadContent(AssetStorage storage)
    {
        // Latin-1 (not just ASCII) so text like "·", "é" or "°" renders.
        font ??= storage.LoadFont(Path.Combine(AppContext.BaseDirectory, "Assets", "Carlito-Regular.ttf"), 18,
            SpriteFont.DefineCharset(0x20, 0x100));
    }

    // A tiny "game": a 128x72 render target redrawn every frame and shown by <Image source="Game.Screen"/>.
    private Texture sprite;
    private float gameTime;

    public override void Initialize()
    {
        ui = new UIContext(GraphicsDevice, MainWindow, font);
        if (Environment.GetEnvironmentVariable("COSMIRIA_NO_LAYERS") == "1") ui.CacheLayers = false;

        // COSMIRIA_ON_DEMAND=1: run like a desktop app (see NextUpdateIn/ShouldRender below).
        RenderOnDemand = !selfTest && Environment.GetEnvironmentVariable("COSMIRIA_ON_DEMAND") == "1";
        ui.Invalidated += Wake;
        ui.SetRoot(new App());

        // Restore the dock layout saved on the last exit (not in the self-test, which must be repeatable).
        if (!selfTest && File.Exists(LayoutFile) && DockLayoutText.TryParse(File.ReadAllText(LayoutFile), out var saved))
            ui.Root<App>().Layout = saved;

        Game.Screen = new Texture(GraphicsDevice, Game.Width, Game.Height, TextureFormat.R8G8B8A8_UNORM,
            TextureUsageFlags.Sampler | TextureUsageFlags.ColorTarget);
        sprite = CreateSprite();
    }

    /// <summary>A 12x12 pixel-art face: scaled up with nearest sampling, its pixels stay square.</summary>
    private Texture CreateSprite()
    {
        string[] art =
        [
            "....YYYY....", "..YYYYYYYY..", ".YYYYYYYYYY.", ".YYKYYYYKYY.", "YYYKYYYYKYYY", "YYYYYYYYYYYY",
            "YYYYYYYYYYYY", "YKYYYYYYYYKY", ".YKYYYYYYKY.", ".YYKKKKKKYY.", "..YYYYYYYY..", "....YYYY....",
        ];
        var pixels = new byte[12 * 12 * 4];
        for (int y = 0; y < 12; y++)
        for (int x = 0; x < 12; x++)
        {
            uint c = art[y][x] switch { 'Y' => 0xF2C94CFFu, 'K' => 0x2E3440FFu, _ => 0u };
            int o = (y * 12 + x) * 4;
            pixels[o] = (byte)(c >> 24); pixels[o + 1] = (byte)(c >> 16); pixels[o + 2] = (byte)(c >> 8); pixels[o + 3] = (byte)c;
        }
        var texture = new Texture(GraphicsDevice, 12, 12, TextureFormat.R8G8B8A8_UNORM, TextureUsageFlags.Sampler);
        using var transfer = new TransferBuffer(GraphicsDevice, TransferBufferUsage.Upload, (uint)pixels.Length);
        pixels.CopyTo(transfer.Map(true, 0));
        transfer.Unmap();
        CommandBuffer cmd = GraphicsDevice.AcquireCommandBuffer();
        CopyPass copy = cmd.BeginCopyPass();
        copy.UploadToTexture(new TextureTransferInfo(transfer, 0, 12, 12), new TextureRegion(texture, 0, 0, 0, 12, 12, 1), false);
        cmd.EndCopyPass(copy);
        GraphicsDevice.Submit(cmd);
        return texture;
    }

    /// <summary>Draw the game into its render target. The UI samples it when drawn: no rebuild needed.</summary>
    private void RenderGame(CommandBuffer cmd)
    {
        float t = gameTime;
        var sky = new Color((byte)(40 + 30 * MathF.Sin(t * 0.7f)), (byte)(70 + 30 * MathF.Sin(t * 0.5f + 1)), 140, 255);
        RenderPass pass = cmd.BeginRenderPass(new ColorTargetInfo(Game.Screen, sky, false, LoadOp.Clear));
        cmd.EndRenderPass(pass);
        uint x = (uint)((Game.Width - 12) * (0.5f + 0.5f * MathF.Sin(t * 1.3f)));
        uint y = (uint)((Game.Height - 12) * (0.5f + 0.5f * MathF.Cos(t * 1.7f)));
        cmd.Blit(new BlitRegion(sprite, 0, 0, 0, 12, 12, 0), new BlitRegion(Game.Screen, x, y, 0, 12, 12, 0), Filter.Nearest, false);
    }

    public override void Update(float delta)
    {
        if (selfTest)
        {
            SelfTestStep();
            return;
        }

        ui.Update();
        gameTime += delta;
        if ((int)gameTime != (int)(gameTime - delta)) Game.Score.Value++; // reactive: the UI repaints by itself

        titleTimer += delta;
        if (titleTimer > 0.5f)
        {
            // Draw calls: what the last frame issued (cached layers); batches: what redrawing every
            // element takes, which happens only on rebuilds.
            long paints = ui.RenderStats.Paints;
            float rebuildsPerSecond = (paints - titlePaints) / titleTimer;
            titlePaints = paints;
            titleTimer = 0;
            MainWindow.Title = $"Cosmiria — nodes {ui.NodeCount}, quads {ui.QuadCount}, draw calls {ui.DrawCalls} (batches {ui.Batches}), " +
                $"rebuilds {rebuildsPerSecond:0}/s, hot reloads {HotReload.UpdateCount}";
        }
    }

    private long titlePaints;

    /// <summary>The game screen is on screen: it animates, so every frame is drawn.</summary>
    private static bool GameVisible => Game.ScreenRef?.IsMounted == true;

    /// <summary>
    /// On demand: every frame while the game screen shows; otherwise sleep until the UI's next
    /// deadline (caret, tooltip) or the game's next score tick, whichever comes first.
    /// </summary>
    protected override TimeSpan NextUpdateIn()
    {
        if (GameVisible) return TimeSpan.Zero;
        double untilScore = Math.Ceiling(gameTime + 1e-4) - gameTime;
        return TimeSpan.FromSeconds(Math.Min(ui.SecondsUntilUpdate, untilScore));
    }

    protected override bool ShouldRender() => GameVisible || ui.NeedsRender;

    private static readonly bool trace = Environment.GetEnvironmentVariable("COSMIRIA_TRACE") == "1";
    private static readonly System.Diagnostics.Stopwatch traceClock = System.Diagnostics.Stopwatch.StartNew();

    public override void Render()
    {
        if (trace) Console.WriteLine($"[trace] {traceClock.Elapsed.TotalSeconds,6:0.000} s   render (game visible: {GameVisible})");
        CommandBuffer cmd = GraphicsDevice.AcquireCommandBuffer();
        RenderTarget swapchain = cmd.AcquireSwapchainTarget(MainWindow);
        RenderGame(cmd);
        if (swapchain != null)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            ui.Render(cmd, swapchain, LoadOp.Clear, Color.Black);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (selfTest && frame > 30)
            {
                if (ui.RebuiltLastFrame) { uiAllocRebuild += bytes; rebuildFrames++; }
                else { uiAllocIdle += bytes; idleFrames++; }
                if (bytes > 0 && Environment.GetEnvironmentVariable("COSMIRIA_ALLOCLOG") == "1")
                    Console.WriteLine($"[alloc] render frame {frame}: {bytes} B (rebuilt={ui.RebuiltLastFrame})");
            }
        }
        GraphicsDevice.Submit(cmd);
    }

    // Scripted interactions, targeted by label (TryFindText) so layout changes don't break them.
    // Each step: (label, occurrence, action). Actions are spread 10 frames apart.
    private static readonly (string label, int occurrence, char action)[] script =
    [
        ("Click me", 0, 'c'), ("Click me", 0, 'c'), ("Click me", 0, 'c'),   // Num = 3 -> Hello World
        ("+", 0, 'c'), ("+", 0, 'c'),                                         // counter 0: Add(1) x2
        ("-", 1, 'c'),                                                        // counter 1: Add(-1) -> 9
        ("Item #5", 0, 'c'),                                                  // Select(5)
        ("x", 2, 'c'),                                                        // Remove(3): third row's x
        ("wrap", 0, 'c'),                                                     // C# listener: SelectedTag = 2
        ("Add item", 0, 'c'),                                                 // Add() -> Item #31, scrolled into view (ElementRef)
        ("Item #31", 0, 'V'),
        ("Item #30", 0, 'U'),                                                 // wheel back to the top
        ("Item #7", 0, 'V'),
        ("Item #7", 0, 'h'),                                                  // hover only: Hover(7, $e)
        // StepList (C# component returning nodes): branches, a fragment, a dropped element.
        ("!Restart", 0, 'k'), ("!Never shown", 0, 'k'),
        ("Next step", 0, 'c'), ("Restart", 0, 'k'),
        ("Next step", 0, 'c'), ("Next step", 0, 'c'), ("All steps done", 0, 'k'), ("!Next step", 0, 'k'),
        // Repeat each kind of interaction: these must not allocate (only first use may warm up).
        ("Item #9", 0, 'h'), ("Item #9", 0, 'c'), ("+", 0, 'c'), ("x", 4, 'c'), ("Item #8", 0, 'h'),
        // Widgets panel.
        ("I agree to the terms", 0, 'c'),                                     // Checkbox
        ("Large", 0, 'c'),                                                    // RadioButton
        ("Dark mode", 0, 'c'),                                                // Switch -> off
        ("Like", 0, 'c'),                                                     // ToggleButton
        ("Apple", 0, 'c'),                                                    // ComboBox opens
        ("Cherry", 0, 'c'),                                                   // Option picks + closes
        ("Your name", 0, 'c'),                                                // TextInput focus
        ("Ada\n", 0, 't'),                                                    // type + Enter (submit)
        // Settings panel: tabs + sliders ('d' drags the slider under the label 100px from its left end).
        ("Music: 60", 0, 'd'),                                                // bind:value -> Music = 32
        ("Video", 0, 'c'),                                                    // bind:selected -> Page = 1
        ("VSync", 0, 'c'),                                                    // Switch -> off
        ("Brightness: 0", 0, 'd'),                                            // ondrag="SetBrightness($e)" -> -18
        ("Audio", 0, 'c'),                                                    // back to the first tab
        // Media tab: transformed hit testing. "Moved" is drawn 40px right of its layout box.
        ("Media", 0, 'c'),
        ("Moved", 0, 'c'),                                                    // where it's drawn: MovedClicks = 1
        ("Moved", 0, 'l'),                                                    // its layout box (60px left): misses
        ("Game", 0, 'c'),
        ("Game pixel: 0, 0", 0, 'u'),                                         // click the screen's center -> ~64, 36
        ("Game pixel: 64, 36", 0, 'm'),                                       // move (no click) -> Pointer: 40, 20
        // Notes: text area, transitions.
        ("Notes", 0, 'c'),                                                    // tab next to Widgets
        ("Saved: 0 lines", 0, 'a'),                                           // click into the text area above
        ("\nThird line, long enough to wrap onto another visual line", 0, 't'), // Enter = line break
        ("Toggle box", 0, 'c'),                                               // box animates (transition-all 500ms)
        // Own values and BindableValues.
        ("Own values", 0, 'c'),
        ("Remember me", 0, 'c'),                                              // own checkbox -> checked
        ("L", 0, 'c'),                                                        // own radio group -> L
        ("Bold", 0, 'c'),                                                     // own toggle -> pressed
        ("Banana", 0, 'c'),                                                   // own-open combo opens
        ("Cherry", 0, 'c'),                                                   // bind:group BindableValue -> Cherry, closes
        ("Muted", 0, 'c'),                                                    // BindableValue: checkbox and switch both on
        // Custom events: MoodPicker raises Callback fields the parent filled with onpick / onreset.
        ("Events", 0, 'c'),
        ("Mood: none · 0 changes", 0, 'k'),
        ("Great", 0, 'c'), ("Mood: Great · 1 change", 0, 'k'),          // Callback<int> with $e
        ("Undecided", 0, 'c'), ("Mood: none · 2 changes", 0, 'k'),      // Callback without argument
        ("Good", 2, 'c'), ("Team: ?, Good", 0, 'k'),                   // third picker: captured i = 1
        ("Meh", 1, 'c'), ("Team: Meh, Good", 0, 'k'),                  // second picker: captured i = 0
        ("Undecided", 2, 'c'), ("Team: Meh, ?", 0, 'k'),               // onreset="TeamMood(i, -1)"
        // Explorer: tree, effect, tooltip, context menu, scrollbar.
        ("Explorer", 0, 'c'),
        ("Effect ran 1 time", 0, 'k'),                                        // effect ran on mount
        ("Textures", 0, 'e'),                                                 // chevron: collapse the 24 textures
        ("Sounds", 0, 'e'),                                                   // chevron: expand
        ("click.wav", 0, 'c'),                                                // select
        ("Selected: click.wav", 0, 'k'),
        ("Effect ran 2 times", 0, 'k'),                                       // effect re-ran: deps changed
        ("Add", 0, 'h'), ("", 0, 'w'), ("", 0, 'w'), ("", 0, 'w'),            // rest on the button
        ("Adds a texture to Assets/Textures", 0, 'T'),                        // tooltip shown
        ("Sounds", 0, 'R'),                                                   // right click: the item's own menu, over the rows below
        ("Selected: Sounds", 0, 'k'),                                         // a right click selects the item
        ("Play all", 0, 'b'),                                                 // click the separator: over a tree row
        ("Selected: Sounds", 0, 'k'),                                         // ...which must not be selected through the menu
        ("Play all", 0, 'c'),                                                 // and the menu stayed open
        ("Playing 2 sounds", 0, 'k'),
        ("!Play all", 0, 'k'),
        ("Textures", 0, 'R'),                                                 // the folder's menu
        ("New texture", 0, 'c'),                                              // adds texture25.png, closes
        ("!New texture", 0, 'k'),
        ("Textures", 0, 'e'),                                                 // expand again: 25 textures
        ("texture02.png", 0, 'R'), ("Duplicate", 0, 'c'),                     // a texture's menu
        ("Selected: texture02 copy.png", 0, 'k'),                             // inserted after it, and selected
        ("texture02 copy.png", 0, 'R'), ("Delete texture02 copy.png", 0, 'c'),
        ("Deleted texture02 copy.png", 0, 'k'),
        ("!texture02 copy.png", 0, 'k'),
        ("Selected: nothing", 0, 'k'),                                        // it was selected
        ("texture03.png", 0, 'R'), ("Rename", 0, 'c'),                        // shows the rename box, focused by its ElementRef
        ("hero.png\n", 0, 't'),                                               // typing goes straight into it, over the selected name; Enter submits
        ("Renamed texture03.png to hero.png", 0, 'k'),
        ("hero.png", 0, 'k'), ("!Cancel", 0, 'k'),
        ("!Scenes", 0, 'k'),                                                  // below the fold
        ("0", 0, 'S'),                                                        // drag the tree's scrollbar to the end
        ("Scenes", 0, 'k'),
        // Menus, modal, keyboard focus.
        ("View", 0, 'c'), ("Show panel", 0, 'h'), ("Show Notes", 0, 'c'),     // submenu
        ("Toggle box", 0, 'k'),                                               // Notes is the active tab now
        ("Help", 0, 'c'), ("About Cosmiria...", 0, 'c'),
        ("Tab moves focus, Enter activates, Escape closes this.", 0, 'k'),    // modal open
        ("Escape", 0, 'K'),
        ("!Tab moves focus, Enter activates, Escape closes this.", 0, 'k'),   // closed by Escape
        ("Help", 0, 'c'), ("About Cosmiria...", 0, 'c'),
        ("Tab", 0, 'K'), ("Tab", 0, 'K'),                                     // close (X), then Close button
        ("Return", 0, 'K'),                                                   // activates Close
        ("!Tab moves focus, Enter activates, Escape closes this.", 0, 'k'),
        // Shortcuts (menus closed), reactivity.
        ("F1", 0, 'K'),                                                       // Help > About via its shortcut
        ("Tab moves focus, Enter activates, Escape closes this.", 0, 'k'),
        ("Escape", 0, 'K'),
        ("Ins", 0, 'K'),                                                      // Explorer context menu item, menu closed
        ("26 textures", 0, 'k'),
        ("Ctrl+R", 0, 'K'),                                                   // File > Reset layout
        ("Clicked 3 times", 0, 'k'),                                          // Showcase again: state kept while hidden (keep-alive)
        ("View", 0, 'c'), ("Show panel", 0, 'h'), ("Show Notes", 0, 'c'),
        ("Score: 42 · level 4", 0, 'k'),                                      // set by game code at frame 300, no Notify
        // Docking: drag the List tab into the middle of the Settings group (becomes a tab there).
        ("Explorer>Counters (independent state)", 0, 'O'),                    // dropped off any target: floats in a window
        ("Explorer", 0, 'k'),
        ("!Close window", 0, 'C'),                                            // Explorer isn't closable: no close button on its window
        ("Explorer", 0, 'W'),                                                 // move the window by its tab bar
        ("Explorer>Settings>Dock left", 0, 'X'),                              // drag the window itself onto an anchor: docks beside Settings
        ("Widgets>Own values", 0, 'O'),                                       // one drag past two tabs: Notes, Own values, Widgets
        ("Events>Notes", 0, 'O'),                                             // onto another group's bar: inserted before Notes
        ("List>Settings>Dock as tab", 0, 'D'),
        // Closable panels (closable: true): the window's close button when all its panels are, then a tab's own.
        ("Own values>Counters (independent state)", 0, 'O'),                  // float it
        ("Drag to resize", 0, 'G'),                                           // the grip, over the content's scrollbar: grip wins
        ("440 320 (tabs 0 \"own\")", 0, 'L'),                                 // resized by +80, +60
        ("Close window", 0, 'C'),                                             // the window's close button (by tooltip)
        ("!Own values", 0, 'k'),                                              // closed: not added back automatically
        ("View", 0, 'c'), ("Show panel", 0, 'h'), ("Show Own values", 0, 'c'),
        ("Own values", 0, 'k'),                                               // reopened
        ("Close panel@Own values", 0, 'C'),                                   // the x on its tab
        ("!Own values", 0, 'k'),
        ("(closed \"own\")", 0, 'L'),                                         // remembered as closed
        ("View", 0, 'c'), ("Show panel", 0, 'h'), ("Show Own values", 0, 'c'),
        ("Own values", 0, 'k'),
        // Async: listeners, Suspense with resources, async callbacks.
        ("Async", 0, 'c'),                                                    // its tab: the profile starts loading
        ("Loading profile...", 0, 'k'),                                       // Suspense fallback
        ("Fetch quote", 0, 'c'), ("Loading quote...", 0, 'k'),                // async listener: state set before the await
        ("", 0, 'w'), ("", 0, 'w'), ("", 0, 'w'),
        ("“Simplicity is prerequisite for reliability.”", 0, 'k'),            // ...and after it, on the UI thread
        ("Ada Lovelace", 0, 'k'), ("!Loading profile...", 0, 'k'),           // the resource arrived: content replaces the fallback
        ("Analytical engine programmer · loaded 1 time", 0, 'k'),
        ("Reload", 0, 'c'), ("Loading profile...", 0, 'k'), ("!Ada Lovelace", 0, 'k'),
        ("", 0, 'w'), ("", 0, 'w'), ("", 0, 'w'), ("", 0, 'w'),
        ("Analytical engine programmer · loaded 2 times", 0, 'k'),
        ("Refresh", 0, 'c'),                                                  // keepValue: no fallback, the old data stays
        ("Analytical engine programmer · loaded 2 times · refreshing...", 0, 'k'),
        ("", 0, 'w'), ("", 0, 'w'), ("", 0, 'w'), ("", 0, 'w'),
        ("Analytical engine programmer · loaded 3 times", 0, 'k'),
        ("Failed: 503 Service Unavailable", 0, 'k'),                          // <Failed> with the resource's error
        ("Retry", 0, 'c'), ("Connecting...", 0, 'k'),
        ("", 0, 'w'), ("", 0, 'w'), ("", 0, 'w'),
        ("Failed: 503 Service Unavailable", 0, 'k'),
        ("Save", 0, 'c'), ("Saving...", 0, 'k'), ("Saved 0 times", 0, 'k'),  // the child awaits the parent's async handler
        ("", 0, 'w'), ("", 0, 'w'), ("", 0, 'w'), ("", 0, 'w'),
        ("Saved 1 time", 0, 'k'), ("Save", 0, 'k'),
        ("Tab", 0, 'K'),                                                      // a focus ring for the screenshot
    ];

    private float px = -1, py = -1;
    private int pressFrame = -1;
    private int dragFrames; // moves between press and release ('d' steps)
    private float dragToX = float.NaN, dragToY; // 'D' steps: glide to this point while pressed
    private float dragFromX = float.NaN, dragFromY; // 'O'/'W' steps: glide linearly from here
    private string dockAnchor; // 'D' steps: the anchor (tooltip) to drop on, once over the group

    private static readonly int End = 40 + script.Length * 12; // frame after the last step

    private void SelfTestStep()
    {
        frame++;
        bool press = false, release = false;
        float wheel = 0;

        int step = (frame - 40) / 12;
        int phase = (frame - 40) % 12;
        if (frame >= 40 && step < script.Length && phase == 0)
        {
            var (label, occurrence, action) = script[step];
            int stopAt = int.TryParse(Environment.GetEnvironmentVariable("COSMIRIA_STEPS"), out int n) ? n : int.MaxValue;
            if (step >= stopAt) { }
            else if (action == 't') ui.TypeText(label);
            else if (action == 'w') { }
            else if (action == 'K')
            {
                // "Ctrl+Shift+R" style: modifiers, then the key.
                string[] parts = label.Split('+');
                bool ctrl = parts.Contains("Ctrl"), shift = parts.Contains("Shift"), alt = parts.Contains("Alt");
                string keyName = parts[^1] switch { "Ins" => "Insert", "Del" => "Delete", var k => k };
                ui.SendKey(Enum.Parse<Riateu.Inputs.KeyCode>(keyName), shift, ctrl, alt);
            }
            else if (action == 'T') Console.WriteLine($"[selftest] check tooltip '{label}': {(ui.ActiveTooltip == label ? "OK" : $"FAIL (got '{ui.ActiveTooltip}')")}");
            else if (action == 'V')
            {
                // On screen: found, and laid out inside the window (not scrolled out of view below it).
                bool shown = ui.TryFindText(label, out var r) && r.Y >= 0 && r.Y + r.W <= MainWindow.Height;
                Console.WriteLine($"[selftest] check visible '{label}': {(shown ? "OK" : $"FAIL (y {r.Y})")}");
            }
            else if (action == 'U' && ui.TryFindText(label, out var over))
            {
                px = over.X + over.Z * 0.5f; py = over.Y + over.W * 0.5f;
                wheel = 100; // scroll all the way up
            }
            else if (action == 'k')
            {
                bool expectMissing = label.StartsWith('!');
                string text = expectMissing ? label[1..] : label;
                bool found = ui.TryFindText(text, out _);
                Console.WriteLine($"[selftest] check {(expectMissing ? "missing" : "found")} '{text}': {(found != expectMissing ? "OK" : "FAIL")}");
            }
            else if (action == 'S')
            {
                if (ui.TryFindScrollbar(int.Parse(label), out var thumb))
                {
                    px = thumb.X + thumb.Z * 0.5f; py = thumb.Y + thumb.W * 0.5f;
                    dragToX = px; dragToY = py + 2000; // past the end: clamps to the bottom
                    pressFrame = frame + 2;
                    dragFrames = 6;
                }
                else Console.WriteLine($"[selftest] scrollbar #{label} not found");
            }
            else if (action == 'O')
            {
                // "Tab>Other": drag a tab straight onto another tab (reorder, or insert into its bar).
                string[] parts = label.Split('>');
                if (ui.TryFindText(parts[0], out var from) && ui.TryFindText(parts[1], out var to))
                {
                    px = from.X + from.Z * 0.5f; py = from.Y + from.W * 0.5f;
                    dragToX = to.X + to.Z * 0.5f; dragToY = to.Y + to.W * 0.5f;
                    dragFromX = px; dragFromY = py; // linear: the pointer passes over every tab on the way
                    pressFrame = frame + 2;
                    dragFrames = 8;
                }
                else Console.WriteLine($"[selftest] '{label}' not found");
            }
            else if (action == 'L')
            {
                string layoutText = ui.Root<App>().Layout.Serialize();
                Console.WriteLine($"[selftest] check layout has '{label}': {(layoutText.Contains(label) ? "OK" : $"FAIL ({layoutText})")}");
            }
            else if (action == 'G')
            {
                // Drag the element with this tooltip by (+80, +60).
                if (ui.TryFindTooltip(label, out var r))
                {
                    px = r.X + r.Z * 0.5f; py = r.Y + r.W * 0.5f;
                    dragFromX = px; dragFromY = py;
                    dragToX = px + 80; dragToY = py + 60;
                    pressFrame = frame + 2;
                    dragFrames = 6;
                }
                else Console.WriteLine($"[selftest] tooltip '{label}' not found");
            }
            else if (action == 'C')
            {
                // Click the element with this tooltip (icon buttons have no text). "!tip": check there's
                // none. "tip@Text": the one in the same row as Text, right after it (a tab's close button).
                string[] tip = label.TrimStart('!').Split('@');
                bool hit = false;
                var r = default(System.Numerics.Vector4);
                for (int k = 0; ui.TryFindTooltip(tip[0], out var c, k); k++)
                {
                    if (tip.Length > 1 && !(ui.TryFindText(tip[1], out var t) && MathF.Abs(c.Y + c.W / 2 - (t.Y + t.W / 2)) < 6 && c.X >= t.X + t.Z && c.X < t.X + t.Z + 30)) continue;
                    r = c; hit = true; break;
                }
                if (label.StartsWith('!')) Console.WriteLine($"[selftest] check no '{tip[0]}' button: {(hit ? "FAIL" : "OK")}");
                else if (hit)
                {
                    px = r.X + r.Z * 0.5f; py = r.Y + r.W * 0.5f;
                    dragFromX = float.NaN; dragFrames = 0;
                    pressFrame = frame + 2;
                }
                else Console.WriteLine($"[selftest] tooltip '{label}' not found");
            }
            else if (action == 'W')
            {
                // A floating window's tab bar, right of the tab: drag the window 120 right, 60 down.
                if (ui.TryFindText(label, out var tab))
                {
                    px = tab.X + tab.Z + 30; py = tab.Y + tab.W * 0.5f;
                    dragFromX = px; dragFromY = py;
                    dragToX = px + 120; dragToY = py + 60;
                    pressFrame = frame + 2;
                    dragFrames = 6;
                }
                else Console.WriteLine($"[selftest] '{label}' not found");
            }
            else if (action == 'D' || action == 'X')
            {
                // "Tab>Group>Anchor": drag the tab into the group, then onto that anchor of its cross.
                string[] parts = label.Split('>');
                if (ui.TryFindText(parts[0], out var from) && ui.TryFindText(parts[1], out var to))
                {
                    px = from.X + from.Z * 0.5f; py = from.Y + from.W * 0.5f;
                    if (action == 'X') px = from.X + from.Z + 30; // 'X': a floating window's bar, beside its tab
                    dragToX = to.X + 120; dragToY = to.Y + 300; // inside that group: its anchors appear
                    pressFrame = frame + 2;
                    dragFrames = 5;
                    dockAnchor = parts.Length > 2 ? parts[2] : null;
                }
                else Console.WriteLine($"[selftest] '{label}' not found");
            }
            else if (ui.TryFindText(label, out var r, occurrence))
            {
                px = r.X + r.Z * 0.5f;
                py = r.Y + r.W * 0.5f;
                dragFrames = 0;
                if (action == 'c') pressFrame = frame + 2; // move first, then press
                if (action == 'a') { py -= 60; pressFrame = frame + 2; } // click 60px above the label
                if (action == 'e') { px = r.X - 11; pressFrame = frame + 2; } // a tree row's chevron, left of its label
                if (action == 'b') { py = r.Y + r.W + 5 + 4; px = r.X + 30; pressFrame = frame + 2; } // a menu separator, under the item
                if (action == 'R') ui.ProcessSecondaryPress(px, py);           // right click
                if (action == 'l') { px -= 60; pressFrame = frame + 2; } // click 60px left of the label
                // Game pixel (40, 20) of the 320x180 letterboxed screen (x offset 103, scale 2.5); no click.
                // Game pixel (40, 20) of the letterboxed screen (the label is as wide as the image above it).
                float k = MathF.Min(r.Z / 128f, 180f / 72f), ox = (r.Z - 128 * k) * 0.5f, oy = (180 - 72 * k) * 0.5f;
                if (action == 'm') { px = r.X + ox + 40 * k + 1; py = r.Y - 8 - 180 + oy + 20 * k + 1; }
                if (action == 'u') { px = r.X + r.Z * 0.5f; py = r.Y - 8 - 90; pressFrame = frame + 2; } // center of the 180px-tall image above the label
                if (action == 'd')
                {
                    // The slider sits below its label (panel gap 8) and is 16px tall; its value
                    // range starts 8px in (the knob radius).
                    px = r.X + 8;
                    py = r.Y + r.W + 8 + 8;
                    pressFrame = frame + 2;
                    dragFrames = 5;
                }
            }
            else Console.WriteLine($"[selftest] '{label}' #{occurrence} not found");
        }
        if (frame == pressFrame) press = true;
        if (frame > pressFrame && frame <= pressFrame + dragFrames)
        {
            if (float.IsNaN(dragToX)) px += 20; // 5 moves = 100px
            else if (!float.IsNaN(dragFromX))
            {
                float t = (frame - pressFrame) / (float)dragFrames;
                px = dragFromX + (dragToX - dragFromX) * t; py = dragFromY + (dragToY - dragFromY) * t;
            }
            else
            {
                float k = (frame - pressFrame) / (float)dragFrames; // glide to the drop point
                px += (dragToX - px) * k; py += (dragToY - py) * k;
            }
        }
        if (frame == pressFrame + dragFrames + 1 && dockAnchor != null)
        {
            // Over the group now: glide on to the anchor and release there.
            bool found = ui.TryFindTooltip(dockAnchor, out var anchor);
            Console.WriteLine($"[selftest] dock anchor '{dockAnchor}' shown: {(found ? "OK" : "FAIL")}");
            if (found) { dragToX = anchor.X + anchor.Z * 0.5f; dragToY = anchor.Y + anchor.W * 0.5f; dragFrames += 3; }
            dockAnchor = null;
        }
        if (frame == pressFrame + dragFrames + 1) dragToX = dragFromX = float.NaN;
        if (frame == pressFrame + dragFrames + 1) release = true;

        if (frame == 300) Game.Score.Value = 42; // game code; no ui.Notify()
        long before = GC.GetAllocatedBytesForCurrentThread();
        ui.ProcessKeyboard();
        ui.ProcessPointer(px, py, press, release, wheel);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        if (frame > 30 && frame < End) uiAllocRebuild += bytes; // interaction frames
        if (bytes > 0 && frame > 30 && Environment.GetEnvironmentVariable("COSMIRIA_ALLOCLOG") == "1")
            Console.WriteLine($"[alloc] frame {frame} step {(frame - 40) / 12} phase {(frame - 40) % 12}: {bytes} B");

        if (frame == End + 10)
        {
            // Shaping: kerning pulls A and V together (the pair is narrower than its letters apart).
            UIFont uiFont = ui.Fonts[0];
            float pair = uiFont.Advance("AV"), apart = uiFont.Advance("A") + uiFont.Advance("V");
            Console.WriteLine($"[selftest] kerning AV {pair:0.0} < {apart:0.0}: {(pair < apart ? "OK" : "FAIL")}");
            string text = ui.Root<App>().Layout.Serialize();
            bool roundTrip = DockLayoutText.TryParse(text, out var parsed) && parsed.Serialize() == text;
            Console.WriteLine($"[selftest] layout {text} round-trip: {(roundTrip ? "OK" : "FAIL")}");
            Console.WriteLine($"[selftest] nodes={ui.NodeCount} quads={ui.QuadCount} batches={ui.Batches} selectedTag={ui.Root<App>().SelectedTag}");
            Console.WriteLine($"[selftest] alloc: idle frames={idleFrames} bytes={uiAllocIdle}; rebuild/interaction frames={rebuildFrames} bytes={uiAllocRebuild}");
            RenderStats st = ui.RenderStats;
            Console.WriteLine($"[selftest] render: {st.Paints} paints, {st.QuadsPainted} quads; text quads reused {st.TextQuadsReused} ({100.0 * st.TextQuadsReused / Math.Max(1, st.QuadsPainted):0}% of all); " +
                $"uploaded {st.QuadsUploaded} quads in {st.Uploads} uploads ({100.0 * st.QuadsUploaded / Math.Max(1, st.QuadsPainted):0}% of painted)");
            Console.WriteLine($"[selftest] layers: {st.Composites} frames composited, {st.LayerRenders} layer renders; last frame {ui.DrawCalls} draw calls for {ui.Batches} batches");
        }
        int dumpAt = int.TryParse(Environment.GetEnvironmentVariable("COSMIRIA_DUMP_AT"), out int at) ? at : End + 15;
        if (frame == dumpAt && Environment.GetEnvironmentVariable("COSMIRIA_DUMP") == "1") ui.DumpLayout(Console.Out);
        int shotAt = int.TryParse(Environment.GetEnvironmentVariable("COSMIRIA_SCREENSHOT_AT"), out int sa) ? sa : End + 20;
        if (frame == shotAt && Environment.GetEnvironmentVariable("COSMIRIA_SCREENSHOT") is { Length: > 0 } shot)
        {
            Screenshot(shot);
            Console.WriteLine($"[selftest] screenshot frame {frame}: {ui.DrawCalls} draw calls for {ui.Batches} batches");
        }
        if (frame == End + 70) Quit();
    }

    /// <summary>Render the UI offscreen and save it as a PNG (self-test only).</summary>
    private void Screenshot(string path)
    {
        uint w = (uint)MainWindow.Width, h = (uint)MainWindow.Height;
        using var target = new Texture(GraphicsDevice, w, h, MainWindow.SwapchainFormat, TextureUsageFlags.Sampler | TextureUsageFlags.ColorTarget);
        CommandBuffer cmd = GraphicsDevice.AcquireCommandBuffer();
        RenderGame(cmd);
        ui.Render(cmd, target, LoadOp.Clear, Color.Black);
        GraphicsDevice.Submit(cmd);
        var bytes = new byte[w * h * 4];
        target.Download(bytes);
        if (MainWindow.SwapchainFormat.ToString().StartsWith("B8G8R8A8"))
            for (int i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
        using var image = new Image(bytes, (int)w, (int)h);
        image.WritePNG(path);
        Console.WriteLine($"[selftest] screenshot {w}x{h} -> {path}");
    }

    private static string LayoutFile => Path.Combine(AppContext.BaseDirectory, "layout.txt");

    public override void Destroy()
    {
        if (!selfTest && ui.Root<App>().Layout is { } layout) File.WriteAllText(LayoutFile, layout.Serialize());
        ui.Dispose();
        Game.Screen?.Dispose();
        sprite?.Dispose();
    }

    public static void Main()
    {
        var program = new Program(
            new WindowSettings("Cosmiria", 1280, 800, WindowMode.Windowed, new Flags(Resizable: true)),
            GraphicsSettings.Vsync);

        // Quit cleanly on SIGTERM/SIGINT (e.g. when `dotnet watch` restarts the app) instead of
        // being torn down mid-frame with the GPU device still alive.
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; program.Quit(); });
        using var interrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGINT, ctx => { ctx.Cancel = true; program.Quit(); });

        program.Run();
    }
}
