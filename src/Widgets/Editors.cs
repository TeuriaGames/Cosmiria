using System.Globalization;

namespace Cosmiria;

/// <summary>
/// The state of a <see cref="UIStack.NumberInput"/>: a number edited as text, like
/// <see cref="TextBuffer"/> for a <c>TextInput</c>. Keep one in your component's state.
/// <code>
/// public NumberBuffer Speed;   // new NumberBuffer(1, step: 0.5, min: 0)
/// ui.NumberInput(Speed).OnChange(ev.Listener(static (ref Form f) => f.Apply(f.Speed.Value)));
/// </code>
/// Typing updates <see cref="Value"/> as soon as the text parses (clamped to
/// [<see cref="Min"/>, <see cref="Max"/>]); the step buttons add or subtract <see cref="Step"/>.
/// Setting <see cref="Value"/> from code rewrites the text, except while the field has focus.
/// </summary>
public sealed class NumberBuffer
{
    public readonly TextBuffer Text = new();
    internal readonly ElementRef Input = new();

    /// <summary>What the -/+ buttons add; 0 hides them.</summary>
    public double Step;
    public double Min;
    public double Max;
    /// <summary>Round to whole numbers.</summary>
    public bool Integer;

    private double value;
    private int parsedVersion = -1;

    public NumberBuffer(double value = 0, double step = 1, double min = double.MinValue, double max = double.MaxValue, bool integer = false)
    {
        Step = step;
        Min = min;
        Max = max;
        Integer = integer;
        this.value = Clamp(value);
        WriteText();
    }

    public double Value
    {
        get
        {
            Parse();
            return value;
        }
        set
        {
            Parse();
            this.value = Clamp(value);
            if (!Input.HasFocus) WriteText();
        }
    }

    public int IntValue => (int)Value;
    public float FloatValue => (float)Value;

    internal void Nudge(int direction)
    {
        Parse();
        value = Clamp(value + direction * Step);
        WriteText();
    }

