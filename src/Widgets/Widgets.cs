using System.Numerics;
using Riateu.Graphics;

namespace Cosmiria;

// Widgets are "controlled", like GPUI's ui crate: they render state you pass in and report
// interaction through listeners, so the value always lives in your component.
//
//   ui.Checkbox(Agree, "I agree").OnClick(ev.Listener(static (ref Form f) => f.Agree = !f.Agree));
//
// Each widget returns its root node (label included), so listeners and .Styled(...) apply to the
// whole control. Look & feel comes from UIContext.Theme.
public sealed partial class UIStack
{
    /// <summary>A checkbox with an optional label. Attach <c>.OnClick</c> to toggle your state.</summary>
    public Node Checkbox(bool isChecked, string label = null, in Style style = default)
    {
        Theme t = context.Theme;
        Node root = ControlRow(style);
        Node box = View(t.Checkbox);
        if (isChecked)
        {
            box.Styled(t.CheckboxChecked);
            box.Child(Icon(Icons.Check, 14));
        }
        root.Child(box);
        if (label != null) root.Child(Text(label));
        return root;
    }

    /// <summary>A checkbox that toggles <paramref name="value"/> itself when clicked (own value: <c>ui.Own(false)</c>).</summary>
    public Node Checkbox(BindableValue<bool> value, string label = null, in Style style = default) =>
        Checkbox(value.Value, label, style).OwnClick(WidgetListener(Toggle, value, 0));

    /// <summary>A switch that toggles <paramref name="value"/> itself when clicked.</summary>
    public Node Switch(BindableValue<bool> value, string label = null, in Style style = default) =>
        Switch(value.Value, label, style).OwnClick(WidgetListener(Toggle, value, 0));

    /// <summary>A toggle button that toggles <paramref name="value"/> itself when clicked.</summary>
    public Node ToggleButton(BindableValue<bool> value, string label = null, in Style style = default) =>
        ToggleButton(value.Value, label, style).OwnClick(WidgetListener(Toggle, value, 0));

    /// <summary>
    /// A radio button of a group: selected while <paramref name="group"/> holds <paramref name="value"/>,
    /// and clicking stores it. Radios sharing one box form a group (<c>ui.Shared("size", 0)</c> for an own one).
    /// </summary>
    public Node RadioButton<T>(BindableValue<T> group, T value, string label = null, in Style style = default) =>
        RadioButton(EqualityComparer<T>.Default.Equals(group.Value, value), label, style)
            .OwnClick(WidgetListener(SetValue<T>, group, value));

    private static readonly WidgetAction<int> Toggle = static (object box, int _, in PointerEvent e) =>
        ((BindableValue<bool>)box).Value = !((BindableValue<bool>)box).Value;

    internal static void SetValue<T>(object box, T value, in PointerEvent e) => ((BindableValue<T>)box).Value = value;

    /// <summary>A radio button with an optional label. Use one per option with its own listener.</summary>
    public Node RadioButton(bool selected, string label = null, in Style style = default)
    {
        Theme t = context.Theme;
        Node root = ControlRow(style);
        Node ring = View(t.Radio);
        if (selected)
        {
            ring.Styled(t.RadioSelected);
            ring.Child(View(t.RadioDot));
        }
        root.Child(ring);
        if (label != null) root.Child(Text(label));
        return root;
    }

    /// <summary>An on/off switch with an optional label.</summary>
    public Node Switch(bool on, string label = null, in Style style = default)
    {
        Theme t = context.Theme;
        Node root = ControlRow(style);
        Node track = View(t.SwitchTrack);
        if (on) track.Styled(t.SwitchTrackOn);
        track.Child(View(t.SwitchKnob).Styled(default(Style).Left(on ? 18 : 2)));
        root.Child(track);
        if (label != null) root.Child(Text(label));
        return root;
    }

    /// <summary>A button that shows a pressed (latched) state.</summary>
    public Node ToggleButton(bool pressed, string label = null, in Style style = default)
    {
        Node button = Button(label, style);
        if (pressed) button.Styled(context.Theme.TogglePressed);
        return button;
    }

