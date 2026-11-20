using Riateu;
using Riateu.Graphics;
using Riateu.Inputs;
using SDL3;

namespace Cosmiria;

/// <summary>
/// The UI runtime (GPUI's App + Window). Owns component state, builds the element tree
/// when something changed, lays it out, handles input and draws it.
/// <code>
/// ui = new UIContext(GraphicsDevice, MainWindow, font);
/// ui.SetRoot(new MyRoot());
/// // Update(): ui.Update();    Render(): ui.Render(cmd, swapchain);
/// </code>
/// </summary>
public sealed class UIContext : IDisposable
{
    internal const int RootSeed = 0x2545F491;

    public Theme Theme { get; } = new Theme();
    public FontRegistry Fonts { get; } = new FontRegistry();

    /// <summary>Lines scrolled per mouse-wheel notch, in pixels.</summary>
    public float ScrollSpeed = 40f;

    internal int Build { get; private set; }

    private readonly Window window;
    private readonly UIStack stack;
    private readonly EventStack events;
    private EventBinder[] binders = new EventBinder[16];
    private readonly FlexLayout layout = new();
    private readonly UIRenderer renderer;

    private ComponentTable[] tables = new ComponentTable[16];
    private readonly TransitionEngine transitions = new();
    private int resolveOrder, blockerOrder = -1; // modal blocking: nodes resolved before the last blocker ignore input
    // Pointer occlusion by overlays (popups): under the pointer, nodes painted before the topmost
    // overlay containing it are covered, except that overlay's ancestors (like CSS :hover).
    private int occluderOrder = -1;
    private readonly HashSet<int> occluderAncestors = new();
    private int focusAnyIndex = -1;              // node index of the focused element (any kind)
    private readonly List<EventHandle> pendingEffects = new();
    private readonly List<(ShortcutKey key, EventHandle run)> shortcuts = new();

    // Scrollbar dragging.
    private int scrollDragId;
    private float scrollGrab;

    // Tooltip timing.
    private int tooltipId;
    private double tooltipSince;
    private bool tooltipSuppressed;
    /// <summary>Seconds the pointer rests on an element before its tooltip shows.</summary>
    public float TooltipDelay = 0.5f;
    private double buildTime; // seconds, for transitions
    private BindingStore[] bindings = new BindingStore[4];
    private ComponentTable rootTable;
    private int rootSlot = -1;
    private int rootNode = -1;
    private int rootStableId;

    // Volatile: Notify() may be called from the hot reload thread.
    private volatile bool dirty = true;
    private bool rebuiltSinceRender;
    private float builtW = -1, builtH = -1;

    // Interaction state, keyed by node stable ids so it survives rebuilds.
    private (int index, int parent, int parentId, int sibling)[] overlays = new (int, int, int, int)[8];
    private int overlayCount;
    private bool resolvingOverlays;
    private int overlayRoot = -1;

    private HashSet<int> hovered = new();
    private HashSet<int> hoveredNext = new();
    private int pressedId;
    private readonly Dictionary<int, float> scrollOffsets = new();
    private readonly Func<int, float> getScroll;
    private readonly Action<int, float> setScroll;

    private int[] interactive = new int[64];
    private int interactiveCount;
    private int[] scrollables = new int[16];
    private int scrollableCount;

    private (EventHandle handle, PointerEvent e)[] queue = new (EventHandle, PointerEvent)[16];
    private int queueCount;

    private float lastMouseX = float.NaN, lastMouseY = float.NaN;

    // Text input focus & keyboard.
    private readonly FocusState focus = new();
    private TextBuffer focusedBuffer;
    private int focusedIndex = -1;
    private bool pointerHeld, selectingText;
    private readonly List<char> pendingChars = new();
    private readonly Action<char> onChar;
    private float blinkTimer;
    private static readonly KeyCode[] repeatKeys = { KeyCode.Backspace, KeyCode.Delete, KeyCode.Left, KeyCode.Right, KeyCode.Home, KeyCode.End, KeyCode.Up, KeyCode.Down };
    private readonly float[] repeatTimers = new float[8];
    private const float RepeatDelay = 0.45f, RepeatInterval = 0.035f, BlinkInterval = 0.53f;

    public UIContext(GraphicsDevice device, Window window, SpriteFont defaultFont)
    {
        this.window = window;
        events = new EventStack(this);
        stack = new UIStack(this, events);
        renderer = new UIRenderer(device, window.SwapchainFormat, focus, Theme);
        onChar = c => pendingChars.Add(c);
        Keyboard.TextInput += onChar;
        Fonts.Add(defaultFont);

        HotReload.Register(this);
        Reactive.Register(this);
        uiThread = Environment.CurrentManagedThreadId;
        sync = new UISynchronizationContext(this);
        // Awaits in listeners resume here (unless the app brought its own context).
        if (SynchronizationContext.Current == null) SynchronizationContext.SetSynchronizationContext(sync);
        getScroll = id => scrollOffsets.TryGetValue(id, out float v) ? v : 0f;
        setScroll = (id, v) => scrollOffsets[id] = v;
    }

    // ------------------------------------------------------------------ async

    private readonly int uiThread;
    private readonly UISynchronizationContext sync;

    /// <summary>True on the thread that created this UI (the one that may touch component state).</summary>
    public bool IsUIThread => Environment.CurrentManagedThreadId == uiThread;

    /// <summary>
    /// An exception escaped an async listener or callback (or code posted to the UI thread).
    /// Without a handler it's written to the error output; cancellation (the component unmounting)
    /// isn't reported.
    /// </summary>
    public event Action<Exception> UnhandledException;

    /// <summary>Async listeners and callbacks still running (for diagnostics, or a global busy indicator).</summary>
    public int PendingTasks { get; private set; }