    private void Parse()
    {
        if (Text.Version == parsedVersion) return;
        parsedVersion = Text.Version;
        if (double.TryParse(Text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            value = Clamp(parsed);
    }

    private double Clamp(double v)
    {
        v = Math.Clamp(v, Min, Max);
        return Integer ? Math.Round(v) : v;
    }

    private void WriteText()
    {
        if (Integer) Text.Set(((long)value).ToString(CultureInfo.InvariantCulture));
        else Text.Set(value.ToString("0.###", CultureInfo.InvariantCulture));
        parsedVersion = Text.Version;
    }
}

/// <summary>Builder returned by <see cref="UIStack.NumberInput"/>.</summary>
public readonly struct NumberInput
{
    public readonly Node Root;
    public readonly Node Field;
    public readonly Node Decrement;
    public readonly Node Increment;

    internal NumberInput(Node root, Node field, Node decrement, Node increment)
    {
        Root = root;
        Field = field;
        Decrement = decrement;
        Increment = increment;
    }

    /// <summary>Run <paramref name="listener"/> after every edit: typing or a step button.</summary>
    public NumberInput OnChange(EventHandle listener)
    {
        Field.OnChange(listener);
        Decrement.OnClick(listener);
        Increment.OnClick(listener);
        return this;
    }

    public static implicit operator Node(NumberInput input) => input.Root;
}

/// <summary>Builder returned by <see cref="UIStack.Collapsible"/>.</summary>
public readonly struct Collapsible
{
    public readonly Node Root;
    public readonly Node Header;
    /// <summary>The content; only exists while open (build it only when <see cref="IsOpen"/>).</summary>
    public readonly Node Body;
    public readonly bool IsOpen;

    internal Collapsible(Node root, Node header, Node body, bool open)
    {
        Root = root;
        Header = header;
        Body = body;
        IsOpen = open;
    }

    public static implicit operator Node(Collapsible collapsible) => collapsible.Root;
}

public sealed partial class UIStack
{
    /// <summary>A number field with -/+ step buttons editing <paramref name="number"/> in place.</summary>
    public NumberInput NumberInput(NumberBuffer number, in Style style = default)
    {
        Theme t = context.Theme;
        Node root = View(t.NumberInput).Styled(style);
        Node field = TextInput(number.Text, null, t.TextInput.MergedWith(t.NumberField)).Ref(number.Input);
        root.Child(field);
        if (number.Step <= 0) return new NumberInput(root, field, Node.None, Node.None);

        Node decrement = Button("-", t.NumberStep).OwnClick(WidgetListener(Nudge, number, -1));
        Node increment = Button("+", t.NumberStep).OwnClick(WidgetListener(Nudge, number, 1));
        root.Child(decrement).Child(increment);
        return new NumberInput(root, field, decrement, increment);
    }

    private static readonly WidgetAction<int> Nudge = static (object box, int direction, in PointerEvent e) =>
        ((NumberBuffer)box).Nudge(direction);

    /// <summary>A horizontal rule, with <paramref name="label"/> at its start when given (ImGui's SeparatorText).</summary>
    public Node Separator(string label = null)
    {
        Theme t = context.Theme;
        Node root = View(t.Separator);
        if (label != null) root.Child(Text(label).Styled(t.SeparatorLabel));
        return root.Child(View(t.SeparatorLine));
    }

    /// <summary>A clickable list row, highlighted while <paramref name="selected"/>. Attach <c>.OnClick</c>; put content in it or pass a label.</summary>
    public Node Selectable(bool selected, string label = null, in Style style = default)
    {
        Theme t = context.Theme;
        Node row = View(t.Selectable).Styled(style).Hover(t.SelectableHover).Focusable();
        if (selected) row.Styled(t.SelectableSelected);
        if (label != null) row.Child(Text(label));
        return row;
    }

    /// <summary>A row with a fixed-width <paramref name="label"/> before <paramref name="control"/> (a form field).</summary>
    public Node Labeled(string label, Node control) =>
        View(context.Theme.Labeled).Child([Text(label).Styled(context.Theme.Label), control]);

    /// <summary>
    /// A drop-down of <paramref name="options"/> that stores the picked index in <paramref name="selected"/>
    /// and opens and closes itself.
    /// </summary>
    public ComboBox ComboBox(BindableValue<int> selected, string[] options, in Style style = default,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        int index = selected.Value;
        string value = (uint)index < (uint)options.Length ? options[index] : "";
        ComboBox combo = ComboBox(value, OwnKeyed(HashCode.Combine(file, line), selected, false), style);
        if (combo.IsOpen)
        {
            for (int i = 0; i < options.Length; i++)
                combo.Option(selected, i).Child(Text(options[i]));
        }
        return combo;
    }

    /// <summary>A header that shows or hides its <see cref="Collapsible.Body"/> when clicked (ImGui's CollapsingHeader).</summary>
    public Collapsible Collapsible(string title, BindableValue<bool> open, in Style style = default)
    {
        Theme t = context.Theme;
        Node root = View(t.Collapsible).Styled(style);
        Node header = View(t.CollapsibleHeader).Hover(t.CollapsibleHeaderHover).Focusable()
            .OwnClick(WidgetListener(ToggleOpen, open, 0))
            .Child([Icon(open.Value ? Icons.ChevronDown : Icons.ChevronRight, 14), Text(title)]);
        root.Child(header);
        if (!open.Value) return new Collapsible(root, header, Node.None, false);

        Node body = View(t.CollapsibleBody);
        root.Child(body);
        return new Collapsible(root, header, body, true);
    }

    private static readonly WidgetAction<int> ToggleOpen = static (object box, int _, in PointerEvent e) =>
        ((BindableValue<bool>)box).Value = !((BindableValue<bool>)box).Value;
}