    /// <summary>
    /// A drop-down. You own <paramref name="open"/> and the selection; options are added with
    /// <see cref="Cosmiria.ComboBox.Option(bool)"/>, each with its own listener:
    /// <code>
    /// var combo = ui.ComboBox(Fruits[Picked], Open)
    ///     .OnToggle(ev.Listener(static (ref Form f) => f.Open = !f.Open))
    ///     .OnDismiss(ev.Listener(static (ref Form f) => f.Open = false));
    /// for (int i = 0; i &lt; Fruits.Length; i++)
    ///     combo.Option(Fruits[i], i == Picked, ev.Listener(static (ref Form f, int i) => { f.Picked = i; f.Open = false; }, i));
    /// </code>
    /// </summary>
    public ComboBox ComboBox(ReadOnlySpan<char> value, bool open, in Style style = default)
    {
        Theme t = context.Theme;
        Node root = View(t.ComboBox).Styled(style);
        Node field = View(t.ComboField).Hover(t.ComboFieldHover).Focusable();
        field.Child(Text(value).Styled(default(Style).Grow()));
        field.Child(Icon(open ? Icons.ChevronUp : Icons.ChevronDown, 16));
        root.Child(field);

        // The popup always exists (hidden when closed) so options never become orphans.
        Style popupStyle = t.ComboPopup.Absolute().Left(0).Right(0).Top(Length.Full).Overlay();
        if (!open) popupStyle = popupStyle.Hidden();
        Node popup = View(popupStyle);
        root.Child(popup);
        return new ComboBox(this, root, field, popup, open);
    }

    public ComboBox ComboBox(string value, bool open, in Style style = default) => ComboBox(value.AsSpan(), open, style);

    /// <summary>
    /// A drop-down that opens and closes itself through <paramref name="open"/> (field click toggles,
    /// a click outside or picking an option closes). Pick options with <see cref="Cosmiria.ComboBox.Option{T}"/>.
    /// </summary>
    public ComboBox ComboBox(ReadOnlySpan<char> value, BindableValue<bool> open, in Style style = default)
    {
        ComboBox combo = ComboBox(value, open.Value, style).WithOpenBox(open);
        combo.Field.OwnClick(WidgetListener(Toggle, open, 0));
        if (open.Value) combo.Root.OwnOutside(WidgetListener(Close, open, 0));
        return combo;
    }

    public ComboBox ComboBox(string value, BindableValue<bool> open, in Style style = default) => ComboBox(value.AsSpan(), open, style);

    internal static readonly WidgetAction<int> Close = static (object box, int _, in PointerEvent e) => ((BindableValue<bool>)box).Value = false;

    /// <summary>
    /// A single-line text field editing <paramref name="buffer"/> in place. Click to focus; supports
    /// typing, Backspace/Delete, arrows (Ctrl for words, Shift to select), Home/End, Ctrl+A/C/X/V,
    /// mouse selection, Enter (<see cref="Node.OnSubmit"/>) and Escape (unfocus).
    /// </summary>
    public Node TextInput(TextBuffer buffer, string placeholder = null, in Style style = default) =>
        TextInput(buffer, placeholder, context.Theme.TextInput.MergedWith(style));

    private Node TextInput(TextBuffer buffer, string placeholder, Style fullStyle)
    {
        Theme t = context.Theme;
        int start = CharCount;
        if (placeholder != null) AppendChars(placeholder);
        int index = CreateNode(NodeKind.TextInput, fullStyle);
        ref NodeData n = ref Nodes[index];
        n.Buffer = buffer;
        n.TextStart = start;
        n.TextLength = CharCount - start;
        n.Flags |= NodeFlags.TextInput | NodeFlags.Cursor;
        n.Cursor = CursorShape.Text;
        return new Node(this, index).Hover(t.TextInputHover);
    }

