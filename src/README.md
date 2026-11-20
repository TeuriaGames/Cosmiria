# Cosmiria

A GPUI-style UI framework for [Riateu](../Riateu). Components are structs owned by the framework, `Render` builds and returns an element tree, and interactions are typed listeners that mutate the component's state.

```csharp
public struct Counter : IComponent
{
    public int Num;

    public Node Render(UIStack ui, EventStack ev)
    {
        return ui.View(ui.Style().FlexRow().Gap(8).Padding(8).Bg(0x2E3440FF).Rounded(6)).Child([
            ui.Text($"Count: {Num}"),                                       // written straight into the arena
            ui.Button("+").OnClick(ev.Listener(static (ref Counter c) => c.Num++)),
            Num > 10 ? ui.Text("That's a lot") : Node.None,                 // branches: None is skipped
            ui.Mount<OtherComponent>(key: 0),                               // child component with its own state
        ]);
    }
}

// GameApp
ui = new UIContext(GraphicsDevice, MainWindow, spriteFont);
ui.SetRoot(new Counter());
// Update(): ui.Update();    Render(): ui.Render(cmd, swapchain);
```

Requires .NET 11 (C# 15): the framework uses union types and closed hierarchies in its API. Text shaping uses [HarfBuzzSharp](https://www.nuget.org/packages/HarfBuzzSharp) (native HarfBuzz for Windows, macOS and Linux, pulled in by NuGet).

## Concepts
- **State**: `Mount<T>(key)` stores the struct in a per-type table keyed by (parent, type, key). Listeners receive it by `ref`, mutate it in place, and schedule a rebuild. Initial state: `Mount<T>(key, initial)`, or `Mount<T>(key, static () => Create())` when building it allocates (the factory runs only on first mount). For changes from outside the UI, mutate `ui.Root<T>()` (or other state) and call `ui.Notify()`.
- **Lifetime and keep-alive:** a component the parent doesn't render in a build is unmounted, and its state is dropped (as in React).
  - A component in a hidden tab or dock panel would lose its state on every tab switch. `Mount<T>(key, …, keepAlive: true)` keeps it while the parent component lives, even in builds where it isn't rendered.
  - A kept component's own children, own values and nested state freeze with it, and come back as they were when it's rendered again.
  - `ui.Forget<T>(key)` drops a kept component explicitly; otherwise it lives as long as its parent.
  - In CML: `<Counter keep-alive="true"/>`, or `keep-alive="true"` on `<Tabs>` or `<DockSpace>` for every component inside their tabs or panels.
  - Own values written directly in the parent's markup inside a hidden tab (not inside a child component) aren't kept, because the parent is still rendering. Put such content in a component, or use a `BindableValue`.
- **Output**: `Render` returns the component's element, and `Mount` returns it to the parent. Only what's reachable from the returned element is shown; an element created but never attached is dropped, so building a few candidates and returning one is fine.
  - `Node.None` (or `default`) is "nothing": `Child` skips it and modifiers on it do nothing, so `cond ? ui.Text("…") : Node.None` works inside `.Child([...])`. A component that returns it renders nothing.
  - Several top-level elements: `return ui.Fragment([header, body]);` gives none, the single element, or a column holding them. From loops and branches, fill a builder: `var f = ui.Fragment(); foreach (…) f.Child(…); return f;`.
  - Assemble trees in any order: `var list = ui.View(); foreach (var item in Items) list.Child(Row(ui, item)); return ui.View().Child([title, list]);`. Helper methods just return `Node`s.
  - Returning an element that already has a parent, or one created outside this `Render`, throws.
  - Text and elements mix: `Child` also takes `Content`, a union of `Node` and `string`, so `.Child(["Volume", slider, muted ? "(muted)" : Node.None])` works. Strings become text elements. A literal costs nothing, but `$"…"` allocates a string; use `ui.Text($"…")` for formatted text, which writes straight into the arena. Widget handles like `Modal` or `Tabs` need a `(Node)` cast in a mixed list, since C# doesn't chain their conversion to `Node` with the union conversion.
- **Rebuild only when dirty**: the tree is rebuilt after events, hover or press changes on styled elements, scrolling, resizes, or `Notify()`. Idle frames draw the UI from cached layer textures (one draw call per layer) and allocate nothing. A rebuild reuses what didn't change (see "Reusing the previous frame").
- **Styles**: `Style` is a value type. Builder methods return copies (`Flex FlexRow FlexCol Gap Padding/P Px Py Margin W H Size SizeFull Grow Shrink ItemsCenter JustifyBetween Bg Border Rounded RoundedFull TextColorOf Font TextSizeOf OverflowHidden OverflowScroll Absolute Left Top…`). Text color, font and size inherit from ancestors. `.Hover(style)` and `.Active(style)` layer on top.
- **Ids**: hover, press and scroll state follow element ids. An element's id comes from its parent's id plus its position among siblings, so conditional content only shifts its own siblings. Use `.Id(n)` on list items so that state (and the item's children) follows the item, not its position.

## Events
Listeners are bound with `ev.Listener(...)` and attached with `.OnClick`, `.OnMouseDown`, `.OnMouseUp`, `.OnHover`, `.OnMove`, `.OnDrag` or `.OnClickOutside`. The component is passed by `ref`. Per-item data goes in an argument that is stored with the listener, so each row can carry its own id:
```csharp
ui.Button("+").OnClick(ev.Listener(static (ref Counter c) => c.Num++));
ui.Button("x").OnClick(ev.Listener(static (ref TodoList t, int id) => t.Remove(id), item.Id));
row.OnClick(ev.Listener(static (ref TodoList t, (int id, bool shift) a) => t.Select(a.id, a.shift), (item.Id, false)));
row.OnHover(ev.Listener(static (ref TodoList t, in PointerEvent e, int id) => t.Hover(id, e.Hovered), item.Id));
```
- There are four listener shapes: `(ref T)`, `(ref T, TArg)`, `(ref T, in PointerEvent)` and `(ref T, in PointerEvent, TArg)`.
- `PointerEvent` has `Kind` (Click, Down, Up, HoverEnter, HoverLeave, ClickOutside, Drag, Move, …), `Position`, `LocalPosition` (relative to the element, transforms undone), `Size` (the element's) and `Hovered`.
- `OnMove` fires on every pointer move over the element, with the button up or down. Only the topmost element with a move listener under the pointer gets it, so a button over a game screen doesn't block it unless the button also listens. `OnDrag` instead follows a press until release, even outside the element. Like every listener, each event triggers a rebuild.
- **Zero allocation:** use `static` lambdas, which the compiler caches, and pass data as the argument, which is stored as `TArg` without boxing. A capturing lambda allocates a closure on every render.
- A component can only bind listeners to *itself*; `Listener<Other>` throws. For a child to tell its parent something, give it a custom event (`Callback`, below), or state the parent reads (`<Child bind:Field="ParentField"/>` in CML).
- An `EventHandle` is a position in the current build's listener list, meant to be attached to elements; don't keep one in state or call it later. Use a `Callback` for that.

**Custom events (`Callback`)**

A component declares its events as `Callback` (no argument) or `Callback<TArg>` fields. The parent fills them with handlers that run on the *parent's* state, and the component calls `Invoke` whenever it likes: from its own listeners, an effect, a timer or game code.
```csharp
public struct Picker : IComponent
{
    public Callback<int> OnPick;   // the event, with its argument type
    public Callback OnCancel;
    // ... in a listener of Picker: self.OnPick.Invoke(index);
}

// Parent's Render: fill the child's callbacks before mounting it, in every build.
ref Picker picker = ref ui.State<Picker>(key: 0, out _);
picker.OnPick = ev.Callback(static (ref Parent p, int index) => p.Picked = index);
picker.OnCancel = ev.Callback(static (ref Parent p, int row) => p.Cancel(row), row); // a value captured now
Node node = ui.Mount<Picker>(key: 0);
```
- **Shapes:** `ev.Callback((ref T self) => …)` and `ev.Callback((ref T self, TCapture c) => …, captured)` make a `Callback`. `ev.Callback((ref T self, TArg arg) => …)` and `ev.Callback((ref T self, TArg arg, TCapture c) => …, captured)` make a `Callback<TArg>`.
- **Allocation:** as with listeners, `static` lambdas allocate nothing. A captured value of up to 8 bytes with no references (ids, enums, floats), or a reference, is stored without boxing. Larger structs are boxed.
- **Lifetime:** a callback targets its owner instance, not a build. It stays valid as long as the component that created it is mounted, and does nothing after that (`Invoke` returns false), even if the slot is reused.
- **Invoking:** the handler runs at once, on the main thread, and schedules a rebuild. Invoked during a build (from a `Render`), it runs right after that build instead, since `Render` must not change state.
- **Unset:** `Invoke` on an unset callback does nothing; `IsSet` tells whether a parent assigned one.
- **Drags:** `OnDrag` gets `Kind == Drag` on the press and each move, then `DragEnd` on release.
- **Box shadow:** `.Shadow(x, y, blur, spread, color)`, like CSS `box-shadow`, or the Tailwind presets `ShadowSm/Shadow/ShadowMd/ShadowLg/ShadowXl/Shadow2Xl/ShadowNone`. `ShadowColorOf(color)` changes only the color. It's drawn under the element with a Gaussian blur, follows its rounded corners and transform, and fades with its opacity. Like CSS, it's only visible outside the element's box, so it doesn't show through a translucent element. In CML: `shadow`, `shadow-sm|md|lg|xl|2xl|none` and `shadow-COLOR`, e.g. `shadow-lg shadow-black/50`.
- **Pointer pass-through:** `.IgnorePointer()` makes an element and its subtree invisible to the pointer (CSS `pointer-events: none`): no hover or clicks, and it doesn't cover what's under it. A drag that started inside keeps going, so a dragged element can let the drop targets under it react.
- `PointerEvent.ParentPosition`/`ParentSize` give the pointer relative to the parent's content box, which is what a divider needs to size its pane.
- **Cursors:** `.Cursor(CursorShape.ResizeX)` sets the mouse cursor over an element, or while dragging it. Text inputs show an I-beam, and split dividers show resize arrows.
- Hit testing picks the topmost element, so a button inside a clickable row gets the click. Hover is recomputed after every rebuild, so elements that move under a still pointer get enter and leave events.


## Async listeners, callbacks and Suspense
Listeners get the component's state by `ref`, which can't live across an `await`. Async ones get a `ComponentHandle<T>` instead (GPUI's `cx.spawn` with a weak entity): read and write the state through it before and after awaiting.
```csharp
ui.Button("Fetch").OnClick(ev.AsyncListener(static async (ComponentHandle<Quotes> self) =>
{
    self.Update(static (ref Quotes q) => q.Loading = true);
    string text = await Api.FetchQuote(self.Unmounted);   // the token is cancelled if the component unmounts
    self.Update(static (ref Quotes q, string t) => { q.Text = t; q.Loading = false; }, text);
}));
```
- **The UI thread:** the UI installs a `SynchronizationContext`, so code after `await` runs on the UI thread, at the start of the next `Update` or `Render`. It can touch state right away. `ui.Post(action)` queues work from any thread; `ui.IsUIThread` tells where you are.
- **`ComponentHandle<T>`:** `Update(change)` / `Update(change, arg)` change the state and rebuild (false once the component is gone; queued if called off the UI thread). `Read(...)` and `Snapshot()` read it, `IsMounted` checks it, `Unmounted` is a token cancelled when the component unmounts. `ev.Handle<T>()` gets one in `Render` for work started elsewhere (an effect, a timer).
- **Shapes:** `ev.AsyncListener(handler)`, with a captured argument `(handler, arg)`, with the pointer event (`PointerEvent e`, a copy), or both — like `Listener`.
- **Errors:** an exception escaping an async listener or callback goes to `ui.UnhandledException` (or the error output). Cancellation isn't reported. `ui.PendingTasks` counts the ones still running.
- **Async callbacks:** `ev.Callback(static async (ComponentHandle<Parent> self, int id) => ...)` makes a `Callback`/`Callback<T>` like the plain ones. `Invoke` starts it; `await callback.InvokeAsync(arg)` waits for the handler (a plain handler is already done), so a child can show "Saving..." until the parent's save finishes. Its result is false if there's no handler or its component is gone.
- **In CML:** an event or callback attribute naming a method that returns `Task` and takes a `ComponentHandle<Self>` first binds it as async, and the handle is passed before the attribute's arguments: `onclick="Delete(item.Id)"` calls `Task Delete(ComponentHandle<Self> self, int id)`. An instance method runs on a copy of the state as it was when the event fired (its `this`) and writes back through `self`; a static one works too.
  ```csharp
  public async Task FetchQuote(ComponentHandle<AsyncPanel> self)
  {
      self.Update(static (ref AsyncPanel p) => p.Loading = true);
      await Task.Delay(500, self.Unmounted);
      self.Update(static (ref AsyncPanel p) => { p.Quote = "..."; p.Loading = false; });
  }
  ```

### Suspense
A `Resource<T>` loads data off the UI thread. Reading it while it loads *suspends*: the nearest Suspense boundary throws away what its content built and shows its fallback, and the UI rebuilds by itself when the data arrives (resources are reactive, so this also wakes an app that renders on demand).
```xml
<Suspense style="gap-4">
    <Fallback><Text>Loading profile...</Text></Fallback>
    <Failed><Text>Failed: @error.Message</Text></Failed>      <!-- optional; `error` is the exception -->
    <Text>@Profile.Read().Name</Text>
</Suspense>
<Suspense fallback="Loading...">...</Suspense>                <!-- a text fallback -->
```
```csharp
public Resource<Profile> Profile;   // a field, set where the component is created:
//   new MyPanel { Profile = new Resource<Profile>(static ct => Api.LoadProfile(ct)) }
// or one owned by the call site:
Resource<User> user = ui.Resource(userId, static (id, ct) => Api.LoadUser(id, ct)); // owned, reloads when userId changes

ui.Suspense(static ui => ui.Mount<ProfileCard>(), static ui => ui.Text("Loading..."));
```
- **Reading:** `Read()` (or `Value`) returns the data, suspends while loading, and throws `ResourceFailedException` when loading failed (a boundary's `<Failed>` view catches it; without one it goes to the next boundary up). Loading starts on the first read, or early with `Load()`. Outside a boundary, use `TryGet(out value)` or `Status` (`Idle`, `Loading`, `Ready`, `Failed`); a suspending read there is an error that says so.
- **Reloading:** `Reload()` loads again and suspends readers; `Reload(keepValue: true)` keeps returning the current data until the new data arrives (show a "refreshing" hint with `IsLoading`). `Set(value)` replaces the data (an optimistic update). The loader's token is cancelled by a reload.
- **What a suspended boundary keeps:** components mounted in its content keep their state, and effects that became due inside it run only once the content really shows. Elements, listeners, shortcuts and refs built inside are dropped.
- **The boundary** is a column (`style` applies to it) holding the content or the fallback. Boundaries nest: a fallback can suspend too, and the next boundary up catches it.
- In C#, `ui.Suspense(content, fallback, failed)` takes static lambdas (`Suspense(arg, (ui, arg) => ..., ...)` passes an argument without a closure); mount a component in `content` to reach its state.

## Layout & rendering
- Layout is flexbox: grow and shrink (CSS-style freeze-and-redistribute), justify, align/stretch, `Gap(x, y)`, margin, padding, min/max, %, absolute insets, and overflow hidden/scroll.
- **Flex wrap**: `FlexWrap()` breaks items onto new lines. Grow, shrink and justify apply per line, lines are separated by the cross-axis gap, and stretch fills the line's thickness. A wrapping container with `OverflowScroll()` scrolls across its lines, so a row-wrap grid scrolls vertically.
- **Text wrapping**: on by default and inherited. Text breaks at spaces to fit its box, and words longer than a line break between characters. Height follows the line count, and an item never shrinks below its longest word. `NoWrap()` keeps text on one line; the default button style uses it.
- Items with an explicit `W(...)` keep that width when space runs out. Auto-sized items shrink, but never below their content size: min-content width, or measured height in a column.
- As in CSS, that automatic minimum goes away with an explicit `MinW`/`MinH` (`min-w-0`, `min-h-0`) or with `OverflowHidden`/`OverflowScroll`. That lets a scrolling list or a panel shrink to the space it's given instead of pushing its parent taller.
- **Scroll containers measure their full content,** so a content-sized parent such as a dialog fits them, and they shrink and scroll when constrained.
  - Children of scroll containers shrink too, down to their minimum, as in CSS.
  - Unlike CSS, a scroll container nested anywhere inside an element counts as 0 toward that element's automatic minimum height. Panels holding a scrolling list shrink to fit without `min-h-0` on every ancestor.
- One pipeline draws everything. Rounded rects with borders come from an SDF in the fragment shader, and clipping uses scissor rects. Edges are anti-aliased over one screen pixel at any display scale or transform. A new draw call starts only when the clip changes or a raster image is drawn. Wrapped lines outside the clip are skipped.
- **Sharp text at any size:** glyphs are rasterized from the font's outlines at the exact pixel size they're drawn at (text size × display scale × transform scale, rounded to ¼ px) and drawn 1:1, so `text-4xl`, `scale-150` or a 2× display don't blur them. Every font and size shares one glyph atlas.
- **Text shaping:** text is shaped with HarfBuzz, so it gets the font's ligatures ("ffi" as one glyph), GPOS kerning ("AV", "Wa"), contextual alternates, marks placed over their letters (an `e` followed by a combining accent draws as `é`), and the shaping complex scripts need (Arabic joining, Indic reordering), given a font that has those glyphs.
  - A paragraph is shaped once, split into runs of one script, each in its script's direction. Layout, wrapping, carets, selection and painting all use that one result, so they agree.
  - Each character gets a share of its glyph's width: a caret can stop inside a ligature ("of|fice"), but never inside a character made of two UTF-16 units or between a letter and its accent.
  - Shaped text is cached per font by its content, so a string costs one dictionary lookup per use after the first.
  - Not covered yet: reordering mixed left-to-right and right-to-left text on one line (each run keeps its direction, runs stay in text order), and fallback fonts for characters the font lacks (they draw as the font's missing-glyph box).
- **Reusing the previous frame:** a rebuild repaints the tree, but avoids work where nothing changed.
  - A text element whose shaped text, text style, box, clip, transform and display scale are the same as last frame copies its glyph quads from that frame instead of placing each glyph again.
  - Only the quads that differ from what the GPU buffer holds are uploaded: from the first changed quad to the last. A rebuild that changed nothing visible uploads nothing.
  - `ui.RenderStats` has running totals (quads painted, glyph quads reused, quads uploaded). In the sample's self-test, about 80% of painted quads are reused glyphs, and uploads send about 40% of what full uploads would.
- **Cached layers:** the UI's pixels are kept in textures and redrawn only after a rebuild. A frame without one, even while the game under or inside the UI animates, draws one quad per layer instead of every element.
  - A texture shown with `ui.Image(texture)` (a game screen, a video) changes without a rebuild, so it's drawn live every frame, between the layer of what's under it and the layer of what's over it. Stacking stays right: a menu or tooltip over the game screen is in the layer above it.
  - The sample draws in 1 call (game screen hidden) or 3 (game screen shown) instead of 22–24, with the same pixels. The layers are rendered with the same premultiplied blending as the window, so compositing them gives the result drawing the elements would.
  - Cost: one window-sized texture per layer (live textures + 1). With more than 4 live textures on screen, or with `ui.CacheLayers = false`, every frame redraws every element instead. `ui.DrawCalls` is what the last frame issued, and `ui.Batches` what drawing every element takes; `RenderStats.Composites` and `LayerRenders` count cached frames and layer redraws.
  - A frame with a rebuild costs a little more than before (the layers, then the composite), so continuous animation (transitions, a caret blinking) pays that each time it rebuilds.
- **On-demand rendering (optional):** for tools and editors, the app can sleep while nothing happens and draw only frames that changed, like a desktop app: no ticks, no draw calls, no CPU while idle. It needs Riateu's `RenderOnDemand` loop and three hooks:
  ```csharp
  RenderOnDemand = true;               // GameApp: sleep until input, a wake-up or a deadline
  ui.Invalidated += Wake;              // state changed from any thread (reactive values, async results)

  protected override TimeSpan NextUpdateIn() =>
      double.IsInfinity(ui.SecondsUntilUpdate) ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(ui.SecondsUntilUpdate);
  protected override bool ShouldRender() => ui.NeedsRender;
  ```
  - `ui.NeedsRender`: a rebuild is due, an animation runs, or a repaint hasn't been drawn yet. `ui.SecondsUntilUpdate`: the next caret blink, tooltip or key repeat (0 while animating, infinity when nothing is scheduled). `ui.Invalidated`: raised by `Notify()`, which every reactive change goes through, on the thread that made it.
  - The loop: `GameApp` waits in `SDL_WaitEventTimeout` until input, a window event, `Wake()` (any thread) or `NextUpdateIn`, then runs `Update` once with the real elapsed time (capped by `OnDemandMaxDelta`, 1 s) and `Render` if `ShouldRender()` or the window was resized or uncovered. Ticks are still at least `FixedStepTarget` apart. `MaxIdleWait` caps the sleep (half a second in debug builds with the asset server, so asset changes are picked up; infinite otherwise).
  - Anything that changes the screen without `Notify()` needs its own wake-up: a game view keeps the app drawing every frame while it's visible (the sample uses an `ElementRef` on its `<Image>` and returns zero from `NextUpdateIn`), and other threads call `Wake()`.
  - The MVU sample runs this way (`COSMIRIA_TRACE=1` logs each tick and render): idle, it doesn't tick at all; a message from another thread, an async result and each caret blink cost one tick and one render each. The main sample has it as an option, `COSMIRIA_ON_DEMAND=1`: with the game screen hidden it draws once a second (the score), with it shown every frame.

## Element refs
A handle on one element, like a React ref: read where it was laid out, focus it, or scroll it into view, from listeners, effects or game code.
```csharp
public ElementRef NameBox;                     // a field of the component (new() it once in C#)

ui.TextInput(Name).Ref(NameBox);               // attach it on every build

public void Rename()
{
    Editing = true;                            // shows the text input on the next build...
    Name.SelectAll();
    NameBox.Focus();                           // ...and focuses it right after that build
}
```
- **CML:** `ref="NameBox"` on any element. A `ref` naming a field of the component is created at the start of `Render`, so it works before its element is first shown. A path like `item.Box` is created where it's used; any other expression must give an existing `ElementRef`.
- **`Focus(showRing = false)` and `ScrollIntoView()`** run after the next build, so they see the changes made alongside them, like the element appearing or a list growing. If the element isn't in that build, they wait until it's attached again. Focus works on text inputs and focusable elements, and scrolls the element into view. A text input keeps a selection it already has (so typing replaces it); otherwise its caret goes to the end.
- **Layout:** `Bounds` is (x, y, width, height) in window pixels, after scrolling; `Position`, `Size` and `Contains(point)` read it. During a build it's the previous build's layout, and once the element is gone, its last known place.
- **State:** `IsMounted` tells whether the element is in the UI, `HasFocus` whether it has keyboard focus, and `Blur()` drops focus.
- **One element each:** attached to several elements in one build, a ref ends up on the last one. For a row in a list, give each item its own ref, or put one on a marker element, like an empty `<View ref="End"/>` after the rows to scroll to the end.

## Keyboard, scrollbars & tooltips
- **Focus:** Tab and Shift+Tab move through focusable elements in paint order. That includes buttons, checkboxes, radios, switches, toggle buttons, sliders, combo boxes, tabs, dock tabs, menu titles and items, tree rows and text inputs; `.Focusable()` (CML `focusable="true"`) adds any element.
- **Keys on a focused element:** Enter or Space clicks it, running the widget's own action and then your `OnClick`. Arrow keys nudge a slider by 5%, working with every way of binding it. Escape closes the top modal or drops focus.
- **Focus ring:** shown only when focus came from the keyboard, as in CSS `:focus-visible`. Clicking focuses without a ring. The color is `Theme.FocusRing`.
- **Scrolling into view:** focus scrolls the element into view inside scroll containers. Inside an open modal, Tab cycles only through the modal's elements.
- **Scrollbars:** every scroll container, and every text area, gets an overlay thumb along its scroll axis while its content overflows. Drag the thumb, or click the track to jump; the wheel still works. The thumb takes no layout space. `Theme.ScrollThumb`, `ScrollThumbHover` and `ScrollThumbActive` color it. The scrollbar takes the pointer before the content it scrolls, but not before an element drawn on top of it from outside, like a floating window's resize grip.
- **Tooltips:** `.Tooltip("Save the file")` (CML `tooltip="Save the file"` or `tooltip="@Expr"`) shows text after the pointer rests for `ui.TooltipDelay` (0.5 s) seconds. It's placed near the pointer and kept inside the window, wraps at `Theme.TooltipMaxWidth`, and a click hides it until the pointer leaves.
- **Context menus:** a right click raises `OnContextMenu` (CML `oncontextmenu`), and `ui.ContextMenu(...)` builds the menu (see "Overlays").
- **For tests and automation:** `ui.SendKey(KeyCode.Tab)`, `ui.ProcessSecondaryPress(x, y)` (right click), `ui.ActiveTooltip` and `ui.TryFindScrollbar(n, out thumb)` drive and inspect these features without real input.

## Overlays: modals and menus
```csharp
// A modal: dims and blocks everything behind it; close button, backdrop click and Escape set ShowAbout to false.
var about = ui.Modal(ShowAbout, "About");               // BindableValue<bool>; or Modal(bool).OnDismiss(...)
root.Child(about);                                      // attach anywhere: it's placed over the window
if (about.IsOpen) about.Body.Child(ui.Text("Cosmiria 1.0"));

// A menu bar: click a title to open it, hover to switch, click outside or an item to close.
var bar = ui.MenuBar(ui.Own(-1));                       // which menu is open: its own value, or a BindableValue<int>
var file = bar.Menu("File");
if (file.IsOpen)
{
    file.Item("Save", ev.Listener(static (ref Editor e) => e.Save()), "Ctrl+S");
    file.Separator();
    var recent = file.Submenu("Recent");                // opens on hover (one level of submenus)
    if (recent.IsOpen) recent.Item("level1.scene", ev.Listener(...));
}

// A context menu: a right click on target opens it at the pointer.
var menu = ui.ContextMenu(target, ui.Own(new Vector2(float.NaN)));
target.Child(menu.Popup);
if (menu.IsOpen) menu.Item("Delete", ev.Listener(...));
```
- **Fixed positioning:** `Fixed()` (CML `fixed`) places an element relative to the window. Modals and context menus use it, and you can too, e.g. `fixed inset-0`.
- **Content is built only while open.**
- **Modal blocking:** while a modal is open, elements painted before it get no hover, clicks, wheel or focus.
- **Shortcuts trigger their items,** even while the menu is closed. Use strings like `"Ctrl+S"`, `"Ctrl+Shift+Z"`, `"Alt+F4"`, `"F2"`, `"Del"`, `"Ins"`, `"Enter"` or `"Esc"`.
  - In C#, call `menu.Item(label, handler, shortcut)` on every render, open or not; on a closed menu it only registers the shortcut. `ui.Shortcut("Ctrl+S", listener)` registers one without a menu.
  - In CML, every `<MenuItem shortcut="…" onclick="…">` of a menu bar or context menu is registered automatically, including items in submenus, but not items inside `@if`/`@for`.
  - Modifiers must match exactly, and the first registration of a key combination wins.
  - Shortcuts are disabled while a modal is open. While a text input is focused, only Ctrl/Alt shortcuts that don't edit text fire (never Ctrl+A/C/V/X, arrows, Backspace or Delete).
  - For tests: `ui.SendKey(KeyCode.S, ctrl: true)`.

## Trees
```csharp
var tree = ui.Tree(Selected);                           // BindableValue<string>: the selected item's value
var assets = tree.Item("Assets", expanded: ui.Own(true));
if (assets.IsExpanded)
    foreach (var name in Textures) assets.Item(name, leaf: true);
```
- **Rows:** a row has a chevron (toggling its `expanded` box) and a label, indented `Theme.TreeIndent` per level.
  - Clicking a row stores its `value` (default: the label) in the selection.
  - `Row` takes your listeners (double click, `.OnContextMenu`), and `Content` takes icons.
- **Item menus:** `item.ContextMenu(ui.Own(new Vector2(float.NaN)))` gives one item its own right-click menu (attach its `Popup` to the row). A right click on the item selects it, so the highlight shows what the menu acts on, and then opens the menu. A right click elsewhere in the tree still reaches a menu on the tree or its container.
- **Expansion:** an item with no `expanded` box (or `leaf: true`) can't expand. Children are built only while expanded.

## Effects
```csharp
ui.Effect(ev.Listener(static (ref Editor e) => e.LoadLevel()), LevelIndex);   // after the build, on mount and when LevelIndex changes
ui.Effect(ev.Listener(static (ref Editor e) => e.Connect()));                 // once, on mount
```
- **What it is:** a side effect, like React's `useEffect`. It runs after a build, not during it, so it can change state freely; that change triggers another build.
- **When it runs:** after the first build in which it appears, then whenever `deps` changes (compared with `Equals`; use a tuple for several values). It's keyed like own values: call site and occurrence, or `key`.
- **No cleanup callback:** nothing runs when the component disappears.

## Transforms
Like CSS `transform`, transforms are paint-only: the element keeps its layout box, siblings don't move, and children are transformed along with it.
```csharp
ui.Style().Rotate(12)                       // degrees, clockwise
ui.Style().Scale(1.5f)                      // or Scale(x, y), ScaleXOf, ScaleYOf
ui.Style().Translate(40, Length.Percent(-50)) // pixels or % of the element's own size
ui.Style().Origin(0, 0)                     // around the top-left corner (default: center)
```
- They apply in CSS order (translate, rotate, scale) around the origin, and nested transforms compose.
- They merge one part at a time, so `hover:scale-110` on a `rotate-12` element keeps the rotation.
- **Hit testing follows the transformed shape:** clicks, hover, drags and text selection use the inverse transform, and `PointerEvent.LocalPosition` is in the element's own, untransformed space.
- **Text, icons and SVG images are re-rasterized at the transformed scale,** so scaled text stays sharp.
- **Clipping:** inside a rotated element, `overflow-hidden` clips to the bounding box of the rotated area, because scissor rects can't rotate.
- **Popups:** overlays such as dropdowns are drawn with their parent's transform.

## Transitions & opacity
CSS transitions: when a re-render or a hover/active state changes a property, it animates to the new value instead of jumping.
```csharp
ui.Style().Transition().Duration(200).Ease(Easing.Out)                      // colors, opacity, transform
ui.Style().Transition(TransitionParts.All).Duration(500).Delay(100)
ui.Style().Opacity(0.5f)                                                   // the element and its children
```
```xml
<Button style="transition duration-200 hover:scale-110 hover:bg-blue-600">Hover me</Button>
<View style="transition-all duration-500 ease-in-out @(Expanded ? Big : Small)"/>
```
- **What animates:** `Colors` covers background, border and text. `Opacity` and `Transform` (translate, scale, rotate) are their own groups. `Size` covers pixel widths and heights, border width and corner radius.
- `transition` (Tailwind's default) means colors, opacity and transform. `transition-all` adds size, which animates layout, so siblings move smoothly too.
- **Timing:** the default duration is 150 ms. Easings are `Default`/`InOut` (cubic-bezier 0.4, 0, 0.2, 1), `In`, `Out` and `Linear`.
- **Interruption:** a change mid-way animates from the value currently on screen. Colors are interpolated with premultiplied alpha, so fading to transparent doesn't darken.
- **When it applies:** an element doesn't animate in when it first appears, as in CSS. The transition used is the one on the *new* state's style.
- **Cost:** while something animates, the UI rebuilds every frame, which doesn't allocate. When the animation ends, the UI goes idle again.
- **Opacity** multiplies the alpha of everything the element draws. Overlapping children aren't composited as a group the way CSS does it.

## Gradients
```csharp
ui.Style().LinearGradient(90).From(Tailwind.Sky500).Via(Tailwind.Violet500).To(Tailwind.Pink500)
ui.Style().RadialGradient().From(Tailwind.Amber300).To(Tailwind.Rose600)
```
```xml
<View style="bg-gradient-to-r from-sky-500 via-violet-500 to-pink-500 rounded-6"/>
<View style="bg-radial from-amber-300 to-rose-600 rounded-full"/>
<Button style="bg-linear-135 from-emerald-500 to-teal-700 hover:from-emerald-400">Go</Button>
```
- **Linear:** `bg-gradient-to-{t|tr|r|br|b|bl|l|tl}` or `bg-linear-to-…` takes a direction, and `bg-linear-N` takes an angle in degrees (CSS convention: 0 = up, 90 = right). The gradient line spans the box along that direction, as in CSS.
- **Radial:** `bg-radial` is an ellipse from the center out to the corners.
- **Colors:** `from-*`, `via-*` (an optional middle stop at 50%) and `to-*` accept any color, opacity modifiers included. A missing end fades the other end to transparent, as in Tailwind: `bg-gradient-to-b from-blue-500`.
- **Rendering:** colors blend with premultiplied alpha, so fades don't go muddy. The gradient replaces the background color and is clipped by `rounded-*` and borders like a plain background.
- **Merging:** the parts merge separately, so `hover:from-…` changes only the starting color.
- **Not supported:** transitions don't animate gradients yet, and gradients don't apply to text or borders.

## Reactivity
`BindableValue<T>` is reactive. Change it from anywhere, game code included, and the UI repaints by itself, with no `ui.Notify()`. `Computed<T>` derives values from reactive ones:
```csharp
public static readonly BindableValue<int> Score = new(0);
public static readonly Computed<int> Level = new(() => Score.Value / 10);   // recomputes only when Score changed

Score.Value += 10;                        // anywhere: the UI repaints, Level follows
Score.Changed += s => Console.WriteLine(s); // plain C# event, for game code
```
```xml
<Text>Score: @Game.Score.Value · level @Game.Level.Value</Text>
```
- **Dependency tracking is automatic:** whatever `.Value` reads a `Computed` function makes are its dependencies, re-collected on every recompute. `Computed`s can depend on other `Computed`s.
- **It's lazy:** the function runs when the value is read after a change, and `Version` only increases when the result differs.
- **Repaints:** changes made while the UI builds (during `Render`) are already part of that build, so they don't trigger another one. Changes made anywhere else repaint every `UIContext` on its next frame.
- **Threads:** set values from the main thread.
- **Text:** write `@Score.Value` rather than `@Score` in text, so the number is formatted without allocating.
- **Plain fields aren't reactive.** Component fields changed by listeners already repaint; for state changed from outside, use a `BindableValue` (or call `Notify()`).

## Images
```csharp
static readonly ImageSource Photo = ImageSource.FromFile("Assets/photo.png"); // relative to the app directory
ui.Image(Photo, ImageFit.Cover, ui.Style().W(160).H(100).Rounded(10));
ui.Image(Logo, ImageFit.Contain, ui.Style().WFull());                           // an SvgIcon: vector
ui.Image(gameScreen, ImageFit.Contain).Sampling(ImageSampling.Nearest);       // a GPU Texture
```
- **Sizing:** with no width or height, an image takes its natural size (pixels, or the SVG's viewBox). With one side set, the other follows the aspect ratio, so `w-full` gives a responsive image.
- **Fit (CSS object-fit):**
  - `Fill` (the default) stretches the image to the box.
  - `Contain` letterboxes it inside the box.
  - `Cover` fills the box and crops the overflow, centered.
- `rounded-*` clips the image's corners, and `bg-*` shows behind letterbox bars.
- **Quality when resized:**
  - SVG images, like icons, are rasterized at the exact pixel size they cover, so they're sharp at any size.
  - Raster images (PNG, JPEG, BMP, TGA, …) are uploaded once with a full mipmap chain and sampled trilinearly, so they shrink smoothly without shimmer or moiré.
  - A raster image drawn *larger* than its pixel size can only be interpolated. Use an SVG for artwork that must stay sharp when enlarged, or `Nearest` sampling for pixel art.
- **ImageSource:**
  - `FromFile(path)` decodes once and is cached per path. A missing file prints a warning and draws nothing.
  - `FromPixels(colors, w, h)` takes straight-alpha pixels. `Update(...)` replaces them; call `ui.Notify()` to repaint.
  - Textures of images that haven't been drawn for 600 rebuilds are freed, and re-uploaded if they're drawn again.
- **Game screens: `ui.Image(texture)`** draws any GPU `Texture`, such as the render target your game draws into:
  - The texture is sampled when the UI renders, so it shows its current contents every frame with no rebuild. Draw the game into it before `ui.Render` in the same command buffer.
  - It needs `TextureUsageFlags.Sampler` and is treated as premultiplied alpha, which is fine for opaque screens.
  - If you recreate it (e.g. on resize), call `ui.Notify()`. A disposed texture is skipped until the next rebuild.
  - `.Sampling(ImageSampling.Nearest)` keeps pixel-art screens crisp when scaled up.
  - `ImageFit.Contain.ToPixel(e, texW, texH)` maps a pointer event on the image to texture pixels, undoing the letterboxing (assumes no padding or border).

- `ui.DumpLayout(Console.Out)` prints the tree with rects for debugging.

## Docking
A docking workspace: panels live in tab groups arranged by resizable splits. Users can:
- click tabs to switch between them;
- drag a tab out onto another group's left, right, top or bottom edge (splits that group in half) or its center (joins it as a tab);
- drag dividers to resize.

An emptied group disappears and its neighbor takes the space.
```csharp
public DockLayout Layout;   // in your component's state (a reference type: it persists)

Layout ??= new DockLayout(DockLayout.Row(0.25f,
    DockLayout.Tabs("hierarchy"),
    DockLayout.Column(0.7f, DockLayout.Tabs("scene", "game"), DockLayout.Tabs("console"))));

var dock = ui.DockSpace(Layout, style: ui.Style().Grow());
if (dock.Panel("scene", "Scene", out Node scene)) scene.Child(SceneView(ui, ev));
if (dock.Panel("console", "Console", out Node console)) console.Child(Console(ui, ev));
```
- **Saving and restoring:** `layout.Serialize()` gives a short text form, e.g. `(row 0.27 (tabs 0 "scene") (tabs 1 "console" "assets"))`, keeping fractions, tab order and active tabs.
  - `DockLayoutText.TryParse(text, out DockLayout layout)` reads it back and returns false for malformed text.
  - The sample saves its workspace to `layout.txt` on exit and restores it at startup.
- **Keeping hidden panels' state:** mount panel components with `keepAlive: true` (CML: `<DockSpace keep-alive="true">`). Otherwise a panel hidden behind another tab is unmounted, and its state reset.
- **Panels:** `Panel(id, title, out content)` returns true only for the visible tab of its group, so hidden panels cost nothing. Mount components inside panels: their state is keyed by component, so it survives being re-docked.
- **Layout API:**
  - A panel declared but missing from the layout joins the first group.
  - `Layout.Move(id, group, DockZone.Left)`, `MoveTab(id, group, index)`, `Float(id, x, y, w, h)`, `Activate`, `Add`, `Remove` and `FindGroup` change it from code.
  - Split fractions live on `DockSplit.Fraction`, so you can save and restore a workspace by walking the tree.
  - `DockNode` is a closed hierarchy: every node is a `DockGroup` or a `DockSplit`, so `node switch { DockGroup g => …, DockSplit s => … }` needs no default arm.
- **Tearing off:** drag a tab above or below its tab bar, or onto another tab bar, and it comes off into its own floating window that follows the pointer (a tab alone in a window takes its window along). From there the drag works as a window drag (see below): release on an anchor or a tab bar to dock it, or anywhere else to leave it floating.
- **Dropping:** while a tab or window is dragged, the group under the pointer shows a cross of five anchors in its middle: dock left, right, top or bottom of that group, or join it as a tab (center). Each anchor draws where the panel goes, and hovering one highlights that area of the group. Release on an anchor to dock there.
- **Tab order:** drag a tab along its own tab bar to reorder it; it moves as the pointer passes the other tabs, and stays in your hand until you release. Over another group's tab bar, the cross hides and a marker shows where the tab will be inserted; release to move it there.
- **Styling:** panel content scrolls when it overflows. `Theme.DockGroup`, `DockTabBar` and `DockContent` style the groups. `DockAnchor`/`DockAnchorHover`, `DockAnchorFrame`, `DockAnchorFill` and `DockAnchorGap` style the anchor cross, `DockDropPreview` the highlighted area, and `DockTabMarker` the insertion marker in a tab bar.
- **Floating windows:** a torn-off tab released where it can't dock (not on an anchor or a tab bar) stays floating in its window, drawn above the docked panels.
  - Drag the window's tab bar (beside its tabs) to move it, and its bottom-right grip to resize it. Clicking or dragging a window brings it to the front.
  - **Docking a whole window:** while you drag a window it turns see-through to the pointer, and the anchor crosses and tab bars under the pointer work as for a dragged tab. They're drawn above the window. Release on an anchor's side to dock the window's group there, on the center or a tab bar to merge all its tabs in (its selected tab stays selected), or anywhere else to leave it floating where you dropped it. A plain click on the bar only raises the window.
  - Its tabs work like any group's: reorder them, drag them into other groups or onto anchors, or drop other tabs into it. A window's group only takes tabs (its anchor cross has just the center); when its last tab leaves, it closes.
  - From code: `Layout.Float(id, x, y, w, h)`, `Layout.Floating` (back to front), `BringToFront`, and `Dock(window, group, zone)` or `Dock(window, group, index)` to dock a window. Each `DockFloat` has `X`, `Y`, `W`, `H` and `Group`.
  - Saved layouts include them: `(float x y w h (tabs ...))` entries follow the tree, and text saved before floating windows existed still loads.
  - `Theme.DockFloat` (including its shadow), `DockFloatGrip` and `DockFloatGripHover` style the window, and `DockFloatDragging` is added while it's dragged (70% opacity by default).
- **Closing panels:** each panel chooses: `dock.Panel("notes", "Notes", out Node notes, closable: true)`, or `<DockPanel name="notes" title="Notes" closable="true">` in CML (any bool expression). Panels are not closable by default.
  - A closable panel has a × on its tab, docked or floating.
  - A floating window gets a close button in its tab bar only when every panel in it is closable; it closes them all. A window holding a panel that must stay open has none, so its closable tabs close one by one.
  - A closed panel leaves the layout, and `DockSpace.Panel` doesn't add it back as it does for new panels.
  - `Layout.Activate(id)` or `Layout.Open(id)` reopens it in the first group. A component mounted with `keepAlive` comes back with its state.
  - `Layout.Close(id)` and `Layout.Close(window)` close from code, whether the panel is closable or not; `closable` only decides what the user can close. `IsClosable(id)` reads it. `IsClosed(id)` and `ClosedPanels` tell what's closed, and the `PanelClosed` event reports each one. Saved layouts keep the closed list: `(closed "id" ...)`.
  - `Theme.DockTabClose` and `DockTabCloseHover` style the tab's ×; `DockFloatClose` and `DockFloatCloseHover` the window's button.
- **Not supported:** floating panels in their own OS windows (they float inside the app window).

## Colors
`Tailwind` has the full Tailwind CSS palette as `0xRRGGBBAA` constants: 22 families (`Slate`, `Gray`, `Zinc`, `Neutral`, `Stone`, `Red`, `Orange`, `Amber`, `Yellow`, `Lime`, `Green`, `Emerald`, `Teal`, `Cyan`, `Sky`, `Blue`, `Indigo`, `Violet`, `Purple`, `Fuchsia`, `Pink`, `Rose`) × shades 50–950, plus `Black`, `White` and `Transparent`.
```csharp
ui.Style().Bg(Tailwind.Slate800).Border(1, Tailwind.Slate600).TextColorOf(Tailwind.Sky300);
new Color(Tailwind.Rose400);                          // as a Riateu Color
Tailwind.WithOpacity(Tailwind.Blue500, 0.4f);         // same color, 40% alpha
```
`src/Style/Tailwind.cs` is generated by `src/scripts/gen_tailwind.py`. The CML generator compiles the same file, so both always agree.

## Icons (SVG)
```csharp
static readonly SvgIcon Logo = SvgIcon.Load("Assets/logo.svg");   // or SvgIcon.Parse(svgText)
ui.Icon(Logo, 24);                                                 // 24px tall; width from the viewBox
ui.Icon(Icons.Search, 16).Styled(ui.Style().TextColorOf(Tailwind.Sky400));
```
- **Supported SVG:** paths (every command, including arcs), rect, circle, ellipse, line, polyline, polygon, nested `<g>` with transforms, fill, stroke, stroke-width, stroke-linecap, the opacities (as attributes or inline `style`), and `currentColor`.
- **Not supported:** gradients, masks, clip paths, text and `<use>`. Even-odd fills render as non-zero.
- **Color:** parts drawn with `currentColor`, and fills with no color set, take the element's text color. Explicitly colored SVGs keep their colors.
- **Rendering:** icons are rasterized on the CPU with anti-aliasing, once per pixel size (crisp at any scale), and cached in a GPU atlas. The atlas has its own sampler slot, so icons never split a draw call.
- **Built-in `Icons`:** Check, X, ChevronDown/Up/Left/Right, Plus, Minus, Search, Trash, Info, Circle, Dot, Heart, Star, Home. They are 24×24 with 2px round strokes.
- `icon.Rasterize(w, h)` returns premultiplied RGBA bytes, e.g. for a window icon.

## Widgets
Each widget returns its root node (label included), so `.OnClick(...)` and `.Styled(...)` apply to the whole control.

**Where a widget's value lives.** A checkbox, switch, toggle button, radio group, combo box, tabs, slider, split or text input can get its value in three ways:

1. **Its own value:** pass nothing and it still works. The framework keeps a value box for that element across rebuilds, and drops it when the element goes away.
   - In C#, `ui.Own(initial)` is keyed by the call site, like a React hook; calls in a loop get one box each, in order. `ui.Shared("name", initial)` is one box per name per component, for radio groups.
   - In CML, just leave the value out (`<Checkbox>Remember me</Checkbox>`); `default="true"` sets the starting value.
2. **A `BindableValue<T>`:** a small box (`Value`, `Version`) the widget reads and writes in place, like `TextBuffer` for text. Keep one in your state to read it from code or share it between widgets. No handler is needed.
3. **Controlled:** pass a plain value and a listener; the widget only renders it and reports clicks. This is GPUI's model, for when the change needs your logic.

Built-in behavior (toggle, select, drag) runs in its own slot. Adding `.OnClick`/`onclick` to a self-managing widget adds a listener: both run, the widget's action first. Changing a `BindableValue` from game code needs `ui.Notify()`, like any state change.
```csharp
public BindableValue<bool> Muted;          // create once, e.g. in the Mount factory: Muted = new(false)

ui.Checkbox(ui.Own(false), "Remember me");                     // 1. own value
ui.Checkbox(Muted, "Muted");                                   // 2. in place; Muted.Value from code
ui.Switch(Muted, "Same value, another widget");
ui.RadioButton(ui.Shared("size", 1), 2, "Large");              // own radio group named "size"
ui.Slider(Level, 0, 100);                                      // BindableValue<float>; vertical: true for upright
ui.ProgressBar(Level.Value, 0, 100);
```
Controlled versions:
```csharp
ui.Checkbox(Agree, "I agree").OnClick(ev.Listener(static (ref Form f) => f.Agree = !f.Agree));
ui.RadioButton(Size == i, Sizes[i]).OnClick(ev.Listener(static (ref Form f, int i) => f.Size = i, i));
ui.Switch(Dark, "Dark mode").OnClick(ev.Listener(static (ref Form f) => f.Dark = !f.Dark));
ui.ToggleButton(Bold, "Bold").OnClick(ev.Listener(static (ref Form f) => f.Bold = !f.Bold));

var combo = ui.ComboBox(Fruits[Fruit], FruitsOpen)
    .OnToggle(ev.Listener(static (ref Form f) => f.FruitsOpen = !f.FruitsOpen))
    .OnDismiss(ev.Listener(static (ref Form f) => f.FruitsOpen = false));
for (int i = 0; i < Fruits.Length; i++)
    combo.Option(Fruits[i], i == Fruit, ev.Listener(static (ref Form f, int i) => { f.Fruit = i; f.FruitsOpen = false; }, i));

ui.TextInput(Name, "Your name").OnSubmit(ev.Listener(static (ref Form f) => f.Greeting = f.Name.ToString()));

ui.Slider(Volume, 0, 1).OnDrag(ev.Listener(static (ref Form f, in PointerEvent e) => f.Volume = Slider.ValueAt(e, 0f, 1f, 0.05f)));

var tabs = ui.Tabs(Page);
for (int i = 0; i < Pages.Length; i++)
    tabs.Tab(i, Pages[i], ev.Listener(static (ref Form f, int i) => f.Page = i, i));
tabs.Panel.Child(Page == 0 ? General(ui, ev) : Audio(ui, ev)); // only the selected page is built
```
- **ProgressBar:** `ui.ProgressBar(value, min, max, style, vertical)` is a track filled to the value (bottom-up when vertical). It's display only; `Theme.Progress`/`ProgressFill` style it.
- **Vertical slider:** `ui.Slider(value, min, max, style, vertical: true)` puts min at the bottom. `Slider.Fraction`/`ValueAt` detect the orientation from the slider's shape (taller than wide), so the same drag handler works for both.
- **Slider:** `ui.Slider(value, min, max)` draws a track, a fill and a knob. `OnDrag` fires on the press and on every move until release, even outside the slider.
  - `Slider.ValueAt(e, min, max, step)` turns the event into a value in `[min, max]`, snapped to `step` when it's positive. Integer types always snap, to 1 by default.
  - `Slider.Fraction(e)` gives the raw 0–1 position.
  - The knob center travels over the width minus the slider's horizontal padding. Keep `Theme.Slider`'s `Px` at half its height so the ends line up.
- **Tabs:** `ui.Tabs(selected)` returns `Bar` (the headers) and `Panel` (the content). `tabs.Tab(index, label)` adds a header and returns it, so you can attach `.OnClick` or add an icon.
- **OnDrag** works on any element. `PointerEvent.LocalPosition` is relative to the element and `PointerEvent.Size` is its size, which is handy for splitters or color pickers.
- **ComboBox:** the dropdown is an overlay, so it draws above everything and isn't clipped. It scrolls past `MaxH(220)`. `OnDismiss` fires on a press outside the field and the list.
- **TextInput** edits a `TextBuffer` you keep in your state, in place, so typing allocates nothing:
  - Read the value with `Text` (a span) or `ToString()`.
  - Keys: typing, Backspace/Delete, ←/→ (Ctrl jumps by word, Shift selects), Home/End, and Ctrl+A/C/X/V.
  - Mouse: click to place the caret, drag to select.
  - Enter fires `OnSubmit`, Escape unfocuses, and `OnChange` fires after each edit.
  - The caret blinks, and the text scrolls to keep the caret visible.
  - `ui.HasTextFocus` tells you whether to suppress game hotkeys; `ui.TypeText(...)` and `ui.Blur()` help with automation.
- **NumberInput:** `ui.NumberInput(number)` edits a `NumberBuffer` you keep in your state (`new NumberBuffer(value, step, min, max, integer)`), like `TextInput` with a `TextBuffer`: a text field plus -/+ buttons that add `Step` (0 hides them). `Value` follows the text as soon as it parses, clamped to `[Min, Max]`; setting `Value` from code rewrites the text unless the field has focus. `.OnChange(listener)` fires after typing and after each step.
- **Selectable:** `ui.Selectable(selected, label)` is a clickable list row, highlighted while selected; attach `.OnClick` and put icons in it.
- **Separator:** `ui.Separator("Treasure")` is a rule with a label at its start (no label: a plain rule).
- **Labeled:** `ui.Labeled("Name", control)` puts a fixed-width label (`Theme.Label`) before a control, for forms.
- **ComboBox of options:** `ui.ComboBox(selected, options)` with a `BindableValue<int>` and a `string[]` opens and closes itself and stores the picked index.
- **Collapsible:** `ui.Collapsible("Wave 1", open)` is a header with a chevron that toggles `open` (e.g. `ui.OwnKeyed(item, false)`); build its `Body` only while `IsOpen`.
- **Disabled:** `.Disabled(condition)` dims any element (`Theme.Disabled`) and makes it ignore the pointer, so its listeners don't fire.
- **Image regions:** `source.Region(x, y, w, h)` is a part of an `ImageSource`, e.g. one sprite of an atlas, cached per rectangle.
- **Nested overlays:** a popup inside an overlay (a combo box in a modal) is painted and hit-tested after its ancestor overlay, so it stays on top of the modal's other content.
- **Theme:** everything (sizes, colors, accent) comes from `ui.Theme`. Change it before the next rebuild to restyle every widget.
- **Building blocks:** the pieces behind these widgets are available to your own components:
  - `.Overlay()` paints an element above everything else and hit-tests it first.
  - `.OnClickOutside(listener)` fires on a press outside the element.
  - Percentage insets: `.Top(Length.Full)` places an element right below its parent.
- **Split:** `ui.Split(size, vertical)` gives two panes and a draggable divider. `First` is `size` wide (pixels, or `Length.Percent(p)` for a share of the space), and `Second` takes the rest.
  - Resize from the handle: `split.Handle.OnDrag(ev.Listener(static (ref Editor e, in PointerEvent p) => e.Sidebar = Split.SizeAt(p, false, 120, 480)))`.
  - `Split.FractionAt` does the same for percentage splits.
  - Splits nest, for three or more columns.
- **TextArea:** `ui.TextArea(buffer, placeholder, rows: 5)` is a multi-line `TextInput`.
  - Text wraps to the width. Enter inserts a line break, and Ctrl+Enter fires `OnSubmit`.
  - Up/Down move by visual line, keeping the x position. Home/End go to the line's ends; with Ctrl, to the text's ends.
  - Paste keeps line breaks. Click and drag select across lines.
  - It scrolls to keep the caret visible, and the mouse wheel scrolls it too.
  - Without a height in the style it's `rows` lines tall.
- **Fonts:** any character the TTF has is drawn: the `SpriteFont`'s charset doesn't limit the UI, because glyphs are shaped and rasterized from the font file itself. The `SpriteFont`'s size only sets layout metrics: glyphs are rasterized at whatever size they're drawn.

## CML markup (source generator)
Mark a component with `[Cml("File.cml")]` (a path relative to the `.cs` file). The generator writes its `Render` method and adds `: IComponent`. Event attributes call methods on the component.

```csharp
[Cml("MyButtonComponent.cml")]
public partial struct MyButtonComponent
{
    public int Num;
    public void Increment() => Num++;
}
```
```xml
<cml>
    <using namespace="Riateu"/>
    <View style="flex flex-col gap-6">
        <Text>Clicked @Num times</Text>
        <Button onclick="Increment">Click me</Button>
        @if (Num >= 3) {
            <Button style="justify-center items-center bg-#5E81AC hover:bg-#81A1C1">
                <Text style="text-center">Hello World</Text>
            </Button>
        } else {
            <Text>Click @(3 - Num) more times</Text>
        }
        @for (int i = 0; i < Num; i++) {
            <Counter key="i" state="new Counter { Value = i }"/>
        }
    </View>
</cml>
```
Setup in the consuming project:
```xml
<ProjectReference Include="path/to/src/Generator/Cosmiria.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
<AdditionalFiles Include="**/*.cml" Exclude="bin/**;obj/**" />
```

**Elements**
- `<View>` and `<Button>` take `style`, `id` (an expression) and the event attributes `onclick`, `onmousedown`, `onmouseup` and `onhover`. Text placed directly inside them becomes a text element.
- **Event attributes** name a method of the component, `onclick="Increment"`, or call it with arguments, `onclick="Remove(item.Id)"`:
  - Arguments are evaluated at render time and stored with the listener, so every item in an `@foreach` gets its own.
  - `$e` passes the `PointerEvent` at event time: `onhover="Hover(item.Id, $e)"`.
  - Up to 7 arguments are allowed. Handlers with arguments need C# 14 (the .NET 10 default).
  - Attribute values aren't entity-decoded; to pass a string, quote with the other quote: `onclick='Say("hi")'`.
- `<Text>` takes the same attributes and may contain only text and `@expressions`.
- Any struct component can be used as a tag: `<Counter key="1" state="new Counter { Value = 10 }" keep-alive="true"/>`. `keep-alive` keeps its state in builds where it isn't rendered. It's looked up in the component's namespace and its parents, then in `<using>` namespaces, then in `Cosmiria`.
- `<using namespace="X"/>` must be a direct child of `<cml>`.
- **Split:** `<Split direction="row|column" bind:size="SidebarWidth" min="120" max="480" second-min="100">` with exactly two `<Pane style="...">` children. The first pane is sized, and the second takes the rest. Use `size="expr"` with `onresize="M($e)"` (and `Split.SizeAt(e, …)`) for the explicit form.
- **TextArea:** `<TextArea value="Notes" placeholder="..." rows="5" onchange="..." onsubmit="..."/>` (Ctrl+Enter submits).
- **Modal:** `<Modal open="ShowAbout" title="About">…</Modal>`.
  - `open` takes a `BindableValue<bool>`, or use `bind:open` with a plain `bool`.
  - `dismissable="false"` disables the backdrop click and Escape, and `ondismiss="M"` runs your own handler.
  - Its content is built only while it's open.
- **Menus:**
  - Bar: `<MenuBar>` contains `<Menu title="File">`, which contains `<MenuItem shortcut="Ctrl+S" onclick="Save">Save</MenuItem>` and `<MenuSeparator/>`. A nested `<Menu title="Recent">` is a submenu.
  - Context menu: put `<ContextMenu>` with the same children *inside* an element, and a right click on that element opens it.
- **Tree:** `<Tree selected="Selected">` contains `<TreeItem label="Assets" value="..." default="true">…children…</TreeItem>`.
  - `label` also takes `@expr`.
  - A `<TreeItem>` with no children is a leaf. One with children expands itself (`default` sets the starting state) unless you give `expanded=` or `bind:expanded` (a `BindableValue<bool>`).
  - The selection is a `BindableValue<string>`, or the tree's own when left out.
  - A `<ContextMenu>` inside a `<TreeItem>` is that item's own menu (it doesn't make the item a folder). A right click selects the item and opens it. In a `@foreach`, give the menu `key="..."` too, so an open menu stays with its item when the list changes. A `<ContextMenu>` directly in `<Tree>` belongs to the whole tree.
- **Effect:** `<Effect deps="LevelIndex" run="LoadLevel"/>` runs `LoadLevel` after the build where `LevelIndex` changed, and once when it first appears. It renders nothing.
- **Docking:** `<DockSpace layout="Layout" style="grow">` contains `<DockPanel name="scene" title="Scene">…</DockPanel>` elements, optionally inside `@if`/`@foreach`. Only visible panels are built. `closable="true"` on a panel puts a × on its tab.
- **Images:** `<Image src="Assets/photo.png" fit="cover" style="w-160 h-100 rounded-10"/>`.
  - `src` ending in `.svg` is embedded at build time like `<Icon>` and drawn as a vector. Other files are loaded at runtime relative to the app directory, so copy them to the output (`<None Include="Assets/**" CopyToOutputDirectory="PreserveNewest"/>`).
  - `source="expr"` takes an `ImageSource`, `SvgIcon` or `Texture` expression. A game screen looks like `<Image source="Game.Screen" fit="contain" sampling="nearest" onclick="Poke($e)"/>`.
  - `fit` is `fill|contain|cover` and `sampling` is `linear|nearest`. Event attributes work as on `<View>`.
- **Icons:** `<Icon src="Assets/logo.svg" size="24"/>` embeds an SVG at build time. Add `**/*.svg` to `AdditionalFiles`. The SVG is checked for well-formed XML, and editing it hot-reloads. `<Icon icon="Icons.Star"/>` takes any C# `SvgIcon` expression instead.
- **Values:** every widget below works with no value attribute, keeping its own (see "Where a widget's value lives" above).
  - `default="..."` sets that own value's starting state: `<Switch default="true">`, `<Slider default="30"/>`, `<Split default="240">`, `<TextInput default="hello"/>`.
  - A value attribute or `bind:` pointing at a `BindableValue` is edited in place with no generated handler: `<Checkbox checked="Muted">`, `<Slider bind:value="Level"/>`. The generator recognizes the field's type.
  - Radio buttons and options can share an own selection by name: `<RadioButton group="size" value="0">S</RadioButton>`, plus `default="true"` on the initially selected one. Without `default`, the group starts at `default(T)`.
  - A `<ComboBox>` without `open`/`bind:open`/`ontoggle` opens and closes itself, and `<Tabs>` without `selected`/`bind:selected` selects itself.
  - Own values are keyed by the element's position in the file and by its occurrence. Elements in a loop get one each, in order.
  - **`key="item.Id"` keys them by item instead**, so inserting or removing items doesn't shift the others' values: `<Checkbox key="item.Id">@item.Name</Checkbox>`. In C#, use `ui.OwnKeyed(item.Id, false)`.
  - Hot-reload edits above an element can reset its own value.
- **Widgets** take their content as the label:
  - `<Checkbox checked="Agree" onclick="ToggleAgree">I agree</Checkbox>`
  - `<RadioButton selected="i == Size" onclick="PickSize(i)">@Sizes[i]</RadioButton>`
  - `<Switch on="Dark" onclick="ToggleDark">Dark mode</Switch>`
  - `<ToggleButton pressed="Liked" onclick="ToggleLike"><Icon icon="Icons.Heart" size="14"/>Like</ToggleButton>`
- **ComboBox:** `<ComboBox value="Fruits[Fruit]" open="FruitsOpen" ontoggle="ToggleFruits" ondismiss="CloseFruits">` contains `<Option selected="i == Fruit" onclick="PickFruit(i)">@Fruits[i]</Option>` elements, optionally inside `@for`/`@foreach`/`@if`.
- **TextInput:** `<TextInput value="Name" placeholder="Your name" onchange="Changed" onsubmit="Submit"/>`.
- **ProgressBar:** `<ProgressBar value="Loaded" min="0" max="1" orientation="vertical"/>`. `orientation` is also accepted by `<Slider>`.
- **Slider:** `<Slider value="Brightness" min="-50" max="50" step="5" ondrag="SetBrightness($e)"/>`. `min` defaults to 0 and `max` to 100. With no `step`, a float value is continuous and an integer snaps to whole numbers. The handler converts the event with `Slider.ValueAt(e, -50, 50)`.
- **Tabs:** `<Tabs selected="Page">` contains `<Tab title="General">...</Tab>` elements, optionally inside `@if`/`@foreach`.
  - Tabs are numbered 0, 1, 2… in the order they're built, so a tab hidden by `@if` shifts the ones after it.
  - Only the selected tab's content is built.
  - `<Tab>` takes `style`, `id` and the pointer events, which apply to its header. Without `bind:selected`, switch pages with `onclick`, e.g. `<Tab title="Audio" onclick="Go(1)">`.
- **Event attributes on every element:** `onclickoutside`, `ondrag` (press, every move until release, then `DragEnd`), `onmove` (every move over the element) and `oncontextmenu` (right click).
- **Other attributes on every element:** `tooltip="text"` (or `"@Expr"`), `focusable="true"` and `ref="Field"` (an `ElementRef`, see "Element refs"). For a game screen, `<Image source="Game.Screen" fit="contain" onmove="Track($e)"/>` with `ImageFit.Contain.ToPixel(e, w, h)` gives the pointer in game pixels.

**Two-way binding (`bind:`)**

`bind:` connects a widget or child component to a field of your component, so you don't write a handler for each control. It generates the value attribute and the handler that writes the field back.

| Markup | Same as |
|---|---|
| `<Checkbox bind:checked="Agree">` | `checked="Agree"` + `onclick`: `Agree = !Agree` |
| `<Switch bind:on="Dark">` | `on="Dark"` + `onclick`: `Dark = !Dark` |
| `<ToggleButton bind:pressed="Liked">` | `pressed="Liked"` + `onclick`: `Liked = !Liked` |
| `<RadioButton bind:group="Size" value="i">` | `selected="Size == i"` + `onclick`: `Size = i` |
| `<ComboBox bind:open="FruitsOpen">` | `open="FruitsOpen"` + `ontoggle` flips it, `ondismiss` sets it to `false` |
| `<Option bind:group="Fruit" value="i">` | `selected="Fruit == i"` + `onclick`: `Fruit = i`, and closes a `bind:open` combo box |
| `<Slider bind:value="Volume" min="0" max="1" step="0.05">` | `value="Volume"` + `ondrag`: `Volume = Slider.ValueAt(...)`, typed by the field (a `float` stays `float`) |
| `<Split bind:size="Width" min="120" max="480">` | `size="Width"` + the divider's drag sets `Width = Split.SizeAt(...)` (a `float` field) |
| `<Tabs bind:selected="Page">` | `selected="Page"`, and each `<Tab>`'s click sets `Page` to its index |

```xml
@for (int i = 0; i < Sizes.Length; i++) {
    <RadioButton bind:group="Size" value="i">@Sizes[i]</RadioButton>
}
<ComboBox value="Fruits[Fruit]" bind:open="FruitsOpen">
    @for (int i = 0; i < Fruits.Length; i++) {
        <Option bind:group="Fruit" value="i">@Fruits[i]</Option>
    }
</ComboBox>
```
- The target is a path into the component's own state: `Agree`, `Settings.Dark`, `Rows[i].Done`.
  - Index expressions and `value` are evaluated at render time and stored with the listener, like handler arguments, so `bind:checked="Rows[i].Done"` works inside `@for`. Writing a field of a struct element (`.Done`) needs an array or a `ref` indexer; C# can't assign through `List<T>` that way (CS1612). `Flags[i]` on a `List<bool>` is fine.
  - Locals can't be bound, because the handler runs later on the stored state. `bind:checked="item.Done"` in a `@foreach` is an error (CML115); index the field instead.
- A binding replaces the attributes it generates: `bind:checked` with `checked` or `onclick` is an error (CML114). The same goes for `bind:value` with `ondrag` on a slider, and for `onclick` on a `<Tab>` inside `<Tabs bind:selected>`. To run your own code on change, use the explicit form.
- `value` is only valid together with `bind:group`, and `bind:group` requires it.
- `<TextInput value="Name">` needs no `bind:`: it already edits your `TextBuffer` in place.

**Binding to custom components**

`bind:Field="Path"` on a component tag syncs a field of the child's state with a value in the parent:
```xml
<Counter bind:Value="Quantity"/>
<Text>Quantity: @Quantity</Text>
```
- `Field` is a public or internal, non-readonly **field** of the child (`Counter.Value`). Properties aren't supported, since both sides are passed by `ref`.
- `Path` is anything you can pass by `ref`: a field of the parent, an array element (`Totals[i]`), or a `ref` local. Unlike widget bindings it can use locals, because the sync runs during render.
- Both sides must have the same type. A mismatch is reported on the attribute (CS0411).
- On each build, before the child renders, the parent value and the child field are compared against the value synced last time:
  - The parent changed (e.g. `Quantity *= 2` in a parent handler): it's pushed into the child.
  - The child changed (its own handler ran `Value++`): it's copied into the parent, and a rebuild is requested so parent elements rendered earlier in this pass catch up on the next frame.
  - Neither changed: nothing happens. When the child is first mounted, the parent's value wins over `state="..."`.
- Several bindings per tag are fine, and they combine with `key` and `state`: `<Channel key="i" bind:Volume="Volumes[i]" bind:Muted="Muted[i]"/>`.

Writing a component meant to be bound needs nothing special. Keep the value in a public field and change it from your handlers as usual:
```csharp
[Cml("Counter.cml")]
public partial struct Counter
{
    public int Value;                        // <Counter bind:Value="..."/>
    public void Add(int amount) => Value += amount;
}
```
From C#, the same thing is `ui.State<T>(key, out int id)` + `ui.Sync(id, index, ref parentValue, ref child.Field)` before `ui.Mount<T>(key)`.

**Custom events on components**

Every `Callback` / `Callback<T>` field of a child component is an attribute, named after the field in any case (`OnPick` is `onpick`). Its value calls one of your methods, like an event attribute. `$e` is the callback's argument, and other arguments are captured when the parent renders:
```xml
<MoodPicker onpick="MoodPicked($e)" onreset="MoodReset"/>
@for (int i = 0; i < Team.Length; i++) {
    <MoodPicker key="i + 1" onpick="TeamMood(i, $e)" onreset="TeamMood(i, -1)"/>
}
```
```csharp
[Cml("MoodPicker.cml")]
public partial struct MoodPicker
{
    public Callback<int> OnPick;
    public Callback OnReset;
    public int Selected;

    public void Pick(int index) { Selected = index; OnPick.Invoke(index); } // onclick="Pick(i)" in its markup
    public void Reset() { Selected = -1; OnReset.Invoke(); }
}
```
- The callbacks are assigned in every build, so they follow the parent's current state.
- `$e` on a plain `Callback` (no argument) is an error, and C# errors in the call (a misspelled method, wrong arguments) point into the `.cml`.
- Event and `bind:` attributes combine on the same tag.

**C# in markup**
- Text expressions: `@Name.Chain[0].Call()`, or `@(any expression)` in text.
- Control flow with markup bodies: `@if (...) { } else if (...) { } else { }`, `@foreach`, `@for` and `@while`.
- Plain C# statements: `@{ ... }`.
- `@@` and `@}` produce a literal `@` and `}`.
- Entities: `&lt; &gt; &amp; &quot; &apos; &#N; &#xH;`.
- Attribute values are raw, so `<` needs no escaping there.
- Whitespace runs in text collapse to one space, as in HTML.

**Style utilities** (numbers are pixels)
- Layout: `flex hidden flex-row flex-col flex-wrap flex-nowrap flex-1 grow[-N] shrink[-N] justify-{start|center|end|between|around|evenly} items-{start|center|end|stretch} self-*`
- Spacing: `gap-N gap-x-N gap-y-N p|px|py|pt|pb|pl|pr-N m|mx|my|mt|mb|ml|mr-N`
- Size: `w|h|size|min-w|min-h|max-w|max-h-{N|N%|full|auto}`
- Positioning and overflow: `relative absolute fixed overlay inset|top|left|right|bottom-{N|N%|full} overflow-{hidden|scroll}`
- Visuals: `bg-COLOR border[-N] border-COLOR rounded[-N|-full|-none] shadow[-sm|-md|-lg|-xl|-2xl|-none] shadow-COLOR`
- Gradients: `bg-gradient-to-{t|tr|r|br|b|bl|l|tl}` `bg-linear-{N|to-DIR}` `bg-radial` `from-COLOR` `via-COLOR` `to-COLOR`
- Text: `text-{left|center|right} text-{wrap|nowrap} text-N text-COLOR font-N`, and Tailwind's size scale `text-xs` (12) `text-sm` (14) `text-base` (16) `text-lg` (18) `text-xl` (20) `text-2xl` (24) `text-3xl` (30) `text-4xl` (36)
- Transitions: `transition` `transition-{all|colors|opacity|transform|none}` `duration-N` `delay-N` (milliseconds) `ease-{linear|in|out|in-out}`, plus `opacity-N` (0–100). They combine with `hover:`: `transition hover:bg-blue-600 hover:scale-105`.
- Transforms: `translate-x|y-{N|N%|full}` `scale-N` (percent: `scale-150` is 1.5×) `scale-x-N` `scale-y-N` `rotate-N` (degrees) `origin-{center|top|top-right|right|bottom-right|bottom|bottom-left|left|top-left}`. A leading `-` negates (`-rotate-6`, `-translate-y-4`, `-scale-x-100` mirrors), and they combine with `hover:`/`active:`: `hover:scale-105`.
- `COLOR` is `#RGB`, `#RRGGBB` or `#RRGGBBAA`; a Tailwind color like `blue-500` or `slate-950`; a bare family like `rose` (its 500 shade); or `black`, `white` or `transparent`.
- Any color takes Tailwind's opacity modifier: `bg-blue-500/50`, `text-white/80`, `border-#ff0000/25`.
- Misspelled colors get a suggestion, e.g. `bleu-500` → "Did you mean 'blue-500'?".
- **Arbitrary values**, Tailwind-style: `name-[value]` takes any value, negatives included.
  - Numbers: `translate-x-[-40]`, `-translate-y-[12]`, `m-[-4]`, `p-[10]`, `rounded-[14]`, `rotate-[-12deg]`.
  - Lengths: `w-[37.5%]`, `top-[25%]`.
  - Colors: `bg-[#1e293b]`, `from-[#0ea5e9]`, `text-[#ffffff]`, `border-[#334155]`; `text-[18]` and `border-[2]` set size and width.
  - Time: `duration-[0.35s]`, `delay-[120ms]`.
  - Fractions and factors: `opacity-[0.6]` or `opacity-[60%]`, `scale-[1.5]` or `scale-[150%]`.
  - Units go inside the brackets. A leading `-` negates where that makes sense (translate, insets, margins, rotate, scale).
- **C# values in brackets:** `name-[@Expr]` or `name-[@(expression)]`, optionally followed by a unit inside the brackets, re-evaluated on every render:
  ```xml
  <View style="w-[@(60 + Tilt)] rotate-[@Tilt] translate-x-[@(Tilt / 3)] bg-[@Accent] opacity-[@Fade] hover:scale-[@Zoom]"/>
  <View style="w-[@Progress%] -translate-y-[@Lift] duration-[@(Speed)ms]"/>
  ```
  - Any numeric type works. A length also accepts a `Length`; colors accept a `Color` or `0xRRGGBBAA`.
  - For `text-[@x]` and `border-[@x]`, the value's type decides: a number sets the size or width, a color sets the color.
  - Errors in the expression are reported at its position in the `.cml`.
  - The static utilities of the attribute stay precompiled and cached. Each `[@…]` part is a small `Style` built and merged at runtime, with no allocation, and it works under `hover:`/`active:` too.
- `hover:` and `active:` prefixes apply a utility only while the element is hovered or pressed.
- `@expr` or `@(expr)` tokens are `Style` values merged at runtime.
- Static utilities compile into cached `static readonly Style` fields, so they cost nothing per render.

**Diagnostics**
- CML0xx are syntax errors.
- CML1xx are semantic errors: unknown element, attribute or utility, a bad value, content in the wrong place, a missing or invalid SVG, conflicting attributes, or an invalid `bind:` target.
- CML2xx are `[Cml]` usage errors: file not found, the type isn't `partial`, isn't a struct, or already has a `Render`.
- C# errors inside the markup (conditions, `@expr`, handler method names and arguments, `bind:` paths, `key`, `state`, `@{}`, `using` namespaces) are reported at their exact `.cml` line and column through `#line` mapping.
- If a CML file has errors, an empty `Render` is generated so you only see those errors.
- Note: the C# compiler skips method-body errors while the project has declaration-level errors, so those surface once the declaration errors are fixed.

## Hot reload
Run your app with `dotnet watch` (a Debug build). Saving a `.cml` file re-runs the generator, the new `Render` is patched into the running process, and every `UIContext` rebuilds on the next frame.
```
dotnet watch run --project sample
```
- **What applies live:** markup, text, style utilities, `@if`/`@for` blocks, event handlers and other expressions. Edits to C# method bodies apply too.
- **State survives:** component state lives in the framework's tables, so counters, scroll positions, hover and so on carry over.
- **Errors in the running app:** if a save has CML errors, the component renders a red error box with the file, line:col and message, and `dotnet watch` prints the same error. The next valid save swaps the real UI back in, with no restart.
- **What forces a restart:** adding or removing *instance fields* on a component struct, and other changes Hot Reload doesn't support. `dotnet watch` restarts the app for those.
- **How it works:**
  - `Cosmiria.HotReload` is a `MetadataUpdateHandler`. It marks UI contexts dirty and exposes `HotReload.Updated` and `HotReload.Version`.
  - Generated styles live in a static array that is rebuilt whenever `Version` changes. Hot Reload never re-runs static initializers.
  - The generated type always declares the same members, so edits never change the type's shape.
- **Restart behavior:** when `dotnet watch` restarts the app it sends SIGTERM. The sample handles it with `PosixSignalRegistration` and calls `Quit()`, so the GPU device shuts down cleanly. Do the same in your app.

## Shaders
After editing `Shaders/*.slang`, run `python scripts/compile_shaders.py`. It needs `slangc` and writes SPIR-V and DXIL into `Shaders/Compiled`, which are embedded into the assembly.

## Sample
`dotnet run --project ../sample`. Set `COSMIRIA_SELFTEST=1` to drive scripted clicks, drags and scrolling and print allocation stats. Add `COSMIRIA_SCREENSHOT=out.png` to save the final frame, and `COSMIRIA_DUMP=1` to print the layout tree.