    /// <summary>Run <paramref name="action"/> on the UI thread at the start of the next <see cref="Update"/> or <see cref="Render"/> (any thread).</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        sync.Post(static a => ((Action)a)(), action);
    }

    /// <summary>Run continuations and work posted to the UI thread.</summary>
    private void RunPosted()
    {
        if (!sync.HasWork) return;
        SynchronizationContext previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(sync); // continuations they start come back here too
        try { sync.Run(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    internal void ReportException(Exception e)
    {
        if (e is OperationCanceledException) return;
        if (UnhandledException != null) UnhandledException(e);
        else Console.Error.WriteLine($"[cosmiria] unhandled exception in async UI code: {e}");
    }

    /// <summary>Count <paramref name="task"/> as pending, and report it if it fails.</summary>
    internal void Observe(Task task)
    {
        if (task == null) return;
        if (task.IsCompleted)
        {
            if (task.IsFaulted) ReportException(task.Exception.InnerException ?? task.Exception);
            return;
        }
        PendingTasks++;
        _ = Watch(task);
    }

    private async Task Watch(Task task)
    {
        try { await task; }
        catch (Exception e) { ReportException(e); }
        finally { PendingTasks--; Notify(); }
    }

    /// <summary>Cancel the unmount tokens of components freed by the last build (after it, so no user code runs mid-build).</summary>
    private static void CancelUnmounted()
    {
        List<CancellationTokenSource> list = ComponentTable.Unmounting;
        if (list.Count == 0) return;
        CancellationTokenSource[] items = list.ToArray();
        list.Clear();
        foreach (CancellationTokenSource cts in items)
        {
            try { cts.Cancel(); } catch (AggregateException) { }
            cts.Dispose();
        }
    }

    /// <summary>Set the root component. <paramref name="initial"/> is its starting state.</summary>
    public void SetRoot<T>(in T initial) where T : struct, IComponent
    {
        var table = GetTable<T>();
        int stableId = UIStack.Hash(RootSeed, table.TypeId + 1, -1);
        rootTable = table;
        rootSlot = table.GetOrCreate(stableId, initial, Build, out _);
        rootStableId = stableId;
        dirty = true;
    }

    /// <summary>Access the root component's state (e.g. to change it from game code; call <see cref="Notify"/> after).</summary>
    public ref T Root<T>() where T : struct, IComponent
    {
        if (rootTable is not ComponentTable<T> t)
            throw new InvalidOperationException($"The root component is not {typeof(T).Name}.");
        return ref t.Get(rootSlot);
    }

    /// <summary>Request a rebuild on the next frame (GPUI's <c>cx.notify()</c>). Any thread.</summary>
    public void Notify()
    {
        dirty = true;
        Invalidated?.Invoke();
    }

    // ------------------------------------------------------------------ on-demand rendering

    /// <summary>
    /// Raised by <see cref="Notify"/>, from whatever thread changed the state (a reactive value
    /// set by game code or background work, an async result). For apps that sleep while idle: wake
    /// the loop here, e.g. <c>ui.Invalidated += Wake</c> with Riateu's <c>RenderOnDemand</c>.
    /// </summary>
    public event Action Invalidated;

    /// <summary>
    /// The UI has something to show that the last rendered frame doesn't: a rebuild is due, an
    /// animation is running, or a repaint hasn't been drawn yet. An app that renders on demand
    /// renders when this is true (checked after <see cref="Update"/>).
    /// </summary>
    public bool NeedsRender =>
        dirty || transitions.Animating || renderer.HasPendingWork || queueCount > 0 || sync.HasWork ||
        window.Width != builtW || window.Height != builtH;

    /// <summary>
    /// Seconds until the UI needs <see cref="Update"/> again without new input: the next caret
    /// blink, tooltip appearance or key repeat. 0: now (something is due or animating);
    /// <see cref="double.PositiveInfinity"/>: nothing is scheduled, sleep until input.
    /// </summary>
    public double SecondsUntilUpdate
    {
        get
        {
            if (NeedsRender) return 0;
            double next = double.PositiveInfinity;
            if (focusedBuffer != null)
            {
                next = Math.Min(next, Math.Max(0, BlinkInterval - blinkTimer));
                Keyboard kb = Input.Keyboard;
                if (kb != null)
                    for (int i = 0; i < repeatKeys.Length; i++)
                        if (kb.IsDown(repeatKeys[i])) next = Math.Min(next, Math.Max(0, repeatTimers[i]));
            }
            if (tooltipId != 0 && !tooltipSuppressed && focus.TooltipText == null)
            {
                double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
                next = Math.Min(next, Math.Max(0, TooltipDelay - (now - tooltipSince)));
            }
            return next;
        }
    }

    /// <summary>True when the last <see cref="Render"/> rebuilt the tree (for diagnostics).</summary>
    public bool RebuiltLastFrame { get; private set; }
    public int NodeCount => stack.NodeCount;
    /// <summary>
    /// Draw calls the last <see cref="Render"/> actually issued. With cached layers (the default)
    /// that's one per layer plus one per live texture, whatever the UI holds; see <see cref="Batches"/>
    /// for what redrawing every element would take.
    /// </summary>
    public int DrawCalls => renderer.FrameDrawCalls;
    /// <summary>
    /// The UI's batches: the draw calls it takes to draw every element (a new one per clip or image).
    /// They're issued only when the layers are redrawn after a rebuild, or every frame with
    /// <see cref="CacheLayers"/> off.
    /// </summary>
    public int Batches => renderer.Batches;
    public int QuadCount => renderer.QuadCount;
    /// <summary>Glyph quads the last rebuild copied from the frame before instead of placing them (diagnostics).</summary>
    public int ReusedTextQuads => renderer.ReusedTextQuads;
    /// <summary>Quads the last upload sent to the GPU; the rest were already there (diagnostics).</summary>
    public int UploadedQuads => renderer.LastUploadQuads;
    /// <summary>Running totals of paint and upload work since startup: how much was reused (diagnostics).</summary>
    public RenderStats RenderStats => renderer.Stats;

    /// <summary>
    /// Keep the UI's pixels in layer textures between rebuilds (on by default): a frame without a
    /// rebuild draws one quad per layer instead of every element. Textures shown by
    /// <c>ui.Image(texture)</c> (a game screen) are drawn live between layers. Turn it off to
    /// redraw every element every frame (less memory: no window-sized layer textures).
    /// </summary>
    public bool CacheLayers
    {
        get => renderer.CacheLayers;
        set { renderer.CacheLayers = value; dirty = true; }
    }

    internal EventBinder<T, TArg> GetBinder<T, TArg>() where T : struct, IComponent
    {
        int id = EventBinderTypeId<T, TArg>.Id;
        if (id >= binders.Length) Array.Resize(ref binders, Math.Max(id + 1, binders.Length * 2));
        return (EventBinder<T, TArg>)(binders[id] ??= new EventBinder<T, TArg>());
    }

    private EventBinder[] widgetBinders = new EventBinder[8];

    internal WidgetBinder<TArg> GetWidgetBinder<TArg>()
    {
        int id = WidgetBinderTypeId<TArg>.Id;
        if (id >= widgetBinders.Length) Array.Resize(ref widgetBinders, Math.Max(id + 1, widgetBinders.Length * 2));
        return (WidgetBinder<TArg>)(widgetBinders[id] ??= new WidgetBinder<TArg>());
    }

    // Own values (BindableValue boxes and text buffers owned by the framework), per
    // (component, site, occurrence), kept while used and dropped when a build no longer asks for them.
    private readonly Dictionary<(int component, int site, int occurrence), (object box, int build)> owned = new();
    private readonly Dictionary<(int component, int site), int> occurrences = new();
    private readonly List<(int, int, int)> deadOwned = new();

    internal int NextOccurrence(int component, int site)
    {
        ref int n = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(occurrences, (component, site), out _);
        return n++;
    }

    internal BindableValue<T> OwnBox<T>(int component, int site, int occurrence, T initial)
    {
        ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(owned, (component, site, occurrence), out _);
        if (entry.box is not BindableValue<T> box) entry.box = box = new BindableValue<T>(initial); // new, or the type changed (hot reload)
        entry.build = Build;
        return box;
    }

    internal TextBuffer OwnText(int component, int site, int occurrence, string initial)
    {
        ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(owned, (component, site, occurrence), out _);
        if (entry.box is not TextBuffer buffer) entry.box = buffer = new TextBuffer(initial);
        entry.build = Build;
        return buffer;
    }

    // Keep-alive: components not rendered this build that survive, by stable id.
    private readonly Dictionary<int, (int table, int slot, int parent, bool keep, bool rendered)> slotInfo = new();
    private readonly Dictionary<int, bool> survives = new();
    private readonly HashSet<int> frozen = new();

    /// <summary>
    /// Decide which unrendered components survive this build: a component survives if its parent
    /// does and it's marked keep-alive, or its parent wasn't rendered either (a frozen subtree:
    /// children can't render while their parent doesn't). Survivors are touched so the sweep
    /// keeps them, and remembered as frozen so their own values are kept too.
    /// </summary>
    private void KeepAlive()
    {
        slotInfo.Clear();
        survives.Clear();
        frozen.Clear();
        bool any = false;
        for (int t = 0; t < tables.Length; t++)
        {
            ComponentTable table = tables[t];
            if (table == null) continue;
            for (int i = 0; i < table.SlotCount; i++)
            {
                if (!table.SlotInfo(i, out int id, out int parent, out bool keep, out int build)) continue;
                slotInfo[id] = (t, i, parent, keep, build == Build);
                if (build != Build) any = true;
            }
        }
        if (!any) return;
        foreach (var (id, info) in slotInfo)
        {
            if (info.rendered || !Survives(id)) continue;
            tables[info.table].Touch(info.slot, Build);
            frozen.Add(id);
        }
    }

    private bool Survives(int id)
    {
        if (!slotInfo.TryGetValue(id, out var info)) return true; // the root seed / not a component
        if (info.rendered) return true;
        if (survives.TryGetValue(id, out bool known)) return known;
        survives[id] = false; // guards against cycles
        bool parentRendered = !slotInfo.TryGetValue(info.parent, out var p) || p.rendered;
        bool result = Survives(info.parent) && (info.keep || !parentRendered);
        survives[id] = result;
        return result;
    }

    private void SweepOwned()
    {
        foreach (var pair in owned)
            if (pair.Value.build != Build && !frozen.Contains(pair.Key.component)) deadOwned.Add(pair.Key);
        foreach (var key in deadOwned) owned.Remove(key);
        deadOwned.Clear();
        occurrences.Clear();
    }

    internal ComponentTable<T> GetTable<T>() where T : struct, IComponent
    {
        int id = ComponentTypeId<T>.Id;
        if (id >= tables.Length) Array.Resize(ref tables, Math.Max(id + 1, tables.Length * 2));
        return (ComponentTable<T>)(tables[id] ??= new ComponentTable<T>());
    }

    internal BindingStore<TValue> GetBindings<TValue>()
    {
        int id = BindingTypeId<TValue>.Id;
        if (id >= bindings.Length) Array.Resize(ref bindings, Math.Max(id + 1, bindings.Length * 2));
        return (BindingStore<TValue>)(bindings[id] ??= new BindingStore<TValue>());
    }

    // ------------------------------------------------------------------ input

    /// <summary>Process mouse input and dispatch events. Call from <c>GameApp.Update</c>.</summary>
    public void Update()
    {
        RunPosted();
        ProcessKeyboard();
        Mouse mouse = Input.Mouse;
        if (mouse.RightButton.Pressed) ProcessSecondaryPress(mouse.X, mouse.Y);
        ProcessPointer(mouse.X, mouse.Y, mouse.LeftButton.Pressed, mouse.LeftButton.Released, mouse.WheelY);
    }

    /// <summary>A right click (context menus). <see cref="Update"/> calls it; use it for other pointers or tests.</summary>
    public void ProcessSecondaryPress(float mx, float my)
    {
        EnsureBuilt();
        ComputeOcclusion(mx, my);
        HideTooltip(suppress: true);
        RunOutside(mx, my);
        int target = TopmostInteractive(mx, my, NodeFlags.ContextMenu);
        if (target >= 0)
        {
            ref NodeData n = ref stack.Nodes[target];
            Enqueue(n.OwnContext, PointerEventKind.ContextMenu, ref n, mx, my);
            Enqueue(n.ContextMenu, PointerEventKind.ContextMenu, ref n, mx, my);
        }
        DispatchQueued();
    }

    private void RunOutside(float mx, float my)
    {
        for (int i = 0; i < interactiveCount; i++)
        {
            ref NodeData o = ref stack.Nodes[interactive[i]];
            if ((o.Flags & NodeFlags.ClickOutside) != 0 && !SubtreeContains(interactive[i], mx, my))
            {
                Enqueue(o.OwnOutside, PointerEventKind.ClickOutside, ref o, mx, my);
                Enqueue(o.Outside, PointerEventKind.ClickOutside, ref o, mx, my);
            }
        }
    }

    /// <summary>
    /// Feed pointer input directly (window coordinates). <see cref="Update"/> calls this with the
    /// mouse; use it for virtual cursors, gamepad pointers or automated tests.
    /// </summary>
    /// <param name="wheel">Wheel notches this frame; positive scrolls up.</param>
    public void ProcessPointer(float mx, float my, bool pressed, bool released, float wheel)
    {
        if (window.Width != builtW || window.Height != builtH) dirty = true;
        EnsureBuilt();

        bool moved = mx != lastMouseX || my != lastMouseY;
        lastMouseX = mx; lastMouseY = my;
        ComputeOcclusion(mx, my);

        if (moved || pressed || released)
        {
            UpdateHover(mx, my);
            UpdateCursor(mx, my);
            UpdateTooltip(mx, my);
            UpdateScrollbarHover(mx, my);
        }
        TickTooltip(mx, my);

        // Scrollbar thumbs take the press before anything under them, and own the drag.
        if (pressed && BeginScrollbarDrag(mx, my)) { DispatchQueued(); return; }
        if (scrollDragId != 0)
        {
            if (moved) DragScrollbar(mx, my);
            if (released) { scrollDragId = 0; focus.ScrollbarActive = 0; dirty = true; }
            DispatchQueued();
            return;
        }

        if (moved)
        {
            int target = TopmostInteractive(mx, my, NodeFlags.Move);
            if (target >= 0)
            {
                ref NodeData n = ref stack.Nodes[target];
                Enqueue(n.Move, PointerEventKind.Move, ref n, mx, my);
            }
        }

        if (pressed)
        {
            HideTooltip(suppress: true);
            RunOutside(mx, my);

            int target = TopmostInteractive(mx, my, NodeFlags.Pressable);
            pointerHeld = true;
            if (target >= 0 && stack.Nodes[target].Kind == NodeKind.TextInput) FocusInput(target, mx);
            else if (target >= 0 && (stack.Nodes[target].Flags & NodeFlags.Focusable) != 0) SetFocus(target, visible: false);
            else if (focus.FocusedId != 0) Blur();
            if (target >= 0)
            {
                ref NodeData n = ref stack.Nodes[target];
                pressedId = n.StableId;
                if ((n.Flags & NodeFlags.Active) != 0) dirty = true;
                if ((n.Flags & NodeFlags.MouseDown) != 0) Enqueue(n.MouseDown, PointerEventKind.Down, ref n, mx, my);
                if ((n.Flags & NodeFlags.Drag) != 0)
                {
                    Enqueue(n.OwnDrag, PointerEventKind.Drag, ref n, mx, my);
                    Enqueue(n.Drag, PointerEventKind.Drag, ref n, mx, my);
                }
            }
        }
        else if (moved && pressedId != 0)
        {
            // Dragging: the pressed element keeps getting moves, wherever the pointer is.
            int p = FindByStableId(pressedId);
            if (p >= 0 && (stack.Nodes[p].Flags & NodeFlags.Drag) != 0)
            {
                ref NodeData n = ref stack.Nodes[p];
                Enqueue(n.OwnDrag, PointerEventKind.Drag, ref n, mx, my);
                Enqueue(n.Drag, PointerEventKind.Drag, ref n, mx, my);
            }
        }

        if (released)
        {
            int target = TopmostInteractive(mx, my, NodeFlags.Pressable);
            if (target >= 0)
            {
                ref NodeData n = ref stack.Nodes[target];
                if ((n.Flags & NodeFlags.MouseUp) != 0) Enqueue(n.MouseUp, PointerEventKind.Up, ref n, mx, my);
                if (pressedId != 0 && n.StableId == pressedId && (n.Flags & NodeFlags.Click) != 0)
                {
                    Enqueue(n.OwnClick, PointerEventKind.Click, ref n, mx, my); // the widget's own action first
                    Enqueue(n.Click, PointerEventKind.Click, ref n, mx, my);
                }
            }
        }

        if (released)
        {
            pointerHeld = false;
            selectingText = false;
        }
        else if (pointerHeld && selectingText && moved && focusedIndex >= 0 && focusedBuffer != null)
        {
            // Drag-select inside the focused input.
            focusedBuffer.SetCaret(CaretIndexAt(focusedIndex, mx), extendSelection: true);
            ShowCaret();
        }

        if (released && pressedId != 0)
        {
            int p = FindByStableId(pressedId);
            if (p >= 0 && (stack.Nodes[p].Flags & NodeFlags.Drag) != 0)
            {
                ref NodeData dn = ref stack.Nodes[p];
                Enqueue(dn.OwnDrag, PointerEventKind.DragEnd, ref dn, mx, my);
                Enqueue(dn.Drag, PointerEventKind.DragEnd, ref dn, mx, my);
            }
            if (p >= 0 && (stack.Nodes[p].Flags & NodeFlags.Active) != 0)
            {
                dirty = true;
            }
            pressedId = 0;
        }

        if (wheel != 0)
        {
            ScrollAt(mx, my, -wheel * ScrollSpeed);
        }

        DispatchQueued();
    }

    /// <summary>
    /// Recompute the hovered set from the current tree and pointer. Called when the pointer moves
    /// and after every rebuild, so hover state is always a function of what's on screen: elements
    /// that moved under/away from a still pointer get enter/leave, and ids of elements that no
    /// longer exist are dropped.
    /// </summary>
    private void UpdateHover(float mx, float my)
    {
        hoveredNext.Clear();
        for (int i = 0; i < interactiveCount; i++)
        {
            ref NodeData n = ref stack.Nodes[interactive[i]];
            if ((n.Flags & (NodeFlags.Hover | NodeFlags.HoverListener)) == 0) continue;
            bool inside = n.Order >= blockerOrder && !Occluded(interactive[i]) && n.Contains(mx, my);
            if (inside) hoveredNext.Add(n.StableId);
            if (inside != hovered.Contains(n.StableId))
            {
                if ((n.Flags & NodeFlags.Hover) != 0) dirty = true; // hover style changes visuals
                if ((n.Flags & NodeFlags.HoverListener) != 0)
                {
                    Enqueue(n.HoverChanged, inside ? PointerEventKind.HoverEnter : PointerEventKind.HoverLeave, ref n, mx, my);
                }
            }
        }
        (hovered, hoveredNext) = (hoveredNext, hovered);
    }

    private readonly IntPtr[] cursors = new IntPtr[16];
    private CursorShape currentCursor;

    /// <summary>The pressed element's cursor while dragging, else the topmost element's under the pointer.</summary>
    private void UpdateCursor(float mx, float my)
    {
        CursorShape shape = CursorShape.Default;
        int p = pressedId != 0 ? FindByStableId(pressedId) : -1;
        if (p >= 0 && (stack.Nodes[p].Flags & NodeFlags.Cursor) != 0) shape = stack.Nodes[p].Cursor;
        else
        {
            int t = TopmostInteractive(mx, my, NodeFlags.Cursor);
            if (t >= 0) shape = stack.Nodes[t].Cursor;
        }
        if (shape == currentCursor) return;
        currentCursor = shape;
        ref IntPtr cursor = ref cursors[(int)shape];
        if (cursor == IntPtr.Zero)
        {
            cursor = SDL.SDL_CreateSystemCursor(shape switch
            {
                CursorShape.Pointer => SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_POINTER,
                CursorShape.Text => SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_TEXT,
                CursorShape.ResizeX => SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_EW_RESIZE,
                CursorShape.ResizeY => SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_NS_RESIZE,
                CursorShape.Move => SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_MOVE,
                CursorShape.NotAllowed => SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_NOT_ALLOWED,
                CursorShape.Crosshair => SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_CROSSHAIR,
                CursorShape.ResizeDiagonal => SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_NWSE_RESIZE,
                _ => SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_DEFAULT,
            });
        }
        if (cursor != IntPtr.Zero) SDL.SDL_SetCursor(cursor);
    }

    /// <summary>
    /// Find the topmost visible overlay (popup, dropdown, context menu, modal backdrop) whose
    /// subtree contains the pointer: it covers everything painted before it there.
    /// </summary>
    private void ComputeOcclusion(float mx, float my)
    {
        occluderOrder = -1;
        occluderAncestors.Clear();
        for (int i = overlayCount - 1; i >= 0; i--)
        {
            int index = overlays[i].index;
            if (!SubtreeContains(index, mx, my)) continue;
            occluderOrder = stack.Nodes[index].Order;
            for (int p = stack.Nodes[index].Parent; p >= 0; p = stack.Nodes[p].Parent) occluderAncestors.Add(p);
            return;
        }
    }

    /// <summary>Covered by the overlay under the pointer (see <see cref="ComputeOcclusion"/>).</summary>
    private bool Occluded(int index) =>
        occluderOrder >= 0 && stack.Nodes[index].Order < occluderOrder && !occluderAncestors.Contains(index);

    /// <summary>Whether the point is inside the node or any of its descendants (overlays included).</summary>
    private bool SubtreeContains(int index, float mx, float my)
    {
        ref NodeData n = ref stack.Nodes[index];
        if (stack.Styles[index].Display == Display.None) return false;
        if (n.Contains(mx, my)) return true;
        for (int c = n.FirstChild; c >= 0; c = stack.Nodes[c].NextSibling)
        {
            if (SubtreeContains(c, mx, my)) return true;
        }
        return false;
    }

    private int TopmostInteractive(float mx, float my, NodeFlags mask)
    {
        for (int i = interactiveCount - 1; i >= 0; i--)
        {
            int idx = interactive[i];
            ref NodeData n = ref stack.Nodes[idx];
            if ((n.Flags & mask) != 0 && n.Order >= blockerOrder && !Occluded(idx) && n.Contains(mx, my)) return idx;
        }
        return -1;
    }

    private int FindByStableId(int id)
    {
        for (int i = 0; i < interactiveCount; i++)
        {
            if (stack.Nodes[interactive[i]].StableId == id) return interactive[i];
        }
        return -1;
    }

    private void ScrollAt(float mx, float my, float delta)
    {
        // A text area under the pointer scrolls first (until it reaches its end).
        for (int i = interactiveCount - 1; i >= 0; i--)
        {
            ref NodeData t = ref stack.Nodes[interactive[i]];
            if (!t.Multiline || t.ScrollMax <= 0 || t.Order < blockerOrder || Occluded(interactive[i]) || !t.Contains(mx, my)) continue;
            float cur = getScroll(t.StableId);
            float next = Math.Clamp(cur + delta, 0, t.ScrollMax);
            if (next == cur) break;
            scrollOffsets[t.StableId] = next;
            dirty = true;
            return;
        }
        for (int i = scrollableCount - 1; i >= 0; i--)
        {
            ref NodeData n = ref stack.Nodes[scrollables[i]];
            if (n.Order < blockerOrder || Occluded(scrollables[i]) || !n.Contains(mx, my) || n.ScrollMax <= 0) continue;
            float cur = getScroll(n.StableId);
            float next = Math.Clamp(cur + delta, 0, n.ScrollMax);
            if (next != cur)
            {
                scrollOffsets[n.StableId] = next;
                dirty = true;
            }
            return;
        }
    }

    private void Enqueue(EventHandle handle, PointerEventKind kind, ref NodeData n, float mx, float my)
    {
        if (!handle.IsValid) return;
        if (queueCount == queue.Length) Array.Resize(ref queue, queueCount * 2);
        var position = new System.Numerics.Vector2(mx, my);
        // Local coordinates are in the element's own (untransformed) space.
        System.Numerics.Vector2 local = n.ToLocal(mx, my);
        System.Numerics.Vector2 parentPos = default, parentSize = default;
        if (n.Parent >= 0)
        {
            ref NodeData p = ref stack.Nodes[n.Parent];
            ref Style ps = ref stack.Styles[n.Parent];
            float px = p.X + ps.PaddingEdges.Left + ps.BorderWidth, py = p.Y + ps.PaddingEdges.Top + ps.BorderWidth;
            parentPos = local - new System.Numerics.Vector2(px, py);
            parentSize = new System.Numerics.Vector2(
                MathF.Max(0, p.W - ps.PaddingEdges.Horizontal - ps.BorderWidth * 2),
                MathF.Max(0, p.H - ps.PaddingEdges.Vertical - ps.BorderWidth * 2));
        }
        queue[queueCount++] = (handle, new PointerEvent(kind, position, local - new System.Numerics.Vector2(n.X, n.Y),
            new System.Numerics.Vector2(n.W, n.H), parentPos, parentSize));
    }

    /// <summary>
    /// Run queued listeners. They run after hit testing, against the current build's binders
    /// (rebuilt only on the next frame), and each one schedules a re-render.
    /// </summary>
    private void DispatchQueued()
    {
        for (int i = 0; i < queueCount; i++)
        {
            ref var q = ref queue[i];
            q.handle.Binder.Invoke(q.handle.Index, this, q.e);
            q.handle = default;
            dirty = true;
        }
        queueCount = 0;
    }

    // ------------------------------------------------------------------ build

    private void EnsureBuilt()
    {
        if (!dirty || rootTable == null) return;
        // Listeners queued against the current build must run before its binders are reset.
        if (queueCount > 0) DispatchQueued();
        Reactive.BeginBuild();
        IsBuilding = true;
        try { RunBuild(); }
        finally
        {
            IsBuilding = false;
            Reactive.EndBuild();
        }
        CancelUnmounted();
        RunDeferred();
    }

    /// <summary>True while components render (state must not change; see <see cref="Defer"/>).</summary>
    internal bool IsBuilding { get; private set; }

    private readonly List<Action> deferred = new();

    /// <summary>Run <paramref name="action"/> right after the current build (callbacks invoked from a Render).</summary>
    internal void Defer(Action action) => deferred.Add(action);

    private void RunDeferred()
    {
        if (deferred.Count == 0) return;
        // Copy first: an action may defer more (it runs outside a build, so it won't).
        Action[] actions = deferred.ToArray();
        deferred.Clear();
        foreach (Action a in actions) a();
        dirty = true;
    }

    private void RunBuild()
    {
        dirty = false;
        rebuiltSinceRender = true;
        Build++;

        float w = window.Width, h = window.Height;
        builtW = w; builtH = h;

        stack.Reset();
        for (int i = 0; i < binders.Length; i++) binders[i]?.Reset();
        for (int i = 0; i < widgetBinders.Length; i++) widgetBinders[i]?.Reset();
        shortcuts.Clear();
        occurrences.Clear();
        refs.Clear();
        rootNode = stack.CreateNode(NodeKind.View, default(Style)
            .FlexCol().Size(w, h)
            .TextColorOf(Theme.Text).Font(0));

        rootTable.Touch(rootSlot, Build); // root stays alive
        buildTime = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        transitions.BeginBuild();
        UIStack.Building = stack;
        Node content;
        try { content = stack.RenderComponent(rootTable, rootSlot, rootStableId); }
        finally { UIStack.Building = null; }
        if (!content.IsNone) stack.Attach(rootNode, content.Index);

        KeepAlive();
        for (int i = 0; i < tables.Length; i++) tables[i]?.Sweep(Build);
        for (int i = 0; i < bindings.Length; i++) bindings[i]?.Sweep(Build);
        SweepOwned();

        interactiveCount = 0;
        scrollableCount = 0;
        overlayCount = 0;
        resolveOrder = 0;
        blockerOrder = -1;
        resolvingOverlays = false;
        Resolve(rootNode, -1, RootSeed, 0);
        // Overlays are resolved after the main tree so they come last in paint order and are
        // hit-tested first (topmost).
        // Overlays nested in an overlay are queued again, so they come after their ancestor.
        resolvingOverlays = true;
        for (int i = 0; i < overlayCount; i++)
        {
            var o = overlays[i];
            overlayRoot = o.index;
            Resolve(o.index, o.parent, o.parentId, o.sibling);
        }
        overlayRoot = -1;
        resolvingOverlays = false;

        transitions.Sweep(Build);
        if (transitions.Animating) dirty = true; // next frame continues the animation

        layout.Run(stack, Fonts, rootNode, w, h, getScroll, setScroll);
        renderer.Paint(stack, Fonts, rootNode, w, h, (int)window.Width, (int)window.Height, getScroll, setScroll);

        RefreshFocus();
        ResolveRefs();

        // Effects (ui.Effect) whose dependencies changed run after the build, like listeners.
        foreach (EventHandle effect in pendingEffects)
        {
            if (queueCount == queue.Length) Array.Resize(ref queue, queueCount * 2);
            queue[queueCount++] = (effect, default);
        }
        pendingEffects.Clear();

        // The tree changed under a possibly still pointer: re-derive hover from geometry.
        if (!float.IsNaN(lastMouseX))
        {
            ComputeOcclusion(lastMouseX, lastMouseY);
            UpdateHover(lastMouseX, lastMouseY);
            UpdateCursor(lastMouseX, lastMouseY);
        }
    }

    /// <summary>
    /// Assign structural stable ids, apply inheritance + hover/active overlays, and collect
    /// interactive nodes in paint order. A node's id is its explicit .Id() (scoped to its component)
    /// or a hash of its parent's id and its position among siblings, like React keys: conditional
    /// content only affects its own siblings, and a keyed row's children stay tied to that row.
    /// </summary>
    private void Resolve(int index, int parent, int parentId, int siblingIndex)
    {
        ref NodeData n = ref stack.Nodes[index];
        ref Style s = ref stack.Styles[index];
        if (s.IsOverlay && parent >= 0 && (!resolvingOverlays || index != overlayRoot))
        {
            if (overlayCount == overlays.Length) Array.Resize(ref overlays, overlayCount * 2);
            overlays[overlayCount++] = (index, parent, parentId, siblingIndex);
            return;
        }
        if ((n.Flags & NodeFlags.ExplicitId) == 0) n.StableId = UIStack.Hash(parentId, siblingIndex, 0x5bd1e995);
        n.Flags |= NodeFlags.InTree;
        if (parent >= 0 && (stack.Nodes[parent].Flags & NodeFlags.PassThrough) != 0) n.Flags |= NodeFlags.PassThrough;
        n.Order = resolveOrder++;
        if ((n.Flags & NodeFlags.Blocker) != 0) blockerOrder = n.Order;

        if (parent >= 0) s.Inherit(stack.Styles[parent]);
        if (n.HoverStyle >= 0 && hovered.Contains(n.StableId)) s.Merge(stack.ExtraStyle(n.HoverStyle));
        if (n.ActiveStyle >= 0 && pressedId == n.StableId) s.Merge(stack.ExtraStyle(n.ActiveStyle));
        if (n.Kind == NodeKind.TextInput && focus.FocusedId != 0 && n.StableId == focus.FocusedId) s.Merge(Theme.TextInputFocus);
        if (s.Has(StyleFlags.Transition) && s.TransitionProperties != TransitionParts.None)
            transitions.Apply(n.StableId, ref s, buildTime, Build);

        if (s.Display == Display.None) return;

        if ((n.Flags & NodeFlags.Interactive) != 0)
        {
            if (interactiveCount == interactive.Length) Array.Resize(ref interactive, interactiveCount * 2);
            interactive[interactiveCount++] = index;
        }
        if (s.Overflow == Overflow.Scroll)
        {
            if (scrollableCount == scrollables.Length) Array.Resize(ref scrollables, scrollableCount * 2);
            scrollables[scrollableCount++] = index;
        }

        int sibling = 0;
        for (int c = n.FirstChild; c >= 0; c = stack.Nodes[c].NextSibling)
        {
            Resolve(c, index, n.StableId, sibling++);
        }
    }

    // ------------------------------------------------------------------ render

    /// <summary>Draw the UI into <paramref name="target"/> (normally the swapchain).
    /// Uses <see cref="LoadOp.Load"/> by default so it composites over your game.</summary>
    public void Render(CommandBuffer cmd, Texture target, LoadOp loadOp = LoadOp.Load, Color clearColor = default)
    {
        if (window.Width != builtW || window.Height != builtH) dirty = true;
        RunPosted();
        EnsureBuilt();
        renderer.Render(cmd, target, builtW, builtH, loadOp, clearColor);
        if (renderer.TakeAtlasReset()) dirty = true; // icons evicted: repaint them next frame
        RebuiltLastFrame = rebuiltSinceRender;
        rebuiltSinceRender = false;
    }

    /// <summary>
    /// Find the <paramref name="occurrence"/>-th visible text element whose text equals
    /// <paramref name="text"/> and return its rect (window coordinates, where it's drawn: transforms
    /// included, as a bounding box). For tests and automation.
    /// </summary>
    public bool TryFindText(ReadOnlySpan<char> text, out System.Numerics.Vector4 rect, int occurrence = 0)
    {
        EnsureBuilt();
        for (int i = 0; i < stack.NodeCount; i++)
        {
            ref NodeData n = ref stack.Nodes[i];
            if ((n.Flags & NodeFlags.InTree) == 0) continue; // created but never attached: not shown
            if ((n.Kind != NodeKind.Text && n.Kind != NodeKind.TextInput) || !stack.GetText(n).SequenceEqual(text)) continue;
            // Visible: its layout center is inside its clip (both in layout space).
            float cx = n.X + n.W * 0.5f, cy = n.Y + n.H * 0.5f;
            if (n.W <= 0 || n.H <= 0 || cx < n.ClipX0 || cy < n.ClipY0 || cx >= n.ClipX1 || cy >= n.ClipY1) continue;
            if (occurrence-- > 0) continue;
            rect = new System.Numerics.Vector4(n.X, n.Y, n.W, n.H);
            if (n.Transformed && System.Numerics.Matrix3x2.Invert(n.Inverse, out var forward))
            {
                // Where it's drawn: the screen-space bounding box of the transformed rect.
                var a = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(n.X, n.Y), forward);
                var b = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(n.X + n.W, n.Y), forward);
                var c = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(n.X, n.Y + n.H), forward);
                var d = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(n.X + n.W, n.Y + n.H), forward);
                var min = System.Numerics.Vector2.Min(System.Numerics.Vector2.Min(a, b), System.Numerics.Vector2.Min(c, d));
                var max = System.Numerics.Vector2.Max(System.Numerics.Vector2.Max(a, b), System.Numerics.Vector2.Max(c, d));
                rect = new System.Numerics.Vector4(min.X, min.Y, max.X - min.X, max.Y - min.Y);
            }
            return true;
        }
        rect = default;
        return false;
    }

    /// <summary>
    /// Find the visible element whose tooltip is <paramref name="tooltip"/> (window coordinates,
    /// layout box). For tests and automation of elements without text, like icon buttons.
    /// </summary>
    public bool TryFindTooltip(string tooltip, out System.Numerics.Vector4 rect, int occurrence = 0)
    {
        EnsureBuilt();
        for (int i = 0; i < stack.NodeCount; i++)
        {
            ref NodeData n = ref stack.Nodes[i];
            if ((n.Flags & (NodeFlags.InTree | NodeFlags.Tooltip)) != (NodeFlags.InTree | NodeFlags.Tooltip) || n.Tooltip != tooltip) continue;
            if (n.W <= 0 || n.H <= 0) continue;
            if (occurrence-- > 0) continue;
            rect = new System.Numerics.Vector4(n.X, n.Y, n.W, n.H);
            return true;
        }
        rect = default;
        return false;
    }

    /// <summary>Write the current element tree with rects to <paramref name="writer"/> (debugging aid; allocates).</summary>
    public void DumpLayout(TextWriter writer)
    {
        EnsureBuilt();
        if (rootNode >= 0) Dump(writer, rootNode, 0);
    }

    private void Dump(TextWriter w, int index, int depth)
    {
        ref NodeData n = ref stack.Nodes[index];
        w.Write(new string(' ', depth * 2));
        w.Write(n.Kind == NodeKind.Text ? $"Text \"{stack.GetText(n).ToString()}\"" : "View");
        w.WriteLine($" [{n.X:0.#}, {n.Y:0.#}, {n.W:0.#} x {n.H:0.#}] measured {n.MeasuredW:0.#} x {n.MeasuredH:0.#} min {n.MinContentW:0.#} id {n.StableId:x8}{(hovered.Contains(n.StableId) ? " HOVERED" : "")}{(pressedId == n.StableId ? " PRESSED" : "")}");
        for (int c = n.FirstChild; c >= 0; c = stack.Nodes[c].NextSibling) Dump(w, c, depth + 1);
    }

    public void Dispose()
    {
        Keyboard.TextInput -= onChar;
        Reactive.Unregister(this);
        if (focus.FocusedId != 0) SDL.SDL_StopTextInput(window.Handle);
        renderer.Dispose();
        foreach (IntPtr c in cursors) if (c != IntPtr.Zero) SDL.SDL_DestroyCursor(c);
    }

    // ------------------------------------------------------------------ text input

    /// <summary>True while a text input has keyboard focus (e.g. to suppress game hotkeys).</summary>
    public bool HasTextFocus => focus.FocusedId != 0 && focusedBuffer != null;

    /// <summary>Type text into the focused input, as if from the keyboard (automation, on-screen keyboards).</summary>
    public void TypeText(ReadOnlySpan<char> text)
    {
        foreach (char c in text) pendingChars.Add(c);
    }

    /// <summary>Remove keyboard focus from the focused text input.</summary>
    public void Blur()
    {
        if (focus.FocusedId == 0) return;
        focus.FocusedId = 0;
        focus.Visible = false;
        focusedBuffer = null;
        focusedIndex = -1;
        focusAnyIndex = -1;
        selectingText = false;
        SDL.SDL_StopTextInput(window.Handle);
        dirty = true;
    }

    private void FocusInput(int index, float mx)
    {
        ref NodeData n = ref stack.Nodes[index];
        if (n.Buffer == null) return;
        if (focus.FocusedId != n.StableId)
        {
            focus.FocusedId = n.StableId;
            focus.ScrollX = 0;
            SDL.SDL_StartTextInput(window.Handle);
        }
        focusedIndex = index;
        focusAnyIndex = index;
        focusedBuffer = n.Buffer;
        focus.Visible = false; // pointer focus: no ring
        bool shift = Input.Keyboard != null && (Input.Keyboard.IsDown(KeyCode.LeftShift) || Input.Keyboard.IsDown(KeyCode.RightShift));
        focusedBuffer.SetCaret(CaretIndexAt(index, mx), shift);
        selectingText = true;
        ShowCaret();
    }

    /// <summary>After a rebuild, find the focused element again (its index changed) or drop focus.</summary>
    private void RefreshFocus()
    {
        focusedIndex = -1;
        focusAnyIndex = -1;
        if (focus.FocusedId == 0) return;
        for (int i = 0; i < interactiveCount; i++)
        {
            ref NodeData n = ref stack.Nodes[interactive[i]];
            if (n.StableId != focus.FocusedId) continue;
            focusAnyIndex = interactive[i];
            if (n.Kind == NodeKind.TextInput && n.Buffer != null)
            {
                focusedIndex = interactive[i];
                focusedBuffer = n.Buffer;
            }
            return;
        }
        Blur(); // the element is gone
    }

    /// <summary>Focus a non-text element (or a text input without moving its caret).</summary>
    private void SetFocus(int index, bool visible)
    {
        ref NodeData n = ref stack.Nodes[index];
        if (n.Kind == NodeKind.TextInput && n.Buffer != null)
        {
            if (focus.FocusedId != n.StableId) SDL.SDL_StartTextInput(window.Handle);
            focus.FocusedId = n.StableId;
            focus.ScrollX = 0;
            focusedIndex = index;
            focusedBuffer = n.Buffer;
            // Caret at the end, unless the text has a selection to keep (e.g. selected by code
            // before ElementRef.Focus, so typing replaces it).
            if (!focusedBuffer.HasSelection) focusedBuffer.SetCaret(focusedBuffer.Length, false);
            ShowCaret();
        }
        else
        {
            if (focusedBuffer != null) SDL.SDL_StopTextInput(window.Handle);
            focus.FocusedId = n.StableId;
            focusedIndex = -1;
            focusedBuffer = null;
        }
        focusAnyIndex = index;
        focus.Visible = visible;
        dirty = true;
    }

    /// <summary>Tab / Shift+Tab: the next focusable element in paint order (inside the top modal, if any).</summary>
    private void MoveFocus(int direction)
    {
        int count = 0, current = -1;
        for (int i = 0; i < interactiveCount; i++)
        {
            ref NodeData n = ref stack.Nodes[interactive[i]];
            if (!IsTabStop(ref n)) continue;
            if (n.StableId == focus.FocusedId) current = count;
            count++;
        }
        if (count == 0) return;
        int target = current < 0 ? (direction > 0 ? 0 : count - 1) : ((current + direction) % count + count) % count;
        for (int i = 0, k = 0; i < interactiveCount; i++)
        {
            ref NodeData n = ref stack.Nodes[interactive[i]];
            if (!IsTabStop(ref n)) continue;
            if (k++ != target) continue;
            SetFocus(interactive[i], visible: true);
            ScrollIntoView(interactive[i]);
            return;
        }
    }

    /// <summary>Focusable, not under a modal, and visible (or only scrolled out of view).</summary>
    private bool IsTabStop(ref NodeData n)
    {
        if ((n.Flags & (NodeFlags.Focusable | NodeFlags.TextInput)) == 0 || n.Order < blockerOrder || n.W <= 0 || n.H <= 0) return false;
        bool visible = n.X < n.ClipX1 && n.Y < n.ClipY1 && n.X + n.W > n.ClipX0 && n.Y + n.H > n.ClipY0;
        return visible || InScrollable(ref n);
    }

    /// <summary>Clipped only by scroll containers (Tab can scroll it into view).</summary>
    private bool InScrollable(ref NodeData n)
    {
        for (int p = n.Parent; p >= 0; p = stack.Nodes[p].Parent)
            if (stack.Styles[p].Overflow == Overflow.Scroll) return true;
        return false;
    }

    /// <summary>Scroll ancestor containers so the focused element is visible.</summary>
    private void ScrollIntoView(int index)
    {
        ref NodeData n = ref stack.Nodes[index];
        for (int p = n.Parent; p >= 0; p = stack.Nodes[p].Parent)
        {
            ref NodeData c = ref stack.Nodes[p];
            ref Style cs = ref stack.Styles[p];
            if (cs.Overflow != Overflow.Scroll || c.ScrollMax <= 0) continue;
            bool row = cs.Direction == FlexDirection.Row;
            float start = row ? c.X : c.Y, size = row ? c.W : c.H;
            float a = row ? n.X : n.Y, b = a + (row ? n.W : n.H);
            float delta = a < start ? a - start - 8 : b > start + size ? b - (start + size) + 8 : 0;
            if (delta == 0) continue;
            scrollOffsets[c.StableId] = Math.Clamp(getScroll(c.StableId) + delta, 0, c.ScrollMax);
            dirty = true;
        }
    }

    private static readonly KeyCode[] navigationKeys =
        { KeyCode.Tab, KeyCode.Escape, KeyCode.Return, KeyCode.Space, KeyCode.Left, KeyCode.Right, KeyCode.Up, KeyCode.Down };

    /// <summary>Keys for focus and the focused element when it isn't being typed into.</summary>
    private void ProcessNavigationKeys(Keyboard kb, bool shift)
    {
        foreach (KeyCode key in navigationKeys)
            if (kb.IsPressed(key)) HandleNavigationKey(key, shift);
    }

    /// <summary>
    /// Feed a navigation key (Tab, Escape, Enter, Space, arrows) as if pressed: focus moves,
    /// the focused element activates, sliders nudge, modals close. For automation and tests;
    /// typing into text inputs goes through <see cref="TypeText"/>.
    /// </summary>
    public void SendKey(KeyCode key, bool shift = false, bool ctrl = false, bool alt = false)
    {
        EnsureBuilt();
        if (!TryShortcut(key, ctrl, shift, alt)) HandleNavigationKey(key, shift);
        DispatchQueued();
    }

    /// <summary>The tooltip text currently shown (null when none). For tests.</summary>
    public string ActiveTooltip => focus.TooltipText;

    /// <summary>The <paramref name="occurrence"/>-th visible scrollbar thumb (window coordinates). For tests and automation.</summary>
    public bool TryFindScrollbar(int occurrence, out System.Numerics.Vector4 thumb)
    {
        EnsureBuilt();
        for (int i = 0; i < scrollableCount; i++)
        {
            ref NodeData n = ref stack.Nodes[scrollables[i]];
            if (!Scrollbar.Compute(ref n, ref stack.Styles[scrollables[i]], getScroll(n.StableId), out var g)) continue;
            if (occurrence-- > 0) continue;
            thumb = g.Vertical ? new(g.TrackX, g.ThumbStart, g.TrackW, g.ThumbLength) : new(g.ThumbStart, g.TrackY, g.ThumbLength, g.TrackH);
            return true;
        }
        thumb = default;
        return false;
    }

    private void HandleNavigationKey(KeyCode key, bool shift)
    {
        if (key == KeyCode.Tab) { MoveFocus(shift ? -1 : 1); return; }
        bool typing = focusedBuffer != null;
        if (key == KeyCode.Escape && !typing)
        {
            // Close the top modal (its backdrop's own click dismisses it), else drop focus.
            if (blockerOrder >= 0)
            {
                for (int i = interactiveCount - 1; i >= 0; i--)
                {
                    ref NodeData b = ref stack.Nodes[interactive[i]];
                    if ((b.Flags & NodeFlags.Blocker) == 0) continue;
                    Enqueue(b.OwnClick, PointerEventKind.Key, ref b, lastMouseX, lastMouseY);
                    Enqueue(b.Click, PointerEventKind.Key, ref b, lastMouseX, lastMouseY);
                    break;
                }
            }
            else Blur();
            return;
        }
        if (typing || focusAnyIndex < 0) return;
        ref NodeData n = ref stack.Nodes[focusAnyIndex];
        if (key == KeyCode.Return || key == KeyCode.Space)
        {
            float cx = n.X + n.W * 0.5f, cy = n.Y + n.H * 0.5f;
            Enqueue(n.OwnClick, PointerEventKind.Click, ref n, cx, cy);
            Enqueue(n.Click, PointerEventKind.Click, ref n, cx, cy);
            focus.Visible = true;
        }
        if (!float.IsNaN(n.KeyFraction))
        {
            // Sliders: nudge by 5% with a synthesized drag at the new position, so every handler
            // (own value, BindableValue, bind:, ondrag) moves the same way.
            int dir = key == KeyCode.Right || key == KeyCode.Up ? 1 : key == KeyCode.Left || key == KeyCode.Down ? -1 : 0;
            if (dir != 0)
            {
                float f = Math.Clamp(n.KeyFraction + dir * 0.05f, 0f, 1f);
                bool vertical = n.H > n.W;
                float inset = (vertical ? n.W : n.H) * 0.5f, length = (vertical ? n.H : n.W) - inset * 2;
                float lx = vertical ? n.W * 0.5f : inset + f * length;
                float ly = vertical ? inset + (1 - f) * length : n.H * 0.5f;
                Enqueue(n.OwnDrag, PointerEventKind.Drag, ref n, n.X + lx, n.Y + ly);
                Enqueue(n.Drag, PointerEventKind.Drag, ref n, n.X + lx, n.Y + ly);
            }
        }
    }

    // ------------------------------------------------------------------ scrollbars

    private void UpdateScrollbarHover(float mx, float my)
    {
        int hot = 0;
        if (ScrollbarAt(mx, my, out int index, out _, out _)) hot = stack.Nodes[index].StableId;
        if (hot != focus.ScrollbarHot) { focus.ScrollbarHot = hot; dirty = true; }
    }

    /// <summary>
    /// An interactive element is drawn over <paramref name="container"/> at the pointer: painted
    /// after it and not inside it (like a window's resize grip over a panel's scrollbar). Its
    /// content doesn't count: the scrollbar stays above what it scrolls.
    /// </summary>
    private bool CoveredAt(int container, float mx, float my)
    {
        int top = TopmostInteractive(mx, my, NodeFlags.Interactive);
        if (top < 0 || stack.Nodes[top].Order <= stack.Nodes[container].Order) return false;
        for (int p = top; p >= 0; p = stack.Nodes[p].Parent)
            if (p == container) return false;
        return true;
    }

    /// <summary>The topmost scroll container (or text area) whose scrollbar track is under the pointer.</summary>
    private bool ScrollbarAt(float mx, float my, out int index, out Scrollbar.Geometry g, out bool onThumb)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            int count = pass == 0 ? interactiveCount : scrollableCount;
            for (int i = count - 1; i >= 0; i--)
            {
                index = pass == 0 ? interactive[i] : scrollables[i];
                ref NodeData n = ref stack.Nodes[index];
                if (pass == 0 && !n.Multiline) continue;
                if (n.ScrollMax <= 0 || n.Order < blockerOrder || Occluded(index)) continue;
                if (!Scrollbar.Compute(ref n, ref stack.Styles[index], getScroll(n.StableId), out g)) continue;
                System.Numerics.Vector2 p = n.ToLocal(mx, my);
                if (!g.TrackContains(p.X, p.Y) || p.X < n.ClipX0 || p.Y < n.ClipY0 || p.X >= n.ClipX1 || p.Y >= n.ClipY1) continue;
                if (CoveredAt(index, mx, my)) continue; // something drawn over the scrollbar takes the pointer
                onThumb = g.ThumbContains(p.X, p.Y);
                return true;
            }
        }
        index = -1; g = default; onThumb = false;
        return false;
    }

    private bool BeginScrollbarDrag(float mx, float my)
    {
        if (!ScrollbarAt(mx, my, out int index, out Scrollbar.Geometry g, out bool onThumb)) return false;
        ref NodeData n = ref stack.Nodes[index];
        System.Numerics.Vector2 p = n.ToLocal(mx, my);
        float along = g.Vertical ? p.Y : p.X;
        // On the thumb: keep the grab point. On the track: center the thumb there, then drag.
        scrollGrab = onThumb ? along - g.ThumbStart : g.ThumbLength * 0.5f;
        scrollDragId = n.StableId;
        focus.ScrollbarActive = n.StableId;
        if (!onThumb) DragScrollbar(mx, my);
        dirty = true;
        return true;
    }

    private void DragScrollbar(float mx, float my)
    {
        int index = -1;
        for (int i = 0; i < scrollableCount && index < 0; i++) if (stack.Nodes[scrollables[i]].StableId == scrollDragId) index = scrollables[i];
        for (int i = 0; i < interactiveCount && index < 0; i++) if (stack.Nodes[interactive[i]].StableId == scrollDragId) index = interactive[i];
        if (index < 0) { scrollDragId = 0; return; }
        ref NodeData n = ref stack.Nodes[index];
        if (!Scrollbar.Compute(ref n, ref stack.Styles[index], getScroll(n.StableId), out var g)) return;
        System.Numerics.Vector2 p = n.ToLocal(mx, my);
        float along = (g.Vertical ? p.Y : p.X) - scrollGrab - g.TrackStart;
        float room = g.TrackLength - g.ThumbLength;
        float scroll = room <= 0 ? 0 : Math.Clamp(along / room, 0, 1) * n.ScrollMax;
        scrollOffsets[n.StableId] = scroll;
        dirty = true;
    }

    // ------------------------------------------------------------------ tooltips

    private void UpdateTooltip(float mx, float my)
    {
        int target = TopmostInteractive(mx, my, NodeFlags.Tooltip);
        int id = target >= 0 ? stack.Nodes[target].StableId : 0;
        if (id == tooltipId) return;
        HideTooltip(suppress: false);
        tooltipId = id;
        tooltipSuppressed = false;
        tooltipSince = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    }

    private void TickTooltip(float mx, float my)
    {
        if (tooltipId == 0 || tooltipSuppressed || focus.TooltipText != null) return;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now - tooltipSince < TooltipDelay) return;
        int index = FindByStableId(tooltipId);
        if (index < 0 || stack.Nodes[index].Tooltip == null) return;
        focus.TooltipText = stack.Nodes[index].Tooltip;
        focus.TooltipX = mx;
        focus.TooltipY = my;
        dirty = true;
    }

    private void HideTooltip(bool suppress)
    {
        if (suppress) tooltipSuppressed = true; // a click hides it until the pointer leaves the element
        if (focus.TooltipText == null) return;
        focus.TooltipText = null;
        dirty = true;
    }

    internal void RegisterShortcut(ShortcutKey key, EventHandle run)
    {
        foreach (var s in shortcuts) if (s.key == key) return; // the first registration wins
        shortcuts.Add((key, run));
    }

    private static readonly KeyCode[] textEditingKeys =
        { KeyCode.A, KeyCode.C, KeyCode.V, KeyCode.X, KeyCode.Left, KeyCode.Right, KeyCode.Up, KeyCode.Down,
          KeyCode.Home, KeyCode.End, KeyCode.Backspace, KeyCode.Delete, KeyCode.Return };

    /// <summary>Run the shortcut matching this key and modifiers. True if one fired.</summary>
    private bool TryShortcut(KeyCode key, bool ctrl, bool shift, bool alt)
    {
        if (blockerOrder >= 0) return false; // a modal is open
        if (focusedBuffer != null && (!(ctrl || alt) || Array.IndexOf(textEditingKeys, key) >= 0)) return false; // typing
        foreach (var (k, run) in shortcuts)
        {
            if (k.Key != key || k.Ctrl != ctrl || k.Shift != shift || k.Alt != alt) continue;
            if (queueCount == queue.Length) Array.Resize(ref queue, queueCount * 2);
            queue[queueCount++] = (run, default);
            return true;
        }
        return false;
    }

    /// <summary>Queue an effect to run after this build (see <see cref="UIStack.Effect{TDeps}"/>).</summary>
    internal void QueueEffect(EventHandle run) => pendingEffects.Add(run);

    /// <summary>Sizes of the lists a build appends to (a Suspense boundary cuts them back).</summary>
    internal (int effects, int shortcuts, int deferred, int refs) BuildListCounts() =>
        (pendingEffects.Count, shortcuts.Count, deferred.Count, refs.Count);

    internal void RollbackBuildLists((int effects, int shortcuts, int deferred, int refs) c)
    {
        pendingEffects.RemoveRange(c.effects, pendingEffects.Count - c.effects);
        shortcuts.RemoveRange(c.shortcuts, shortcuts.Count - c.shortcuts);
        deferred.RemoveRange(c.deferred, deferred.Count - c.deferred);
        refs.RemoveRange(c.refs, refs.Count - c.refs);
    }

    // ------------------------------------------------------------------ element refs

    private readonly List<ElementRef> refs = new();

    internal void AttachRef(ElementRef r) => refs.Add(r);

    internal int FocusedId => focus.FocusedId;

    /// <summary>Focus a node of the current build (<see cref="ElementRef.Focus"/>): only focusable ones and text inputs.</summary>
    internal void FocusElement(int index, bool showRing)
    {
        ref NodeData n = ref stack.Nodes[index];
        if ((n.Flags & (NodeFlags.Focusable | NodeFlags.TextInput)) == 0) return;
        SetFocus(index, showRing);
        ScrollIntoView(index);
    }

    internal void ScrollElementIntoView(int index) => ScrollIntoView(index);

    /// <summary>After layout: refs learn where their elements are, then run focus/scroll requests made before.</summary>
    private void ResolveRefs()
    {
        for (int i = 0; i < refs.Count; i++)
        {
            ElementRef r = refs[i];
            // Look the node up again: a ref attached twice is resolved for its last element.
            ref NodeData n = ref stack.Nodes[r.NodeIndex];
            r.Resolved(n, (n.Flags & NodeFlags.InTree) != 0);
        }
        for (int i = 0; i < refs.Count; i++) refs[i].RunPending();
    }

    /// <summary>Font, scale and wrap width (font units) of a text area, as the renderer lays it out.</summary>
    private void TextAreaMetrics(int index, out UIFont font, out float scale, out float wrap)
    {
        ref NodeData n = ref stack.Nodes[index];
        ref Style s = ref stack.Styles[index];
        font = Fonts[s.FontId];
        scale = s.TextSize > 0 ? s.TextSize / font.Size : 1f;
        float cw = MathF.Max(0, n.W - s.PaddingEdges.Horizontal - s.BorderWidth * 2);
        wrap = MathF.Max(1, cw - 2) / scale;
    }

    private void MoveVertical(TextBuffer buffer, int direction, bool extend)
    {
        TextAreaMetrics(focusedIndex, out UIFont font, out _, out float wrap);
        TextAreaLayout.Locate(font, buffer.Text, wrap, buffer.Caret, out int line, out float x, out _, out _);
        int lines = TextAreaLayout.LineCount(font, buffer.Text, wrap);
        int target = line + direction;
        if (target < 0) { buffer.SetCaret(0, extend); return; }
        if (target >= lines) { buffer.SetCaret(buffer.Length, extend); return; }
        buffer.SetCaret(TextAreaLayout.IndexAt(font, buffer.Text, wrap, target, x), extend);
    }

    private void MoveInLine(TextBuffer buffer, bool home, bool extend)
    {
        TextAreaMetrics(focusedIndex, out UIFont font, out _, out float wrap);
        TextAreaLayout.Locate(font, buffer.Text, wrap, buffer.Caret, out _, out _, out int start, out int length);
        buffer.SetCaret(home ? start : start + length, extend);
    }

    private int CaretIndexAt(int index, float mx)
    {
        ref NodeData n = ref stack.Nodes[index];
        ref Style s = ref stack.Styles[index];
        if (n.Multiline)
        {
            TextAreaMetrics(index, out UIFont areaFont, out float areaScale, out float wrap);
            System.Numerics.Vector2 p = n.ToLocal(mx, lastMouseY);
            float top = n.Y + s.PaddingEdges.Top + s.BorderWidth;
            float left = n.X + s.PaddingEdges.Left + s.BorderWidth;
            float lineH = areaFont.LineHeight * areaScale;
            int line = (int)MathF.Floor((p.Y - top + getScroll(n.StableId)) / lineH);
            if (line < 0) return 0;
            if (line >= TextAreaLayout.LineCount(areaFont, n.Buffer.Text, wrap)) return n.Buffer.Length;
            return TextAreaLayout.IndexAt(areaFont, n.Buffer.Text, wrap, line, (p.X - left) / areaScale);
        }
        UIFont font = Fonts[s.FontId];
        float scale = s.TextSize > 0 ? s.TextSize / font.Size : 1f;
        float contentX = n.X + s.PaddingEdges.Left + s.BorderWidth;
        float scroll = focus.FocusedId == n.StableId ? focus.ScrollX : 0;
        float x = n.ToLocal(mx, lastMouseY).X; // undo transforms
        return font.IndexAt(n.Buffer.Text, (x - contentX + scroll) / scale);
    }

    private void ShowCaret()
    {
        focus.CaretVisible = true;
        blinkTimer = 0;
        dirty = true;
    }

    /// <summary>
    /// Process keyboard input for the focused text input (typing, editing keys, clipboard, caret
    /// blink). <see cref="Update"/> calls this; call it yourself if you drive the pointer manually.
    /// </summary>
    public void ProcessKeyboard() => ProcessKeyboard(Input.Keyboard, KeyboardDelta());

    private long lastKeyboardTicks;

    /// <summary>
    /// Real time since the last call, for the caret blink and key repeat. Not the game's delta:
    /// an app that sleeps while idle gets a clamped delta after waking, which would make the blink
    /// wait for several wake-ups instead of one.
    /// </summary>
    private float KeyboardDelta()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = lastKeyboardTicks == 0 ? 0 : (float)((now - lastKeyboardTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
        lastKeyboardTicks = now;
        return Math.Min(dt, 1f);
    }

    private void ProcessKeyboard(Keyboard kb, float dt)
    {
        EnsureBuilt();
        bool shiftDown = kb.IsDown(KeyCode.LeftShift) || kb.IsDown(KeyCode.RightShift);
        if (shortcuts.Count > 0)
        {
            bool ctrlDown = kb.IsDown(KeyCode.LeftControl) || kb.IsDown(KeyCode.RightControl);
            bool altDown = kb.IsDown(KeyCode.LeftAlt) || kb.IsDown(KeyCode.RightAlt);
            foreach (var (k, _) in shortcuts)
            {
                if (!kb.IsPressed(k.Key)) continue;
                if (TryShortcut(k.Key, ctrlDown, shiftDown, altDown)) break;
            }
        }
        ProcessNavigationKeys(kb, shiftDown);
        if (focusedBuffer == null || focusedIndex < 0)
        {
            pendingChars.Clear();
            DispatchQueued();
            return;
        }

        TextBuffer buffer = focusedBuffer;
        int version = buffer.Version, caret = buffer.Caret, anchor = buffer.Anchor;
        bool ctrl = kb.IsDown(KeyCode.LeftControl) || kb.IsDown(KeyCode.RightControl);
        bool shift = kb.IsDown(KeyCode.LeftShift) || kb.IsDown(KeyCode.RightShift);

        // Typed characters (control characters arrive as keys below).
        Span<char> one = stackalloc char[1];
        bool submit = false;
        bool multiline = stack.Nodes[focusedIndex].Multiline;
        foreach (char c in pendingChars)
        {
            if (c == '\r' || c == '\n')
            {
                // Enter (also from TypeText): submits a single-line input; a text area gets a
                // line break, and Ctrl+Enter submits.
                if (multiline && !ctrl) { one[0] = '\n'; buffer.Insert(one); }
                else submit = true;
                continue;
            }
            if (c < 32 || c == 127) continue;                       // other keys are polled below
            one[0] = c;
            buffer.Insert(one);
        }
        pendingChars.Clear();

        if (Repeat(0, kb, dt)) buffer.Backspace(ctrl);
        if (Repeat(1, kb, dt)) buffer.Delete(ctrl);
        if (Repeat(2, kb, dt)) buffer.Move(-1, ctrl, shift);
        if (Repeat(3, kb, dt)) buffer.Move(+1, ctrl, shift);
        if (multiline)
        {
            // Home/End: ends of the visual line (Ctrl: of the text). Up/Down: same x, next line.
            if (Repeat(4, kb, dt)) { if (ctrl) buffer.SetCaret(0, shift); else MoveInLine(buffer, home: true, shift); }
            if (Repeat(5, kb, dt)) { if (ctrl) buffer.SetCaret(buffer.Length, shift); else MoveInLine(buffer, home: false, shift); }
            if (Repeat(6, kb, dt)) MoveVertical(buffer, -1, shift);
            if (Repeat(7, kb, dt)) MoveVertical(buffer, +1, shift);
        }
        else
        {
            if (Repeat(4, kb, dt)) buffer.SetCaret(0, shift);
            if (Repeat(5, kb, dt)) buffer.SetCaret(buffer.Length, shift);
        }

        if (ctrl && kb.IsPressed(KeyCode.A)) buffer.SelectAll();
        if (ctrl && (kb.IsPressed(KeyCode.C) || kb.IsPressed(KeyCode.X)) && buffer.HasSelection)
        {
            SDL.SDL_SetClipboardText(buffer.Selection.ToString());
            if (kb.IsPressed(KeyCode.X)) buffer.DeleteSelection();
        }
        if (ctrl && kb.IsPressed(KeyCode.V))
        {
            string clip = SDL.SDL_GetClipboardText();
            if (!string.IsNullOrEmpty(clip)) buffer.Insert(multiline ? clip.Replace("\r", "") : clip.Replace("\r", "").Replace('\n', ' '));
        }

        ref NodeData node = ref stack.Nodes[focusedIndex];
        if (buffer.Version != version)
        {
            Enqueue(node.Change, PointerEventKind.TextChanged, ref node, lastMouseX, lastMouseY);
        }
        if (submit)
        {
            Enqueue(node.Submit, PointerEventKind.Submit, ref node, lastMouseX, lastMouseY);
            dirty = true;
        }

        if (buffer.Version != version || buffer.Caret != caret || buffer.Anchor != anchor)
        {
            ShowCaret();
        }
        else
        {
            blinkTimer += dt;
            if (blinkTimer >= BlinkInterval)
            {
                blinkTimer = 0;
                focus.CaretVisible = !focus.CaretVisible;
                dirty = true;
            }
        }

        if (kb.IsPressed(KeyCode.Escape)) Blur();
        else if (kb.IsPressed(KeyCode.Tab)) { } // handled by navigation
        DispatchQueued();
    }

    /// <summary>Key with auto-repeat: fires on press, then repeatedly while held.</summary>
    private bool Repeat(int i, Keyboard kb, float dt)
    {
        KeyCode key = repeatKeys[i];
        if (kb.IsPressed(key))
        {
            repeatTimers[i] = RepeatDelay;
            return true;
        }
        if (kb.IsDown(key))
        {
            repeatTimers[i] -= dt;
            if (repeatTimers[i] <= 0)
            {
                repeatTimers[i] += RepeatInterval;
                return true;
            }
        }
        return false;
    }
}