    /// <summary>
    /// A horizontal slider showing <paramref name="value"/> in [<paramref name="min"/>, <paramref name="max"/>].
    /// Attach <c>.OnDrag</c> and turn the pointer into a value with <see cref="Cosmiria.Slider.ValueAt{T}(in PointerEvent, T, T, T)"/>:
    /// <code>
    /// ui.Slider(Volume, 0, 100)
    ///     .OnDrag(ev.Listener(static (ref Form f, in PointerEvent e) => f.Volume = Slider.ValueAt(e, 0f, 100f)));
    /// </code>
    /// </summary>
    public Node Slider(double value, double min = 0, double max = 100, in Style style = default, bool vertical = false)
    {
        Theme t = context.Theme;
        float fraction = Fraction(value, min, max);
        Length at = Length.Percent(fraction * 100f);
        if (vertical)
        {
            // Bottom is min: the fill grows up from the bottom, the knob sits at (1 - f) from the top.
            Node column = View(t.SliderVertical).Styled(style);
            Node rail = View(t.SliderTrackVertical);
            rail.Child(View(t.SliderFillVertical).Styled(default(Style).H(at)));
            rail.Child(View(t.SliderKnobVertical).Styled(default(Style).Top(Length.Percent(100f - fraction * 100f))));
            column.Child(rail);
            return column.KeyFraction(fraction);
        }
        Node root = View(t.Slider).Styled(style);
        Node track = View(t.SliderTrack);
        track.Child(View(t.SliderFill).Styled(default(Style).W(at)));
        track.Child(View(t.SliderKnob).Styled(default(Style).Left(at)));
        root.Child(track);
        return root.KeyFraction(fraction); // focusable; arrow keys nudge it
    }

    /// <summary>A slider that sets <paramref name="value"/> itself while dragged (snapped to <paramref name="step"/> when positive; integers snap to 1).</summary>
    public Node Slider<T>(BindableValue<T> value, double min = 0, double max = 100, double step = 0, in Style style = default, bool vertical = false)
        where T : INumber<T> =>
        Slider(double.CreateSaturating(value.Value), min, max, style, vertical)
            .OwnDrag(WidgetListener(SlideTo<T>, value, (min, max, step)));

    private static void SlideTo<T>(object box, (double min, double max, double step) r, in PointerEvent e) where T : INumber<T>
    {
        var b = (BindableValue<T>)box;
        b.Value = Cosmiria.Slider.ValueAt(e, b.Value, r.min, r.max, r.step);
    }

    /// <summary>A progress bar filled to <paramref name="value"/> in [<paramref name="min"/>, <paramref name="max"/>] (bottom-up when vertical).</summary>
    public Node ProgressBar(double value, double min = 0, double max = 100, in Style style = default, bool vertical = false)
    {
        Theme t = context.Theme;
        Length at = Length.Percent(Fraction(value, min, max) * 100f);
        Node bar = View(vertical ? t.ProgressVertical : t.Progress).Styled(style);
        bar.Child(View(vertical ? t.ProgressFillVertical : t.ProgressFill).Styled(vertical ? default(Style).H(at) : default(Style).W(at)));
        return bar;
    }

    private static float Fraction(double value, double min, double max) =>
        max == min || double.IsNaN(value) ? 0f : (float)Math.Clamp((value - min) / (max - min), 0, 1);

    /// <summary>
    /// A tab bar over a content panel. You own the selected index; add headers with
    /// <see cref="Cosmiria.Tabs.Tab(int, string)"/> and put the selected tab's content in <see cref="Cosmiria.Tabs.Panel"/>:
    /// <code>
    /// var tabs = ui.Tabs(Page);
    /// for (int i = 0; i &lt; Pages.Length; i++)
    ///     tabs.Tab(i, Pages[i]).OnClick(ev.Listener(static (ref Form f, int i) => f.Page = i, i));
    /// tabs.Panel.Child(Page == 0 ? General(ui) : Audio(ui));
    /// </code>
    /// </summary>
    /// <summary>Tabs that select themselves: clicking a header stores its index in <paramref name="selected"/>.</summary>
    public Tabs Tabs(BindableValue<int> selected, in Style style = default) => Tabs(selected.Value, style).WithSelectedBox(selected);

    public Tabs Tabs(int selected, in Style style = default)
    {
        Theme t = context.Theme;
        Node root = View(t.Tabs).Styled(style);
        Node bar = View(t.TabBar);
        Node panel = View(t.TabPanel);
        root.Child(bar).Child(panel);
        return new Tabs(this, root, bar, panel, selected);
    }

    internal Node TabHeader(bool selected, string label)
    {
        Theme t = context.Theme;
        Node header = View(t.Tab).Focusable();
        if (selected) header.Styled(t.TabSelected);
        else header.Hover(t.TabHover);
        if (label != null) header.Child(Text(label));
        return header;
    }

    /// <summary>
    /// A raster image. Without a width or height in <paramref name="style"/> it takes the image's
    /// pixel size; with one of them, the other follows the aspect ratio (<c>w-full</c> works).
    /// It's mipmapped, so it stays smooth when drawn smaller than its pixel size.
    /// </summary>
    public Node Image(ImageSource source, ImageFit fit = ImageFit.Fill, in Style style = default)
    {
        int index = CreateNode(NodeKind.Image, style);
        Nodes[index].Image = source;
        Nodes[index].Fit = fit;
        return new Node(this, index);
    }

    /// <summary>
    /// A GPU texture, e.g. the render target your game draws its screen into. It's sampled when
    /// the UI is drawn, so it shows the texture's current contents every frame without rebuilding
    /// the UI: render the game into it before <c>ui.Render</c>. The texture needs
    /// <c>TextureUsageFlags.Sampler</c> and is sampled as premultiplied alpha (opaque game screens
    /// are fine). If you replace it (e.g. on resize), call <c>ui.Notify()</c>: a disposed texture
    /// is skipped until the next rebuild. Use <c>.Sampling(ImageSampling.Nearest)</c> for pixel
    /// art, and <see cref="ImageFitExtensions.ToPixel"/> to map pointer events to texture pixels.
    /// </summary>
    public Node Image(Texture texture, ImageFit fit = ImageFit.Fill, in Style style = default)
    {
        int index = CreateNode(NodeKind.Image, style);
        Nodes[index].Texture = texture;
        Nodes[index].Fit = fit;
        return new Node(this, index);
    }

    /// <summary>
    /// A vector image: the SVG is rasterized at the exact pixel size it's drawn at, so it's sharp
    /// at any size, display scale or transform. Sizing works like <see cref="Image(ImageSource, ImageFit, in Style)"/>
    /// (the natural size is the SVG's viewBox).
    /// </summary>
    public Node Image(SvgIcon icon, ImageFit fit = ImageFit.Fill, in Style style = default)
    {
        int index = CreateNode(NodeKind.Image, style);
        Nodes[index].Icon = icon;
        Nodes[index].Fit = fit;
        return new Node(this, index);
    }

    /// <summary>
    /// Two panes with a draggable divider: <see cref="Cosmiria.Split.First"/> is <paramref name="size"/>
    /// wide (or tall when <paramref name="vertical"/>), <see cref="Cosmiria.Split.Second"/> takes the rest.
    /// You own the size; resize it from the handle's drag:
    /// <code>
    /// var split = ui.Split(SidebarWidth);
    /// split.Handle.OnDrag(ev.Listener(static (ref Editor e, in PointerEvent p) => e.SidebarWidth = Split.SizeAt(p, false, 120, 480)));
    /// split.First.Child(Sidebar(ui));
    /// split.Second.Child(Content(ui));
    /// </code>
    /// <paramref name="size"/> can be a percentage (<c>Length.Percent(30)</c>); see <see cref="Cosmiria.Split.FractionAt"/>.
    /// </summary>
    /// <summary>A split whose divider resizes the first pane by setting <paramref name="size"/> (pixels) itself.</summary>
    public Split Split(BindableValue<float> size, bool vertical = false, float min = 0, float max = float.MaxValue,
        float secondMin = 0, in Style style = default)
    {
        Split split = Split(size.Value, vertical, style);
        split.Handle.OwnDrag(WidgetListener(ResizeSplit, size, (vertical, min, max, secondMin)));
        return split;
    }

    private static readonly WidgetAction<(bool vertical, float min, float max, float secondMin)> ResizeSplit =
        static (object box, (bool vertical, float min, float max, float secondMin) r, in PointerEvent e) =>
            ((BindableValue<float>)box).Value = Cosmiria.Split.SizeAt(e, r.vertical, r.min, r.max, r.secondMin);

    public Split Split(Length size, bool vertical = false, in Style style = default)
    {
        Theme t = context.Theme;
        Node root = View(vertical ? t.Split.FlexCol() : t.Split.FlexRow()).Styled(style);
        Style first, second = t.SplitPane;
        if (size.Unit == LengthUnit.Percent)
        {
            // A share of the space next to the handle: both panes grow from 0 in the ratio
            // p : (100 - p) (CSS flex: p 1 0), which works whatever the container's size comes from.
            float p = Math.Clamp(size.Value, 0, 100);
            first = (vertical ? t.SplitPane.H(0) : t.SplitPane.W(0)).Grow(p).Shrink(1);
            second = (vertical ? second.H(0) : second.W(0)).Grow(100 - p).Shrink(1);
        }
        else
        {
            first = t.SplitPane.Shrink(0);
            first = vertical ? first.H(size) : first.W(size);
            second = second.Grow().Shrink(1);
        }
        Node a = View(first);
        Style handleStyle = vertical ? t.SplitHandle.H(t.SplitHandleSize).SelfStretch() : t.SplitHandle.W(t.SplitHandleSize).SelfStretch();
        Node handle = View(handleStyle).Hover(t.SplitHandleHover).Active(t.SplitHandleHover)
            .Cursor(vertical ? CursorShape.ResizeY : CursorShape.ResizeX);
        Node b = View(second);
        root.Child(a).Child(handle).Child(b);
        return new Split(root, a, handle, b, vertical);
    }

    /// <summary>
    /// A multi-line text field editing <paramref name="buffer"/> in place. Text wraps to the width;
    /// Enter inserts a line break, Ctrl+Enter fires <see cref="Node.OnSubmit"/>. Up/Down move by
    /// visual line, Home/End go to the line's ends (Ctrl for the whole text), and it scrolls (also
    /// with the mouse wheel) to keep the caret visible. Without a height in <paramref name="style"/>
    /// it's <paramref name="rows"/> lines tall.
    /// </summary>
    public Node TextArea(TextBuffer buffer, string placeholder = null, int rows = 4, in Style style = default)
    {
        Node node = TextInput(buffer, placeholder, context.Theme.TextArea.MergedWith(style));
        ref NodeData n = ref Nodes[node.Index];
        n.Multiline = true;
        n.Rows = rows;
        return node;
    }

    /// <summary>
    /// A docking area for <paramref name="layout"/> (keep it in your component's state). Tabs can
    /// be clicked, dragged out onto another group's edges or center to re-dock them, and dividers
    /// resize the splits; all of that updates the layout. Fill the panels afterwards:
    /// <code>
    /// var dock = ui.DockSpace(Layout);
    /// if (dock.Panel("scene", "Scene", out Node scene)) scene.Child(SceneView(ui, ev));
    /// if (dock.Panel("console", "Console", out Node console)) console.Child(Console(ui, ev));
    /// </code>
    /// Use a distinct <paramref name="key"/> for each dock space in the same component.
    /// </summary>
    public DockSpace DockSpace(DockLayout layout, int key = 0, in Style style = default)
    {
        ref DockHost host = ref State<DockHost>(key, out _);
        host.Layout = layout;
        Node root = Mount<DockHost>(key);
        root.Styled(style);
        return new DockSpace(this, layout, root);
    }

    internal Theme Theme => context.Theme;
    internal void NotifyRebuild() => context.Notify();

    private Node ControlRow(in Style style)
    {
        Theme t = context.Theme;
        return View(t.ControlRow).Styled(style).Hover(t.ControlRowHover).Focusable();
    }

    internal Style ThemeOptionStyle(bool selected)
    {
        Theme t = context.Theme;
        Style s = t.ComboOption;
        if (selected) s.Merge(t.ComboOptionSelected);
        return s;
    }

    internal Style ThemeOptionHover => context.Theme.ComboOptionHover;
}

/// <summary>Builder returned by <see cref="UIStack.ComboBox(string, bool, in Style)"/>.</summary>
public readonly struct ComboBox
{
    private readonly UIStack ui;
    public readonly Node Root;
    public readonly Node Field;
    public readonly Node Popup;
    public readonly bool IsOpen;
    /// <summary>The open state box when the combo box manages it (see <see cref="UIStack.ComboBox(ReadOnlySpan{char}, BindableValue{bool}, in Style)"/>).</summary>
    public readonly BindableValue<bool> OpenBox;

    internal ComboBox(UIStack ui, Node root, Node field, Node popup, bool open, BindableValue<bool> openBox = null)
    {
        this.ui = ui;
        Root = root;
        Field = field;
        Popup = popup;
        IsOpen = open;
        OpenBox = openBox;
    }

    internal ComboBox WithOpenBox(BindableValue<bool> open) => new(ui, Root, Field, Popup, IsOpen, open);

    /// <summary>
    /// An option of a selection held in <paramref name="selection"/>: highlighted while it holds
    /// <paramref name="value"/>, clicking stores it (and closes a self-managed combo box).
    /// </summary>
    public Node Option<T>(BindableValue<T> selection, T value)
    {
        Node option = Option(EqualityComparer<T>.Default.Equals(selection.Value, value));
        option.OwnClick(ui.WidgetListener(SelectAndClose<T>, selection, (value, OpenBox)));
        return option;
    }

    private static void SelectAndClose<T>(object box, (T value, BindableValue<bool> open) a, in PointerEvent e)
    {
        ((BindableValue<T>)box).Value = a.value;
        if (a.open != null) a.open.Value = false;
    }

    /// <summary>Clicking the field: usually toggles your <c>open</c> flag.</summary>
    public ComboBox OnToggle(EventHandle listener)
    {
        Field.OnClick(listener);
        return this;
    }

    /// <summary>A press outside the combo box while it's open: usually closes it.</summary>
    public ComboBox OnDismiss(EventHandle listener)
    {
        if (IsOpen) Root.OnClickOutside(listener);
        return this;
    }

    /// <summary>Add an option row; put content in it and attach <c>.OnClick</c>.</summary>
    public Node Option(bool selected)
    {
        Node option = ui.View(ui.ThemeOptionStyle(selected)).Hover(ui.ThemeOptionHover);
        Popup.Child(option);
        // A self-managed combo box closes when any option is picked.
        if (OpenBox != null) option.OwnClick(ui.WidgetListener(UIStack.Close, OpenBox, 0));
        return option;
    }

    /// <summary>Add a text option that runs <paramref name="onSelect"/> when clicked.</summary>
    public ComboBox Option(string label, bool selected, EventHandle onSelect)
    {
        Option(selected).Child(ui.Text(label)).OnClick(onSelect);
        return this;
    }

    public static implicit operator Node(ComboBox combo) => combo.Root;
}

/// <summary>Builder returned by <see cref="UIStack.Tabs(int, in Style)"/>.</summary>
public readonly struct Tabs
{
    private readonly UIStack ui;
    public readonly Node Root;
    /// <summary>The row of tab headers.</summary>
    public readonly Node Bar;
    /// <summary>Where the selected tab's content goes.</summary>
    public readonly Node Panel;
    public readonly int Selected;
    /// <summary>The selection box when the tabs manage it (see <see cref="UIStack.Tabs(BindableValue{int}, in Style)"/>).</summary>
    public readonly BindableValue<int> SelectedBox;

    internal Tabs(UIStack ui, Node root, Node bar, Node panel, int selected, BindableValue<int> selectedBox = null)
    {
        this.ui = ui;
        Root = root;
        Bar = bar;
        Panel = panel;
        Selected = selected;
        SelectedBox = selectedBox;
    }

    internal Tabs WithSelectedBox(BindableValue<int> box) => new(ui, Root, Bar, Panel, Selected, box);

    /// <summary>Add the header for tab <paramref name="index"/>; put extra content in it and attach <c>.OnClick</c>.</summary>
    public Node Tab(int index, string label = null)
    {
        Node header = ui.TabHeader(index == Selected, label);
        Bar.Child(header);
        if (SelectedBox != null) header.OwnClick(ui.WidgetListener(UIStack.SetValue<int>, SelectedBox, index));
        return header;
    }

    /// <summary>Add a text header that runs <paramref name="onSelect"/> when clicked.</summary>
    public Tabs Tab(int index, string label, EventHandle onSelect)
    {
        Tab(index, label).OnClick(onSelect);
        return this;
    }

    public static implicit operator Node(Tabs tabs) => tabs.Root;
}

/// <summary>Helpers for <see cref="UIStack.Slider"/> drag listeners.</summary>
public static class Slider
{
    /// <summary>
    /// Pointer position along the slider in [0, 1]. A slider taller than wide is vertical (bottom = 0).
    /// The padding at the ends (half the slider's thickness) is left out, so the ends match where
    /// the knob center stops.
    /// </summary>
    public static float Fraction(in PointerEvent e)
    {
        bool vertical = e.Size.Y > e.Size.X;
        float inset = (vertical ? e.Size.X : e.Size.Y) * 0.5f;
        float length = (vertical ? e.Size.Y : e.Size.X) - inset * 2;
        if (length <= 0) return 0;
        float f = Math.Clamp(((vertical ? e.LocalPosition.Y : e.LocalPosition.X) - inset) / length, 0f, 1f);
        return vertical ? 1f - f : f;
    }

    /// <summary>
    /// The value under the pointer, in [<paramref name="min"/>, <paramref name="max"/>], snapped to
    /// <paramref name="step"/> when it's positive. Integer types always snap (step 1 by default).
    /// </summary>
    public static T ValueAt<T>(in PointerEvent e, T min, T max, T step = default) where T : INumber<T> =>
        ValueAt(e, min, double.CreateSaturating(min), double.CreateSaturating(max), double.CreateSaturating(step));

    /// <summary>
    /// <see cref="ValueAt{T}(in PointerEvent, T, T, T)"/> with the result type taken from
    /// <paramref name="current"/> only, so the range can be any numbers (CML's <c>bind:value</c>
    /// uses this: <c>min="0" max="1" step="0.05"</c> on a float field gives a float).
    /// </summary>
    public static T ValueAt<T>(in PointerEvent e, T current, double min, double max, double step) where T : INumber<T>
    {
        bool integral = T.CreateTruncating(0.5) == T.Zero;
        if (integral && step <= 0) step = 1;
        double v = min + Fraction(e) * (max - min);
        if (step > 0) v = min + Math.Round((v - min) / step) * step;
        v = Math.Clamp(v, Math.Min(min, max), Math.Max(min, max));
        return T.CreateSaturating(v);
    }
}

/// <summary>Builder returned by <see cref="UIStack.Split(Length, bool, in Style)"/>.</summary>
public readonly struct Split
{
    public readonly Node Root;
    /// <summary>The sized pane (left, or top when vertical).</summary>
    public readonly Node First;
    /// <summary>The divider: attach <c>.OnDrag</c> to resize.</summary>
    public readonly Node Handle;
    /// <summary>The pane that takes the remaining space.</summary>
    public readonly Node Second;
    public readonly bool Vertical;

    internal Split(Node root, Node first, Node handle, Node second, bool vertical)
    {
        Root = root;
        First = first;
        Handle = handle;
        Second = second;
        Vertical = vertical;
    }

    /// <summary>
    /// The first pane's new size in pixels for a drag on the handle: the divider follows the
    /// pointer, clamped to [<paramref name="min"/>, <paramref name="max"/>] and to leave
    /// <paramref name="secondMin"/> pixels for the other pane.
    /// </summary>
    public static float SizeAt(in PointerEvent e, bool vertical, float min = 0, float max = float.MaxValue, float secondMin = 0)
    {
        float along = vertical ? e.ParentPosition.Y : e.ParentPosition.X;
        float handle = vertical ? e.Size.Y : e.Size.X;
        float total = vertical ? e.ParentSize.Y : e.ParentSize.X;
        float size = along - handle * 0.5f;
        size = MathF.Min(size, MathF.Min(max, total - handle - secondMin));
        return MathF.Max(MathF.Max(0, min), size);
    }

    /// <summary>
    /// Like <see cref="SizeAt"/> but as a fraction (0..1) of the space both panes share (the
    /// container minus the handle), for percentage sizes: <c>ui.Split(Length.Percent(f * 100))</c>.
    /// </summary>
    public static float FractionAt(in PointerEvent e, bool vertical, float minFraction = 0, float maxFraction = 1)
    {
        float handle = vertical ? e.Size.Y : e.Size.X;
        float shared = (vertical ? e.ParentSize.Y : e.ParentSize.X) - handle;
        if (shared <= 0) return minFraction;
        float f = ((vertical ? e.ParentPosition.Y : e.ParentPosition.X) - handle * 0.5f) / shared;
        return Math.Clamp(f, minFraction, maxFraction);
    }

    public static implicit operator Node(Split split) => split.Root;
}
