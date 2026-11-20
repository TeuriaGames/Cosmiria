using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Cosmiria.Generator;

/// <summary>
/// Binds a parsed CML document and emits the component's Render method.
/// Every user-written C# snippet (conditions, @expressions, attribute expressions, @{ } blocks,
/// using namespaces) is emitted behind a <c>#line</c> directive at its original column, so C#
/// compiler errors inside them are reported at the exact .cml location.
/// </summary>
internal sealed class CmlEmitter
{
    private static readonly Dictionary<string, string> EventMethods = new()
    {
        ["onclick"] = "OnClick", ["onmousedown"] = "OnMouseDown", ["onmouseup"] = "OnMouseUp",
        ["onhover"] = "OnHover", ["onclickoutside"] = "OnClickOutside",
        ["onchange"] = "OnChange", ["onsubmit"] = "OnSubmit",
        ["ontoggle"] = "OnToggle", ["ondismiss"] = "OnDismiss", ["ondrag"] = "OnDrag", ["onmove"] = "OnMove",
        ["oncontextmenu"] = "OnContextMenu",
    };
    private static readonly string[] PointerEvents = { "onclick", "onmousedown", "onmouseup", "onhover", "onclickoutside", "ondrag", "onmove",
        "oncontextmenu", "tooltip", "focusable", "ref" };
    private static readonly string[] ElementAttributes = With("style", "id");
    private static readonly string[] IconAttributes = With("style", "id", "src", "icon", "size");
    private static readonly string[] CheckboxAttributes = With("style", "id", "checked", "bind:checked", "default", "key");
    private static readonly string[] RadioAttributes = With("style", "id", "selected", "bind:group", "group", "value", "default");
    private static readonly string[] SwitchAttributes = With("style", "id", "on", "bind:on", "default", "key");
    private static readonly string[] ToggleAttributes = With("style", "id", "pressed", "bind:pressed", "default", "key");
    private static readonly string[] OptionAttributes = With("style", "id", "selected", "bind:group", "group", "value", "default");
    private static readonly string[] ComboAttributes = { "style", "value", "open", "bind:open", "ontoggle", "ondismiss", "key" };
    private static readonly string[] ModalAttributes = { "style", "open", "bind:open", "title", "dismissable", "ondismiss" };
    private static readonly string[] MenuBarAttributes = { "style", "open", "key" };
    private static readonly string[] MenuAttributes = { "title" };
    private static readonly string[] MenuItemAttributes = With("style", "id", "shortcut");
    private static readonly string[] ContextMenuAttributes = { "position", "key" };
    private static readonly string[] TreeAttributes = { "style", "selected", "bind:selected", "key" };
    private static readonly string[] TreeItemAttributes = With("style", "id", "label", "value", "expanded", "bind:expanded", "default", "key");
    private static readonly string[] EffectAttributes = { "deps", "run", "key" };
    private static readonly string[] SuspenseAttributes = { "style", "fallback" };
    private static readonly string[] ProgressAttributes = { "style", "id", "value", "min", "max", "orientation" };
    private static readonly string[] ImageAttributes = With("style", "id", "src", "source", "fit", "sampling");
    private static readonly string[] SplitAttributes = { "style", "direction", "size", "bind:size", "min", "max", "second-min", "onresize", "default", "key" };
    private static readonly string[] PaneAttributes = With("style", "id");
    private static readonly string[] TextAreaAttributes = With("style", "id", "value", "placeholder", "rows", "onchange", "onsubmit", "default", "key");
    private static readonly string[] DockSpaceAttributes = { "style", "layout", "key", "keep-alive" };
    private static readonly string[] DockPanelAttributes = { "name", "title", "closable" };
    private static readonly string[] SliderAttributes = With("style", "id", "value", "min", "max", "step", "bind:value", "default", "orientation", "key");
    private static readonly string[] TabsAttributes = { "style", "selected", "bind:selected", "default", "key", "keep-alive" };
    private static readonly string[] TabAttributes = With("style", "id", "title");
    private static readonly string[] TextInputAttributes = With("style", "id", "value", "placeholder", "onchange", "onsubmit", "default", "key");

    private static string[] With(params string[] extra) => extra.Concat(PointerEvents).ToArray();
    private const int MaxHandlerArgs = 7; // ValueTuple without nesting
    private static readonly string[] ComponentAttributes = { "key", "state", "keep-alive" };

    private readonly SourceText text;
    private readonly string src;
    private readonly string path;
    private readonly string linePath;
    private readonly List<Diagnostic> diagnostics;
    private readonly Compilation compilation;
    private readonly INamedTypeSymbol componentInterface;
    private readonly CmlTarget target;
    private readonly List<string> refFields = new(); // ref="Field": created at the top of Render
    private readonly IReadOnlyList<AdditionalText> svgFiles;
    private readonly List<string> iconSources = new();
    private string comboVar; // the <ComboBox> whose <Option>s are being emitted
    private BindPath comboOpen; // its bind:open path, closed when a bound <Option> is picked
    private string tabsVar;     // the <Tabs> whose <Tab>s are being emitted
    private string menuBarVar;  // the <MenuBar> whose <Menu>s are being emitted
    private string menuVar;     // the menu whose items are being emitted
    private string treeVar;     // the <Tree> or <TreeItem> whose items are being emitted
    private string dockVar;     // the <DockSpace> whose <DockPanel>s are being emitted
    private bool keepAliveDefault;  // inside <Tabs keep-alive="true"> / <DockSpace keep-alive="true"> content
    private bool tabsKeepAlive, dockKeepAlive;
    private BindPath tabsBind;  // its bind:selected path
    private INamedTypeSymbol selfSymbol;
    private bool selfResolved;

    private readonly StringBuilder usings = new();
    private readonly StringBuilder fields = new();
    private readonly StringBuilder body = new();
    private readonly List<string> usingNamespaces = new();
    private int indent = 3;
    private int nodeId, styleId, holeId;

    public CmlEmitter(SourceText text, string path, List<Diagnostic> diagnostics, Compilation compilation, CmlTarget target,
        IReadOnlyList<AdditionalText> svgFiles = null)
    {
        this.svgFiles = svgFiles ?? System.Array.Empty<AdditionalText>();
        this.text = text;
        this.path = path;
        this.diagnostics = diagnostics;
        this.compilation = compilation;
        this.target = target;
        src = text.ToString();
        linePath = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
        componentInterface = compilation.GetTypeByMetadataName("Cosmiria.IComponent");
    }

    // ================================================================== entry

    /// <summary>Emit the Render body from the document. Returns false if nothing usable was produced.</summary>
    public void EmitDocument(CmlDocument doc)
    {
        CmlElement root = doc.Root;
        if (root == null) { Line("return default;"); return; }

        foreach (var attr in root.Attributes)
            Report(CmlDiagnostics.UnknownAttribute, attr.NameSpan, attr.Name, "cml", "(none)");

        // <using> elements first: they affect component resolution everywhere in the file.
        foreach (var node in root.Children)
        {
            if (node is CmlElement { Name: "using" } u) EmitUsing(u);
        }

        EmitRadioGroupDefaults(root.Children);
        // Top-level elements (any number, from branches and loops too) become the returned node:
        // none, the single element, or a column holding them (UIStack.Fragment).
        Line($"var {RootVar} = ui.Fragment();");
        EmitContent(root.Children, parent: RootVar, isCmlRoot: true);
        Line($"return {RootVar}.Result;");
    }

    private const string RootVar = "__cmlRoot";

    /// <summary>
    /// Own radio/option groups (group="name") start at the value of their default="..." item. Set
    /// that up before anything is drawn, so items drawn earlier than the default one aren't shown
    /// selected on the first frame. Only items outside @if/@for/@{ } can be hoisted (their value
    /// can't depend on loop variables); the others are handled at runtime.
    /// </summary>
    private void EmitRadioGroupDefaults(List<CmlNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is not CmlElement e) continue; // control flow and code blocks: not hoistable
            if ((e.Name == "RadioButton" || e.Name == "Option") &&
                e.Attributes.FirstOrDefault(a => a.Name == "group") is { } group &&
                e.Attributes.FirstOrDefault(a => a.Name == "value") is { } value &&
                e.Attributes.FirstOrDefault(a => a.Name == "default") is { } dflt &&
                value.Value.Trim().Length > 0 && dflt.Value.Trim().Length > 0)
            {
                Line($"ui.RadioGroup({StringLiteral(group.Value.Trim())},");
                EmitMapped(value.ValueSpan);
                Line(",");
                EmitMapped(dflt.ValueSpan);
                Line(");");
            }
            EmitRadioGroupDefaults(e.Children);
        }
    }

    /// <param name="errors">When the body can't be generated: error lines shown in-app instead.</param>
    public string BuildSource(bool includeBody, IReadOnlyList<string> errors = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>  Generated by Cosmiria.Generator from " + System.IO.Path.GetFileName(path));
        sb.AppendLine("#nullable restore");
        sb.AppendLine("#pragma warning disable CS1591");
        sb.AppendLine("using global::Cosmiria;");
        if (includeBody) sb.Append(usings);
        sb.AppendLine("#line default");
        sb.AppendLine();

        bool hasNamespace = !string.IsNullOrEmpty(target.Namespace);
        if (hasNamespace) sb.Append("namespace ").Append(target.Namespace).AppendLine().AppendLine("{");

        sb.Append("    partial struct ").Append(target.Name).AppendLine(" : global::Cosmiria.IComponent");
        sb.AppendLine("    {");
        bool hasStyles = includeBody && styleId > 0;

        // Constant styles are built once into a static array by a generated method. Hot Reload
        // never re-runs static initializers and can't swap types, but it can patch method
        // bodies: the array is rebuilt whenever HotReload.Version changes, so edited styles
        // apply live. Cost per Render: one int compare.
        // These members are emitted for every component, even with no styles or with CML
        // errors, so the type's shape never changes between edits (adding or removing members
        // would force Hot Reload to restart the app).
        sb.AppendLine("        private static global::Cosmiria.Style[] __cmlStyles;");
        sb.AppendLine("        private static int __cmlStylesVersion;");
        sb.AppendLine();
        if (hasStyles)
        {
            sb.AppendLine("        private static global::Cosmiria.Style[] __CmlBuildStyles() => new global::Cosmiria.Style[]");
            sb.AppendLine("        {");
            sb.Append(fields);
            sb.AppendLine("        };");
        }
        else
        {
            sb.AppendLine("        private static global::Cosmiria.Style[] __CmlBuildStyles() => global::System.Array.Empty<global::Cosmiria.Style>();");
        }
        sb.AppendLine();

        // Embedded SVG icons (src="..."), same hot-reload-safe caching as styles.
        bool hasIcons = includeBody && iconSources.Count > 0;
        sb.AppendLine("        private static global::Cosmiria.SvgIcon[] __cmlIcons;");
        sb.AppendLine("        private static int __cmlIconsVersion;");
        if (hasIcons)
        {
            sb.AppendLine("        private static global::Cosmiria.SvgIcon[] __CmlBuildIcons() => new global::Cosmiria.SvgIcon[]");
            sb.AppendLine("        {");
            foreach (string svg in iconSources) sb.Append("            global::Cosmiria.SvgIcon.Parse(").Append(StringLiteral(svg)).AppendLine("),");
            sb.AppendLine("        };");
        }
        else
        {
            sb.AppendLine("        private static global::Cosmiria.SvgIcon[] __CmlBuildIcons() => global::System.Array.Empty<global::Cosmiria.SvgIcon>();");
        }
        sb.AppendLine();
        sb.AppendLine("        [global::System.CodeDom.Compiler.GeneratedCode(\"Cosmiria.Generator\", \"1.0\")]");
        sb.AppendLine("        public global::Cosmiria.Node Render(global::Cosmiria.UIStack ui, global::Cosmiria.EventStack ev)");
        sb.AppendLine("        {");
        if (includeBody)
        {
            sb.AppendLine("#line hidden");
            if (hasStyles || hasIcons) sb.AppendLine("            int __cmlVersion = global::Cosmiria.HotReload.Version + 1;");
            if (hasStyles)
            {
                sb.AppendLine("            if (__cmlStylesVersion != __cmlVersion) { __cmlStyles = __CmlBuildStyles(); __cmlStylesVersion = __cmlVersion; }");
                sb.AppendLine("            var __cmlS = __cmlStyles;");
            }
            if (hasIcons)
            {
                sb.AppendLine("            if (__cmlIconsVersion != __cmlVersion) { __cmlIcons = __CmlBuildIcons(); __cmlIconsVersion = __cmlVersion; }");
                sb.AppendLine("            var __cmlI = __cmlIcons;");
            }
            foreach (string f in refFields.Distinct())
                sb.Append("            ").Append(f).AppendLine(" ??= new global::Cosmiria.ElementRef();");
            sb.Append(body);
            sb.AppendLine("#line default");
        }
        else
        {
            EmitErrorBox(sb, errors);
        }
        sb.AppendLine("        }");
        // Listener helpers fix the component type so handler lambdas can be implicitly typed
        // (the argument type comes from the captured expression). Always emitted so the type's
        // shape is stable across hot reloads.
        string self = SelfType;
        sb.AppendLine();
        sb.Append("        private static global::Cosmiria.EventHandle __CmlOn<TArg>(global::Cosmiria.EventStack ev, TArg arg, global::Cosmiria.Listener<")
          .Append(self).AppendLine(", TArg> handler) => ev.Listener(handler, arg);");
        sb.Append("        private static global::Cosmiria.EventHandle __CmlOnE<TArg>(global::Cosmiria.EventStack ev, TArg arg, global::Cosmiria.PointerListener<")
          .Append(self).AppendLine(", TArg> handler) => ev.Listener(handler, arg);");
        // Callback props with captured arguments: the field fixes the callback type, the capture its own.
        sb.Append("        private static void __CmlCb<TCap>(global::Cosmiria.EventStack ev, ref global::Cosmiria.Callback field, TCap cap, global::Cosmiria.Listener<")
          .Append(self).AppendLine(", TCap> handler) => field = ev.Callback(handler, cap);");
        sb.Append("        private static void __CmlCbA<TArg, TCap>(global::Cosmiria.EventStack ev, ref global::Cosmiria.Callback<TArg> field, TCap cap, global::Cosmiria.CallbackHandler<")
          .Append(self).AppendLine(", TArg, TCap> handler) => field = ev.Callback(handler, cap);");
        // Async handlers: the handle type fixes the component, the argument types are inferred.
        sb.Append("        private static global::Cosmiria.EventHandle __CmlAsync<TArg>(global::Cosmiria.EventStack ev, TArg arg, global::Cosmiria.AsyncListener<")
          .Append(self).AppendLine(", TArg> handler) => ev.AsyncListener(handler, arg);");
        sb.Append("        private static global::Cosmiria.EventHandle __CmlAsyncE<TArg>(global::Cosmiria.EventStack ev, TArg arg, global::Cosmiria.AsyncPointerListener<")
          .Append(self).AppendLine(", TArg> handler) => ev.AsyncListener(handler, arg);");
        sb.Append("        private static void __CmlCbAsync<TCap>(global::Cosmiria.EventStack ev, ref global::Cosmiria.Callback field, TCap cap, global::Cosmiria.AsyncListener<")
          .Append(self).AppendLine(", TCap> handler) => field = ev.Callback(handler, cap);");
        sb.Append("        private static void __CmlCbAsyncA<TArg, TCap>(global::Cosmiria.EventStack ev, ref global::Cosmiria.Callback<TArg> field, TCap cap, global::Cosmiria.AsyncCallbackHandler<")
          .Append(self).AppendLine(", TArg, TCap> handler) => field = ev.Callback(handler, cap);");
        // Keep the cache members referenced (no unused-field warnings) with a stable shape.
        sb.AppendLine();
        sb.AppendLine("        private static void __CmlTouchCaches()");
        sb.AppendLine("        {");
        sb.AppendLine("            if (__cmlStylesVersion < 0) { __cmlStyles = __CmlBuildStyles(); __cmlStylesVersion = 0; }");
        sb.AppendLine("            if (__cmlIconsVersion < 0) { __cmlIcons = __CmlBuildIcons(); __cmlIconsVersion = 0; }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        if (hasNamespace) sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Render an error box in place of the component. Mainly for Hot Reload: dotnet watch applies
    /// the generated code even when the CML has errors, so the running app shows what's wrong
    /// (and the next valid save swaps the real UI back in).
    /// </summary>
    private void EmitErrorBox(StringBuilder sb, IReadOnlyList<string> errors)
    {
        const string S = "default(global::Cosmiria.Style)";
        sb.AppendLine($"            var __cmlErr = ui.View({S}.FlexCol().Gap(2f).Padding(8f).Bg(new global::Riateu.Graphics.Color(0x3B1F24FFu)).Border(1f, new global::Riateu.Graphics.Color(0xBF616AFFu)).Rounded(4f));");
        string title = $"CML error in {System.IO.Path.GetFileName(path)} ({target.Name})";
        sb.AppendLine($"            __cmlErr.Child(ui.Text({StringLiteral(title)}).Styled({S}.TextColorOf(new global::Riateu.Graphics.Color(0xFF8A80FFu))));");
        if (errors != null)
        {
            int shown = 0;
            foreach (string e in errors)
            {
                if (shown++ == 5)
                {
                    sb.AppendLine($"            __cmlErr.Child(ui.Text({StringLiteral($"... and {errors.Count - 5} more")}));");
                    break;
                }
                sb.AppendLine($"            __cmlErr.Child(ui.Text({StringLiteral(e)}).Styled({S}.TextColorOf(new global::Riateu.Graphics.Color(0xE5E9F0FFu))));");
            }
        }
        sb.AppendLine("            return __cmlErr;");
    }

    // ================================================================== content

    private void EmitContent(List<CmlNode> nodes, string parent, bool isCmlRoot = false)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case CmlText t:
                {
                    string v = EmitText(t.Parts, null);
                    if (v != null) Attach(parent, v);
                    break;
                }
                case CmlElement e:
                {
                    if (e.Name == "using")
                    {
                        if (!isCmlRoot)
                            Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, "<using> must be a direct child of <cml>");
                        break;
                    }
                    if (e.Name == "ContextMenu" && parent != null && parent != RootVar) { EmitContextMenu(e, parent); break; }
                    string v = EmitElement(e);
                    if (v != null) Attach(parent, v);
                    break;
                }
                case CmlControl c:
                    EmitControl(c, parent);
                    break;
                case CmlCode code:
                    EmitMapped(code.Code);
                    break;
            }
        }
    }

    private void EmitControl(CmlControl c, string parent)
    {
        EmitMapped(c.Header);
        Line("{");
        indent++;
        EmitContent(c.Body, parent);
        indent--;
        Line("}");
        if (c.Else != null) EmitControl(c.Else, parent);
    }

    private string EmitElement(CmlElement e)
    {
        switch (e.Name)
        {
            case "cml":
                Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, "<cml> can only be the root element");
                return null;
            case "View":
            case "Button":
            {
                var attrs = CheckAttributes(e, ElementAttributes);
                CompiledStyle style = CompileStyle(attrs);
                string v = NewNode();
                string baseStyle = StyleField(style?.Base);
                if (e.Name == "View")
                    Line($"var {v} = ui.View({baseStyle ?? ""});");
                else
                    Line($"var {v} = ui.Button(null, {baseStyle ?? "default(global::Cosmiria.Style)"});");
                ApplyCommon(v, attrs, style);
                EmitContent(e.Children, v);
                return v;
            }
            case "Text":
            {
                var attrs = CheckAttributes(e, ElementAttributes);
                var parts = new List<CmlTextPart>();
                foreach (var child in e.Children)
                {
                    if (child is CmlText t) parts.AddRange(t.Parts);
                    else Report(CmlDiagnostics.ContentNotAllowed, ChildSpan(child),
                        "<Text> can only contain text and @expressions; put elements and @if/@foreach in a <View>");
                }
                CompiledStyle style = CompileStyle(attrs);
                string v = EmitText(parts, null, allowEmpty: true);
                string baseStyle = StyleField(style?.Base);
                if (baseStyle != null) Line($"{v}.Styled({baseStyle});");
                ApplyCommon(v, attrs, style);
                return v;
            }
            case "Icon": return EmitIcon(e);
            case "Image": return EmitImage(e);
            case "Checkbox": return EmitToggleWidget(e, CheckboxAttributes, "checked", "Checkbox", radio: false);
            case "RadioButton": return EmitToggleWidget(e, RadioAttributes, "selected", "RadioButton", radio: true);
            case "Switch": return EmitToggleWidget(e, SwitchAttributes, "on", "Switch", radio: false);
            case "ToggleButton": return EmitToggleWidget(e, ToggleAttributes, "pressed", "ToggleButton", radio: false);
            case "ComboBox": return EmitComboBox(e);
            case "Option": return EmitOption(e);
            case "TextInput": return EmitTextInput(e);
            case "TextArea": return EmitTextArea(e);
            case "Slider": return EmitSlider(e);
            case "ProgressBar": return EmitProgressBar(e);
            case "Split": return EmitSplit(e);
            case "DockSpace": return EmitDockSpace(e);
            case "Modal": return EmitModal(e);
            case "MenuBar": return EmitMenuBar(e);
            case "Menu": return EmitMenu(e);
            case "MenuItem": return EmitMenuItem(e);
            case "MenuSeparator":
                if (menuVar == null) { Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, "<MenuSeparator> must be inside a <Menu> or <ContextMenu>"); return null; }
                CheckAttributes(e, System.Array.Empty<string>());
                Line($"{menuVar}.Separator();");
                return null;
            case "ContextMenu":
                Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, "<ContextMenu> must be inside the element it belongs to (a right click on that element opens it)");
                return null;
            case "Tree": return EmitTree(e);
            case "TreeItem": return EmitTreeItem(e);
            case "Effect": return EmitEffect(e);
            case "Suspense": return EmitSuspense(e);
            case "Fallback":
            case "Failed":
                Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, $"<{e.Name}> must be a direct child of <Suspense>");
                return null;
            case "DockPanel": return EmitDockPanel(e);
            case "Pane":
                Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, "<Pane> must be a direct child of <Split>");
                return null;
            case "Tabs": return EmitTabs(e);
            case "Tab": return EmitTab(e);
            default:
                return EmitComponent(e);
        }
    }

    /// <summary>
    /// Checkbox / RadioButton / Switch / ToggleButton: a bool attribute, content = label. With no
    /// value at all the widget keeps its own (default="..." starts it); a BindableValue (in the
    /// attribute or bind:) is edited in place; bind: to a plain field generates the handler.
    /// </summary>
    private string EmitToggleWidget(CmlElement e, string[] allowed, string boolAttr, string method, bool radio)
    {
        var attrs = CheckAttributes(e, allowed);
        CompiledStyle style = CompileStyle(attrs);
        string styleArg = StyleField(style?.Base) ?? "default(global::Cosmiria.Style)";
        string v = NewNode();
        attrs.TryGetValue("default", out var dflt);
        if (dflt != null && !CheckExpression(dflt)) dflt = null;

        if (radio)
        {
            // Own shared group: group="name" value="..." [default="true"].
            if (attrs.TryGetValue("group", out var group))
            {
                if (!NoConflict(attrs, group, "bind:group", "selected")) return null;
                if (!attrs.TryGetValue("value", out var gv) || !CheckExpression(gv))
                {
                    Report(CmlDiagnostics.MissingAttribute, e.NameSpan, e.Name, "value");
                    return null;
                }
                Line($"var {v} = ui.{method}(ui.RadioGroup({StringLiteral(group.Value.Trim())},");
                EmitMapped(gv.ValueSpan);
                Line(",");
                if (dflt != null) EmitMapped(dflt.ValueSpan); else Line("false");
                Line("),");
                EmitMapped(gv.ValueSpan);
                Line($", null, {styleArg});");
                ApplyCommon(v, attrs, style);
                EmitContent(e.Children, v);
                return v;
            }
            CmlAttributeSyntax bindValue = null;
            BindPath gb = GroupBinding(attrs, boolAttr, out bindValue);
            if (gb != null && IsBindable(gb))
            {
                Line($"var {v} = ui.{method}(");
                EmitMapped(gb.Attr.ValueSpan);
                Line(",");
                EmitMapped(bindValue.ValueSpan);
                Line($", null, {styleArg});");
                ApplyCommon(v, attrs, style);
                EmitContent(e.Children, v);
                return v;
            }
            Line($"var {v} = ui.{method}(");
            if (gb != null) EmitGroupSelected(gb, bindValue);
            else if (attrs.TryGetValue(boolAttr, out var sel) && CheckExpression(sel)) EmitMapped(sel.ValueSpan);
            else Line("false");
            Line($", null, {styleArg});");
            ApplyCommon(v, attrs, style);
            if (gb != null) EmitGroupSelect(v, gb, bindValue, null);
            EmitContent(e.Children, v);
            return v;
        }

        BindPath bind = ToggleBinding(attrs, boolAttr);
        bool hasValue = attrs.TryGetValue(boolAttr, out var flag);
        if (dflt != null && (hasValue || bind != null))
            Report(CmlDiagnostics.ConflictingAttributes, dflt.NameSpan, $"'default' is the starting value of a widget's own value; with {boolAttr} or bind:{boolAttr} it has none");
        Line($"var {v} = ui.{method}(");
        if (bind != null) EmitMapped(bind.Attr.ValueSpan); // a bool, or a BindableValue<bool> (edited in place)
        else if (hasValue && CheckExpression(flag)) EmitMapped(flag.ValueSpan);
        else
        {
            // No value: the widget's own (kept by the framework for this element).
            EmitOwnStart(e, attrs);
            if (dflt != null) EmitMapped(dflt.ValueSpan); else Line("false");
            Line(")");
        }
        Line($", null, {styleArg});");
        ApplyCommon(v, attrs, style);
        if (bind != null && !IsBindable(bind))
            EmitBoundListener(v, "OnClick", bind, null, n => $"{bind.Write(n)} = !{bind.Write(n)}");
        EmitContent(e.Children, v);
        return v;
    }

    /// <summary>bind:checked / bind:on / bind:pressed: replaces the value attribute and onclick.</summary>
    private BindPath ToggleBinding(Dictionary<string, CmlAttributeSyntax> attrs, string boolAttr)
    {
        if (!attrs.TryGetValue("bind:" + boolAttr, out var attr)) return null;
        if (!NoConflict(attrs, attr, boolAttr, "onclick")) return null;
        return ParseBindPath(attr);
    }

    /// <summary>bind:group="Field" value="expr": selected while Field == value, clicking sets Field = value.</summary>
    private BindPath GroupBinding(Dictionary<string, CmlAttributeSyntax> attrs, string boolAttr, out CmlAttributeSyntax value)
    {
        attrs.TryGetValue("value", out value);
        if (!attrs.TryGetValue("bind:group", out var attr))
        {
            if (value != null && !attrs.ContainsKey("group"))
                Report(CmlDiagnostics.ConflictingAttributes, value.NameSpan, "'value' is only used with bind:group=\"...\" or group=\"name\"");
            return null;
        }
        if (value == null)
        {
            Report(CmlDiagnostics.InvalidBinding, attr.NameSpan, attr.Name, "add value=\"...\": the value this choice writes into the bound field");
            return null;
        }
        if (!NoConflict(attrs, attr, boolAttr, "onclick") || !CheckExpression(value)) return null;
        return ParseBindPath(attr);
    }

    private void EmitGroupSelected(BindPath bind, CmlAttributeSyntax value)
    {
        Line("(");
        EmitMapped(bind.Attr.ValueSpan);
        Line(") == (");
        EmitMapped(value.ValueSpan);
        Line(")");
    }

    /// <summary>Click handler for a bound radio/option; also closes a bound combo box.</summary>
    private void EmitGroupSelect(string node, BindPath bind, CmlAttributeSyntax value, BindPath close)
    {
        EmitBoundListener(node, "OnClick", bind, new Capture[] { value.ValueSpan }, n =>
        {
            string assign = $"{bind.Write(n)} = {n.Extra[0]}";
            if (close == null) return assign;
            return $"{{ {assign}; {close.Write(n)} = false; }}";
        }, close);
    }

    /// <summary>Report each attribute in <paramref name="others"/> that a binding replaces.</summary>
    private bool NoConflict(Dictionary<string, CmlAttributeSyntax> attrs, CmlAttributeSyntax bind, params string[] others)
    {
        bool ok = true;
        foreach (string other in others)
        {
            if (!attrs.TryGetValue(other, out var a)) continue;
            Report(CmlDiagnostics.ConflictingAttributes, a.NameSpan,
                $"'{other}' can't be combined with {bind.Name}=\"...\", which already sets it; remove one of them");
            ok = false;
        }
        return ok;
    }

    private string EmitIcon(CmlElement e)
    {
        var attrs = CheckAttributes(e, IconAttributes);
        NoContent(e, "<Icon> cannot have content");
        attrs.TryGetValue("src", out var src);
        attrs.TryGetValue("icon", out var icon);
        if (src != null && icon != null)
        {
            Report(CmlDiagnostics.ConflictingAttributes, icon.NameSpan, "Use either src (an .svg file) or icon (a C# SvgIcon expression) on <Icon>, not both");
            return null;
        }
        if (src == null && icon == null)
        {
            Report(CmlDiagnostics.MissingAttribute, e.NameSpan, "Icon", "src' or 'icon");
            return null;
        }

        string iconExpr = null;
        if (src != null && !EmbedSvg(src, out iconExpr)) return null;
        if (icon != null && !CheckExpression(icon)) return null;

        CompiledStyle style = CompileStyle(attrs);
        string v = NewNode();
        Line($"var {v} = ui.Icon(");
        if (iconExpr != null) Line(iconExpr); else EmitMapped(icon.ValueSpan);
        Line(",");
        if (attrs.TryGetValue("size", out var size) && CheckExpression(size)) EmitMapped(size.ValueSpan); else Line("16f");
        Line(");");
        string baseStyle = StyleField(style?.Base);
        if (baseStyle != null) Line($"{v}.Styled({baseStyle});");
        ApplyCommon(v, attrs, style);
        return v;
    }

    /// <summary>
    /// &lt;Image src="Assets/photo.png"/&gt; (raster, loaded at runtime relative to the app directory),
    /// &lt;Image src="Assets/logo.svg"/&gt; (vector, embedded at build time) or source="expr"
    /// (an ImageSource, SvgIcon or GPU Texture such as a game's render target).
    /// fit="fill|contain|cover", sampling="linear|nearest".
    /// </summary>
    private string EmitImage(CmlElement e)
    {
        var attrs = CheckAttributes(e, ImageAttributes);
        NoContent(e, "<Image> cannot have content");
        attrs.TryGetValue("src", out var src);
        attrs.TryGetValue("source", out var source);
        if (src != null && source != null)
        {
            Report(CmlDiagnostics.ConflictingAttributes, source.NameSpan, "Use either src (a file) or source (a C# ImageSource, SvgIcon or Texture expression) on <Image>, not both");
            return null;
        }
        if (src == null && source == null)
        {
            Report(CmlDiagnostics.MissingAttribute, e.NameSpan, "Image", "src' or 'source");
            return null;
        }

        string fit = "global::Cosmiria.ImageFit.Fill";
        if (attrs.TryGetValue("fit", out var fitAttr))
        {
            switch (fitAttr.Value.Trim())
            {
                case "fill": break;
                case "contain": fit = "global::Cosmiria.ImageFit.Contain"; break;
                case "cover": fit = "global::Cosmiria.ImageFit.Cover"; break;
                default:
                    Report(CmlDiagnostics.UnknownAttribute, fitAttr.ValueSpan, fitAttr.Value, "Image fit", "fill, contain, cover");
                    break;
            }
        }

        string sampling = null;
        if (attrs.TryGetValue("sampling", out var samplingAttr))
        {
            switch (samplingAttr.Value.Trim())
            {
                case "linear": break;
                case "nearest": sampling = "global::Cosmiria.ImageSampling.Nearest"; break;
                default:
                    Report(CmlDiagnostics.UnknownAttribute, samplingAttr.ValueSpan, samplingAttr.Value, "Image sampling", "linear, nearest");
                    break;
            }
        }

        string imageExpr = null;
        if (src != null)
        {
            string file = src.Value.Trim().Replace('\\', '/');
            if (file.EndsWith(".svg", System.StringComparison.OrdinalIgnoreCase))
            {
                if (!EmbedSvg(src, out imageExpr)) return null;
            }
            else if (file.Length == 0)
            {
                Report(CmlDiagnostics.EmptyExpression, src.NameSpan, "The image path");
                return null;
            }
            else imageExpr = $"global::Cosmiria.ImageSource.FromFile({StringLiteral(file)})";
        }
        else if (!CheckExpression(source)) return null;

        CompiledStyle style = CompileStyle(attrs);
        string v = NewNode();
        Line($"var {v} = ui.Image(");
        if (imageExpr != null) Line(imageExpr); else EmitMapped(source.ValueSpan);
        Line($", {fit}, {StyleField(style?.Base) ?? "default(global::Cosmiria.Style)"});");
        if (sampling != null) Line($"{v}.Sampling({sampling});");
        ApplyCommon(v, attrs, style);
        return v;
    }

    /// <summary>Embed an .svg (AdditionalFiles) into the generated code, parsed once per hot-reload version.</summary>
    private bool EmbedSvg(CmlAttributeSyntax src, out string expr)
    {
        expr = null;
        string relative = src.Value.Trim().Replace('\\', '/');
        string expected = relative;
        try { expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", relative)); }
        catch (System.Exception) { }

        AdditionalText file = svgFiles.FirstOrDefault(f => SamePath(f.Path, expected))
            ?? svgFiles.FirstOrDefault(f => f.Path.Replace('\\', '/').EndsWith("/" + relative.TrimStart('/'), System.StringComparison.Ordinal));
        string svg = file?.GetText()?.ToString();
        if (svg == null)
        {
            Report(CmlDiagnostics.SvgNotFound, src.ValueSpan, relative, expected);
            return false;
        }
        try
        {
            using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(svg),
                new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null });
            bool sawSvg = false;
            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element && !sawSvg)
                {
                    if (reader.LocalName != "svg") throw new System.Xml.XmlException($"root element is <{reader.LocalName}>, expected <svg>");
                    sawSvg = true;
                }
            }
        }
        catch (System.Xml.XmlException ex)
        {
            Report(CmlDiagnostics.InvalidSvg, src.ValueSpan, relative, ex.Message);
            return false;
        }

        int index = iconSources.IndexOf(svg);
        if (index < 0) { index = iconSources.Count; iconSources.Add(svg); }
        expr = $"__cmlI[{index}]";
        return true;
    }

    private static bool SamePath(string a, string b)
    {
        try { a = System.IO.Path.GetFullPath(a); } catch (System.Exception) { }
        return string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), System.StringComparison.OrdinalIgnoreCase);
    }

    private string EmitComboBox(CmlElement e)
    {
        var attrs = CheckAttributes(e, ComboAttributes);
        CompiledStyle style = CompileStyle(attrs);
        BindPath bindOpen = null;
        if (attrs.TryGetValue("bind:open", out var bindAttr) && NoConflict(attrs, bindAttr, "open", "ontoggle", "ondismiss"))
            bindOpen = ParseBindPath(bindAttr);
        bool bindableOpen = bindOpen != null && IsBindable(bindOpen);
        string v = NewNode();
        Line($"var {v} = ui.ComboBox(");
        if (attrs.TryGetValue("value", out var value) && CheckExpression(value)) EmitMapped(value.ValueSpan); else Line("\"\"");
        Line(",");
        if (bindOpen != null) EmitMapped(bindOpen.Attr.ValueSpan);
        else if (attrs.TryGetValue("open", out var open) && CheckExpression(open)) EmitMapped(open.ValueSpan);
        else if (!attrs.ContainsKey("ontoggle")) { EmitOwnStart(e, attrs); Line("false)"); } // opens and closes itself
        else Line("false");
        if (bindableOpen) bindOpen = null; // the BindableValue overload handles toggle/dismiss/close
        Line($", {StyleField(style?.Base) ?? "default(global::Cosmiria.Style)"});");
        if (style != null)
        {
            foreach (var dyn in style.Dynamic) { Line($"{v}.Root.Styled("); EmitDynamic(dyn); Line(");"); }
        }
        if (bindOpen != null)
        {
            EmitBoundListener(v, "OnToggle", bindOpen, null, n => $"{bindOpen.Write(n)} = !{bindOpen.Write(n)}");
            EmitBoundListener(v, "OnDismiss", bindOpen, null, n => $"{bindOpen.Write(n)} = false");
        }
        if (attrs.TryGetValue("ontoggle", out var toggle)) EmitHandler(v, "OnToggle", toggle);
        if (attrs.TryGetValue("ondismiss", out var dismiss)) EmitHandler(v, "OnDismiss", dismiss);

        // Children: <Option> elements (optionally inside @foreach/@if/@{ }).
        string previous = comboVar;
        BindPath previousOpen = comboOpen;
        comboVar = v;
        comboOpen = bindOpen;
        foreach (var child in e.Children)
        {
            if (child is CmlText t && IsBlank(t)) continue;
            if (child is CmlText || (child is CmlElement ce && ce.Name != "Option"))
            {
                Report(CmlDiagnostics.ContentNotAllowed, ChildSpan(child), "<ComboBox> can only contain <Option> elements (and @foreach/@if around them)");
                continue;
            }
            EmitContent(new List<CmlNode> { child }, null);
        }
        comboVar = previous;
        comboOpen = previousOpen;
        return v;
    }

    private string EmitOption(CmlElement e)
    {
        if (comboVar == null)
        {
            Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, "<Option> must be inside a <ComboBox>");
            return null;
        }
        var attrs = CheckAttributes(e, OptionAttributes);
        CompiledStyle style = CompileStyle(attrs);
        string v = NewNode();
        BindPath bind = null;
        CmlAttributeSyntax bindValue = null;
        if (attrs.TryGetValue("group", out var group))
        {
            // Own shared selection: group="name" value="..." [default="true"].
            if (!NoConflict(attrs, group, "bind:group", "selected")) return null;
            if (!attrs.TryGetValue("value", out bindValue) || !CheckExpression(bindValue))
            {
                Report(CmlDiagnostics.MissingAttribute, e.NameSpan, "Option", "value");
                return null;
            }
            Line($"var {v} = {comboVar}.Option(ui.RadioGroup({StringLiteral(group.Value.Trim())},");
            EmitMapped(bindValue.ValueSpan);
            Line(",");
            if (attrs.TryGetValue("default", out var d) && CheckExpression(d)) EmitMapped(d.ValueSpan); else Line("false");
            Line("),");
            EmitMapped(bindValue.ValueSpan);
            Line(");");
        }
        else
        {
            bind = GroupBinding(attrs, "selected", out bindValue);
            if (bind != null && IsBindable(bind))
            {
                Line($"var {v} = {comboVar}.Option(");
                EmitMapped(bind.Attr.ValueSpan);
                Line(",");
                EmitMapped(bindValue.ValueSpan);
                Line(");");
                bind = null; // handled by the BindableValue
            }
            else
            {
                Line($"var {v} = {comboVar}.Option(");
                if (bind != null) EmitGroupSelected(bind, bindValue);
                else if (attrs.TryGetValue("selected", out var selected) && CheckExpression(selected)) EmitMapped(selected.ValueSpan);
                else Line("false");
                Line(");");
            }
        }
        string baseStyle = StyleField(style?.Base);
        if (baseStyle != null) Line($"{v}.Styled({baseStyle});");
        ApplyCommon(v, attrs, style);
        if (bind != null) EmitGroupSelect(v, bind, bindValue, comboOpen);
        string saved = comboVar;
        BindPath savedOpen = comboOpen;
        comboVar = null; // nested elements are ordinary content
        comboOpen = null;
        EmitContent(e.Children, v);
        comboVar = saved;
        comboOpen = savedOpen;
        return null; // already attached to the combo's popup
    }

    private string EmitTextInput(CmlElement e)
    {
        var attrs = CheckAttributes(e, TextInputAttributes);
        NoContent(e, "<TextInput> cannot have content; use placeholder=\"...\"");
        attrs.TryGetValue("value", out var value);
        if (value != null && !CheckExpression(value)) return null;
        CompiledStyle style = CompileStyle(attrs);
        string v = NewNode();
        Line($"var {v} = ui.TextInput(");
        EmitTextValue(e, attrs, value);
        string placeholder = attrs.TryGetValue("placeholder", out var ph) ? StringLiteral(ph.Value) : "null";
        Line($", {placeholder}, {StyleField(style?.Base) ?? "default(global::Cosmiria.Style)"});");
        ApplyCommon(v, attrs, style);
        return v;
    }

    /// <summary>&lt;Slider value="V" min="0" max="100" step="1" ondrag="..."/&gt; or bind:value="V".</summary>
    private string EmitSlider(CmlElement e)
    {
        var attrs = CheckAttributes(e, SliderAttributes);
        NoContent(e, "<Slider> cannot have content");
        CompiledStyle style = CompileStyle(attrs);
        BindPath bind = null;
        if (attrs.TryGetValue("bind:value", out var bindAttr) && NoConflict(attrs, bindAttr, "value", "ondrag"))
            bind = ParseBindPath(bindAttr);
        var range = new[] { "min", "max", "step" }
            .Select(n => attrs.TryGetValue(n, out var a) && CheckExpression(a) ? a : null).ToArray();

        bool vertical = Orientation(attrs);
        attrs.TryGetValue("value", out var value);
        attrs.TryGetValue("default", out var dflt);
        bool own = bind == null && value == null;
        bool selfUpdating = own || (bind != null && IsBindable(bind)); // the BindableValue overload drags itself
        string v = NewNode();
        // Named arguments: the same call fits Slider(double, ...) and Slider<T>(BindableValue<T>, ...).
        Line($"var {v} = ui.Slider(");
        if (bind != null) EmitMapped(bind.Attr.ValueSpan);
        else if (value != null && CheckExpression(value)) EmitMapped(value.ValueSpan);
        else
        {
            EmitOwnStart(e, attrs);
            Line("(float)(");
            if (dflt != null && CheckExpression(dflt)) EmitMapped(dflt.ValueSpan); else Line("0");
            Line("))");
        }
        string[] rangeNames = { "min", "max", "step" };
        for (int i = 0; i < 3; i++)
        {
            if (i == 2 && !selfUpdating) break; // step only applies when the slider sets its value
            Line($", {rangeNames[i]}:");
            if (range[i] != null) EmitMapped(range[i].ValueSpan); else Line(i == 0 ? "0" : i == 1 ? "100" : "0");
        }
        Line($", style: {StyleField(style?.Base) ?? "default(global::Cosmiria.Style)"}, vertical: {(vertical ? "true" : "false")});");
        ApplyCommon(v, attrs, style);
        if (bind != null && !selfUpdating)
        {
            // Captured range values come after the path's indexes; missing ones use the defaults.
            var present = range.Where(a => a != null).Select(a => (Capture)a.ValueSpan).ToArray();
            EmitBoundListener(v, "OnDrag", bind, present, n =>
            {
                var args = new List<string>();
                int k = 0;
                for (int i = 0; i < 3; i++) args.Add(range[i] != null ? n.Extra[k++] : (i == 0 ? "0" : i == 1 ? "100" : "0"));
                return $"{bind.Write(n)} = global::Cosmiria.Slider.ValueAt(__e, {bind.Write(n)}, {string.Join(", ", args)})";
            }, pointer: true);
        }
        return v;
    }

    /// <summary>
    /// &lt;Split direction="row|column" bind:size="SidebarWidth" min="120" max="480"&gt; with exactly two
    /// &lt;Pane&gt; children; the first is sized, the second takes the rest. onresize="M($e)" is the
    /// explicit form (use Split.SizeAt(e, ...)).
    /// </summary>
    private string EmitSplit(CmlElement e)
    {
        var attrs = CheckAttributes(e, SplitAttributes);
        CompiledStyle style = CompileStyle(attrs);
        bool vertical = false;
        if (attrs.TryGetValue("direction", out var dir))
        {
            switch (dir.Value.Trim())
            {
                case "row": break;
                case "column": vertical = true; break;
                default: Report(CmlDiagnostics.UnknownAttribute, dir.ValueSpan, dir.Value, "Split direction", "row, column"); break;
            }
        }
        BindPath bind = null;
        if (attrs.TryGetValue("bind:size", out var bindAttr) && NoConflict(attrs, bindAttr, "size", "onresize"))
            bind = ParseBindPath(bindAttr);
        bool own = bind == null && !attrs.ContainsKey("size") && !attrs.ContainsKey("bind:size");
        bool selfUpdating = own || (bind != null && IsBindable(bind));

        var panes = new List<CmlElement>();
        foreach (var child in e.Children)
        {
            if (child is CmlText t && IsBlank(t)) continue;
            if (child is CmlElement ce && ce.Name == "Pane") { panes.Add(ce); continue; }
            Report(CmlDiagnostics.ContentNotAllowed, ChildSpan(child), "<Split> must contain exactly two <Pane> elements (no @if/@foreach around them)");
        }
        if (panes.Count != 2)
        {
            Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, $"<Split> needs exactly two <Pane> children, found {panes.Count}");
            return null;
        }

        string v = NewNode();
        var limits = new[] { "min", "max", "second-min" }
            .Select(n => attrs.TryGetValue(n, out var a) && CheckExpression(a) ? a : null).ToArray();
        // Named arguments: the same call fits Split(Length, ...) and Split(BindableValue<float>, ...).
        Line($"var {v} = ui.Split(");
        if (bind != null) EmitMapped(bind.Attr.ValueSpan);
        else if (attrs.TryGetValue("size", out var size) && CheckExpression(size)) EmitMapped(size.ValueSpan);
        else
        {
            EmitOwnStart(e, attrs);
            Line("(float)(");
            if (attrs.TryGetValue("default", out var d) && CheckExpression(d)) EmitMapped(d.ValueSpan); else Line("200");
            Line("))");
        }
        Line($", vertical: {(vertical ? "true" : "false")}");
        if (selfUpdating)
        {
            string[] names = { "min", "max", "secondMin" };
            for (int i = 0; i < 3; i++)
            {
                if (limits[i] == null) continue;
                Line($", {names[i]}:");
                EmitMapped(limits[i].ValueSpan);
            }
        }
        Line($", style: {StyleField(style?.Base) ?? "default(global::Cosmiria.Style)"});");
        if (style != null)
        {
            foreach (var dyn in style.Dynamic) { Line($"{v}.Root.Styled("); EmitDynamic(dyn); Line(");"); }
            EmitStateStyle($"{v}.Root", "Hover", style.Hover, style.HoverDynamic);
        }

        string handle = $"{v}.Handle";
        if (bind != null && !selfUpdating)
        {
            var present = limits.Where(a => a != null).Select(a => (Capture)a.ValueSpan).ToArray();
            EmitBoundListener(handle, "OnDrag", bind, present, n =>
            {
                var args = new List<string> { "__e", vertical ? "true" : "false" };
                int k = 0;
                string[] defaults = { "0f", "float.MaxValue", "0f" };
                for (int i = 0; i < 3; i++) args.Add(limits[i] != null ? n.Extra[k++] : defaults[i]);
                return $"{bind.Write(n)} = global::Cosmiria.Split.SizeAt({string.Join(", ", args)})";
            }, pointer: true);
        }
        else if (attrs.TryGetValue("onresize", out var resize)) EmitHandler(handle, "OnDrag", resize);

        string[] targets = { $"{v}.First", $"{v}.Second" };
        for (int i = 0; i < 2; i++)
        {
            var pane = panes[i];
            var paneAttrs = CheckAttributes(pane, PaneAttributes);
            CompiledStyle paneStyle = CompileStyle(paneAttrs);
            string baseStyle = StyleField(paneStyle?.Base);
            if (baseStyle != null) Line($"{targets[i]}.Styled({baseStyle});");
            ApplyCommon(targets[i], paneAttrs, paneStyle);
            EmitContent(pane.Children, targets[i]);
        }
        return v;
    }

    /// <summary>&lt;DockSpace layout="Layout"&gt; containing &lt;DockPanel name="scene" title="Scene"&gt; elements.</summary>
    private string EmitDockSpace(CmlElement e)
    {
        var attrs = CheckAttributes(e, DockSpaceAttributes);
        if (!attrs.TryGetValue("layout", out var layout))
        {
            Report(CmlDiagnostics.MissingAttribute, e.NameSpan, "DockSpace", "layout");
            return null;
        }
        if (!CheckExpression(layout)) return null;
        CompiledStyle style = CompileStyle(attrs);
        string v = NewNode();
        Line($"var {v} = ui.DockSpace(");
        EmitMapped(layout.ValueSpan);
        Line(",");
        if (attrs.TryGetValue("key", out var key) && CheckExpression(key)) EmitMapped(key.ValueSpan); else Line("0");
        Line($", {StyleField(style?.Base) ?? "default(global::Cosmiria.Style)"}");
        Line(");");

        string previous = dockVar;
        bool savedDockKeep = dockKeepAlive;
        dockKeepAlive = KeepAlive(attrs) && attrs.ContainsKey("keep-alive");
        dockVar = v;
        foreach (var child in e.Children)
        {
            if (child is CmlText t && IsBlank(t)) continue;
            if (child is CmlText || (child is CmlElement ce && ce.Name != "DockPanel"))
            {
                Report(CmlDiagnostics.ContentNotAllowed, ChildSpan(child), "<DockSpace> can only contain <DockPanel> elements (and @if/@foreach around them)");
                continue;
            }
            EmitContent(new List<CmlNode> { child }, null);
        }
        dockVar = previous;
        dockKeepAlive = savedDockKeep;
        return v;
    }

    private string EmitDockPanel(CmlElement e)
    {
        if (dockVar == null)
        {
            Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, "<DockPanel> must be inside a <DockSpace>");
            return null;
        }
        var attrs = CheckAttributes(e, DockPanelAttributes);
        if (!attrs.TryGetValue("name", out var name) || name.Value.Trim().Length == 0)
        {
            Report(CmlDiagnostics.MissingAttribute, e.NameSpan, "DockPanel", "name");
            return null;
        }
        string title = attrs.TryGetValue("title", out var t) ? t.Value : name.Value.Trim();
        string dock = dockVar;
        string content = NewNode();
        // Only the visible tab's content is built, straight into its group.
        // closable="true" (or any bool expression): a close button on its tab.
        Line($"if ({dock}.Panel({StringLiteral(name.Value.Trim())}, {StringLiteral(title)}, out var {content}");
        if (attrs.TryGetValue("closable", out var closable) && CheckExpression(closable))
        {
            Line(", closable:");
            EmitMapped(closable.ValueSpan);
        }
        Line("))");
        Line("{");
        indent++;
        dockVar = null;
        bool savedDefault = keepAliveDefault;
        keepAliveDefault |= dockKeepAlive; // components in a keep-alive dock panel survive while it's hidden
        EmitContent(e.Children, content);
        keepAliveDefault = savedDefault;
        dockVar = dock;
        indent--;
        Line("}");
        return null;
    }

    /// <summary>
    /// Start an own-value call for <paramref name="e"/>: <c>ui.Own(site, </c> or, with key="...",
    /// <c>ui.OwnKeyed(site, key, </c>. The caller emits the initial value and the closing ')'.
    /// </summary>
    private void EmitOwnStart(CmlElement e, Dictionary<string, CmlAttributeSyntax> attrs)
    {
        if (attrs.TryGetValue("key", out var key) && CheckExpression(key))
        {
            Line($"ui.OwnKeyed({SiteOf(e)},");
            EmitMapped(key.ValueSpan);
            Line(",");
        }
        else Line($"ui.Own({SiteOf(e)},");
    }

    /// <summary>&lt;Modal open="ShowAbout" title="About"&gt;content&lt;/Modal&gt; (content is built only while open).</summary>
    private string EmitModal(CmlElement e)
    {
        var attrs = CheckAttributes(e, ModalAttributes);
        CompiledStyle style = CompileStyle(attrs);
        BindPath bind = null;
        if (attrs.TryGetValue("bind:open", out var bindAttr) && NoConflict(attrs, bindAttr, "open", "ondismiss"))
            bind = ParseBindPath(bindAttr);
        attrs.TryGetValue("open", out var open);
        if (bind == null && open == null)
        {
            Report(CmlDiagnostics.MissingAttribute, e.NameSpan, "Modal", "open' or 'bind:open");
            return null;
        }
        bool bindable = bind != null && IsBindable(bind);
        bool dismissable = true;
        if (attrs.TryGetValue("dismissable", out var dis))
        {
            switch (dis.Value.Trim())
            {
                case "true": break;
                case "false": dismissable = false; break;
                default: Report(CmlDiagnostics.UnknownAttribute, dis.ValueSpan, dis.Value, "dismissable", "true, false"); break;
            }
        }
        string v = NewNode();
        Line($"var {v} = ui.Modal(");
        if (bind != null) EmitMapped(bind.Attr.ValueSpan);
        else if (CheckExpression(open)) EmitMapped(open.ValueSpan); else Line("false");
        string title = attrs.TryGetValue("title", out var t) ? StringLiteral(t.Value) : "null";
        Line($", title: {title}{(dis != null && (bindable || (bind == null)) ? $", dismissable: {(dismissable ? "true" : "false")}" : "")}, style: {StyleField(style?.Base) ?? "default(global::Cosmiria.Style)"});");
        if (bind != null && !bindable && dismissable)
            EmitBoundListener(v, "OnDismiss", bind, null, n => $"{bind.Write(n)} = false");
        if (attrs.TryGetValue("ondismiss", out var ondismiss)) EmitHandler(v, "OnDismiss", ondismiss);
        Line($"if ({v}.IsOpen)");
        Line("{");
        indent++;
        EmitContent(e.Children, $"{v}.Body");
        indent--;
        Line("}");
        return v;
    }

    /// <summary>&lt;MenuBar&gt; with &lt;Menu title="File"&gt; children (opens and closes itself, or open="Box").</summary>
    private string EmitMenuBar(CmlElement e)
    {
        var attrs = CheckAttributes(e, MenuBarAttributes);
        CompiledStyle style = CompileStyle(attrs);
        string v = NewNode();
        Line($"var {v} = ui.MenuBar(");
        if (attrs.TryGetValue("open", out var open) && CheckExpression(open)) EmitMapped(open.ValueSpan);
        else { EmitOwnStart(e, attrs); Line("-1)"); }
        Line($", {StyleField(style?.Base) ?? "default(global::Cosmiria.Style)"});");
        string previous = menuBarVar;
        menuBarVar = v;
        foreach (var child in e.Children)
        {
            if (child is CmlText tx && IsBlank(tx)) continue;
            if (child is CmlText || (child is CmlElement ce && ce.Name != "Menu"))
            {
                Report(CmlDiagnostics.ContentNotAllowed, ChildSpan(child), "<MenuBar> can only contain <Menu> elements (and @if/@foreach around them)");
                continue;
            }
            EmitContent(new List<CmlNode> { child }, null);
        }
        menuBarVar = previous;
        return v;
    }

    /// <summary>&lt;Menu title="File"&gt;: a menu bar entry, or a submenu inside another menu.</summary>
    private string EmitMenu(CmlElement e)
    {
        var attrs = CheckAttributes(e, MenuAttributes);
        string title = attrs.TryGetValue("title", out var t) ? StringLiteral(t.Value) : "\"\"";
        string v = NewNode();
        bool topLevel = menuVar == null;
        if (menuVar != null) Line($"var {v} = {menuVar}.Submenu({title});");
        else if (menuBarVar != null) Line($"var {v} = {menuBarVar}.Menu({title});");
        else
        {
            Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, "<Menu> must be inside a <MenuBar>, another <Menu> or a <ContextMenu>");
            return null;
        }
        if (topLevel) EmitMenuShortcuts(e.Children);
        EmitMenuContent(e, v);
        return null;
    }

    /// <summary>
    /// Register the shortcuts of a menu's items (and its submenus') whether or not it's open, so
    /// shortcut="Ctrl+S" onclick="Save" works from anywhere. Items inside @if/@for are skipped.
    /// </summary>
    private void EmitMenuShortcuts(List<CmlNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is not CmlElement e) continue;
            if (e.Name == "Menu") { EmitMenuShortcuts(e.Children); continue; }
            if (e.Name != "MenuItem") continue;
            var shortcut = e.Attributes.FirstOrDefault(a => a.Name == "shortcut");
            var click = e.Attributes.FirstOrDefault(a => a.Name == "onclick");
            if (shortcut == null || click == null || shortcut.Value.Trim().Length == 0) continue;
            EmitHandler($"ui.ShortcutFor({StringLiteral(shortcut.Value.Trim())})", "Run", click);
        }
    }

    /// <summary>Items of an open menu: MenuItem, MenuSeparator, submenus (built only while open).</summary>
    private void EmitMenuContent(CmlElement e, string menu)
    {
        Line($"if ({menu}.IsOpen)");
        Line("{");
        indent++;
        string savedMenu = menuVar, savedBar = menuBarVar;
        menuVar = menu;
        menuBarVar = null;
        foreach (var child in e.Children)
        {
            if (child is CmlText tx && IsBlank(tx)) continue;
            if (child is CmlText || (child is CmlElement ce && ce.Name != "MenuItem" && ce.Name != "MenuSeparator" && ce.Name != "Menu"))
            {
                Report(CmlDiagnostics.ContentNotAllowed, ChildSpan(child), "Menus can only contain <MenuItem>, <MenuSeparator> and <Menu> (submenu) elements");
                continue;
            }
            EmitContent(new List<CmlNode> { child }, null);
        }
        menuVar = savedMenu;
        menuBarVar = savedBar;
        indent--;
        Line("}");
    }

    /// <summary>&lt;MenuItem shortcut="Ctrl+S" onclick="Save"&gt;Save&lt;/MenuItem&gt;: clicking runs it and closes the menu.</summary>
    private string EmitMenuItem(CmlElement e)
    {
        if (menuVar == null)
        {
            Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, "<MenuItem> must be inside a <Menu> or <ContextMenu>");
            return null;
        }
        var attrs = CheckAttributes(e, MenuItemAttributes);
        CompiledStyle style = CompileStyle(attrs);
        string shortcut = attrs.TryGetValue("shortcut", out var sc) ? StringLiteral(sc.Value) : "null";
        string v = NewNode();
        Line($"var {v} = {menuVar}.Item(null, {shortcut});");
        string row = $"{v}.Row";
        string baseStyle = StyleField(style?.Base);
        if (baseStyle != null) Line($"{row}.Styled({baseStyle});");
        ApplyCommon(row, attrs, style);
        string saved = menuVar;
        menuVar = null; // the label content is ordinary markup
        EmitContent(e.Children, $"{v}.Content");
        menuVar = saved;
        return null;
    }

    /// <summary>&lt;ContextMenu&gt; inside an element: a right click on that element opens it at the pointer.</summary>
    private void EmitContextMenu(CmlElement e, string target, string call = null)
    {
        var attrs = CheckAttributes(e, ContextMenuAttributes);
        string v = NewNode();
        Line($"var {v} = {call ?? $"ui.ContextMenu({target},"}");
        if (attrs.TryGetValue("position", out var pos) && CheckExpression(pos)) EmitMapped(pos.ValueSpan);
        else { EmitOwnStart(e, attrs); Line("new global::System.Numerics.Vector2(float.NaN))"); }
        Line(");");
        Line($"{target}.Child({v}.Popup);");
        EmitMenuShortcuts(e.Children);
        string savedBar = menuBarVar;
        menuBarVar = null;
        EmitMenuContent(e, v);
        menuBarVar = savedBar;
    }

    /// <summary>&lt;Tree&gt; with nested &lt;TreeItem label="..."&gt; elements; selection is a BindableValue&lt;string&gt; (own by default).</summary>
    private string EmitTree(CmlElement e)
    {
        var attrs = CheckAttributes(e, TreeAttributes);
        CompiledStyle style = CompileStyle(attrs);
        string v = NewNode();
        Line($"var {v} = ui.Tree(");
        if (attrs.TryGetValue("bind:selected", out var bindAttr))
        {
            BindPath bind = ParseBindPath(bindAttr);
            if (bind != null && !IsBindable(bind))
                Report(CmlDiagnostics.InvalidBinding, bindAttr.NameSpan, bindAttr.Name, "a tree's selection must be a BindableValue<string> (it's edited in place); use selected=\"...\" or leave it out for its own");
            EmitMapped(bindAttr.ValueSpan);
        }
        else if (attrs.TryGetValue("selected", out var sel) && CheckExpression(sel)) EmitMapped(sel.ValueSpan);
        else { EmitOwnStart(e, attrs); Line("(string)null)"); }
        Line($", {StyleField(style?.Base) ?? "default(global::Cosmiria.Style)"});");
        EmitTreeChildren(e, v);
        return v;
    }

    private static bool IsContextMenu(CmlNode n) => n is CmlElement { Name: "ContextMenu" };

    private void EmitTreeChildren(CmlElement e, string parent)
    {
        string saved = treeVar;
        treeVar = parent;
        foreach (var child in e.Children)
        {
            if (child is CmlText tx && IsBlank(tx)) continue;
            if (IsContextMenu(child))
            {
                // A tree item's own menu is emitted with its row; on the <Tree>, the whole tree's menu.
                if (e.Name == "Tree") EmitContextMenu((CmlElement)child, $"{parent}.Root");
                continue;
            }
            if (child is CmlText || (child is CmlElement ce && ce.Name != "TreeItem"))
            {
                Report(CmlDiagnostics.ContentNotAllowed, ChildSpan(child), "Trees can only contain <TreeItem> elements (and @if/@foreach around them) and a <ContextMenu>");
                continue;
            }
            EmitContent(new List<CmlNode> { child }, null);
        }
        treeVar = saved;
    }

    /// <summary>&lt;TreeItem label="Assets" value="..."&gt; children &lt;/TreeItem&gt;: expands itself (default="true") unless expanded= is given.</summary>
    private string EmitTreeItem(CmlElement e)
    {
        if (treeVar == null)
        {
            Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, "<TreeItem> must be inside a <Tree> or another <TreeItem>");
            return null;
        }
        var attrs = CheckAttributes(e, TreeItemAttributes);
        CompiledStyle style = CompileStyle(attrs);
        // A <ContextMenu> child is the row's right-click menu, not a child item.
        bool leaf = e.Children.TrueForAll(c => (c is CmlText t && IsBlank(t)) || IsContextMenu(c));
        string v = NewNode();
        Line($"var {v} = {treeVar}.Item(");
        if (attrs.TryGetValue("label", out var l)) EmitTextOrExpression(l); else Line("null");
        Line(", value:");
        if (attrs.TryGetValue("value", out var value) && CheckExpression(value)) EmitMapped(value.ValueSpan); else Line("null");
        Line(", expanded:");
        if (leaf) Line("null");
        else if (attrs.TryGetValue("bind:expanded", out var be)) EmitMapped(be.ValueSpan);
        else if (attrs.TryGetValue("expanded", out var ex) && CheckExpression(ex)) EmitMapped(ex.ValueSpan);
        else
        {
            EmitOwnStart(e, attrs);
            if (attrs.TryGetValue("default", out var d) && CheckExpression(d)) EmitMapped(d.ValueSpan); else Line("false");
            Line(")");
        }
        Line($", leaf: {(leaf ? "true" : "false")});");
        string row = $"{v}.Row";
        string baseStyle = StyleField(style?.Base);
        if (baseStyle != null) Line($"{row}.Styled({baseStyle});");
        // The item's own menu first: an oncontextmenu on the item replaces its select-on-right-click.
        foreach (var child in e.Children)
            if (IsContextMenu(child)) EmitContextMenu((CmlElement)child, row, $"{v}.ContextMenu(");
        ApplyCommon(row, attrs, style);
        if (!leaf)
        {
            Line($"if ({v}.IsExpanded)");
            Line("{");
            indent++;
            EmitTreeChildren(e, v);
            indent--;
            Line("}");
        }
        return null;
    }

    /// <summary>
    /// &lt;Suspense fallback="Loading..."&gt; content &lt;/Suspense&gt;, or with &lt;Fallback&gt; markup and an
    /// optional &lt;Failed&gt; view (where <c>error</c> is the exception). Emitted inline as try/catch:
    /// if a resource read in the content is still loading, what the content built is rolled back
    /// and the fallback is built instead.
    /// </summary>
    private string EmitSuspense(CmlElement e)
    {
        var attrs = CheckAttributes(e, SuspenseAttributes);
        CompiledStyle style = CompileStyle(attrs);
        CmlElement fallback = null, failed = null;
        var content = new List<CmlNode>();
        foreach (var child in e.Children)
        {
            if (child is CmlElement { Name: "Fallback" } f)
            {
                if (fallback != null) Report(CmlDiagnostics.ContentNotAllowed, f.NameSpan, "<Suspense> takes one <Fallback>");
                fallback = f;
            }
            else if (child is CmlElement { Name: "Failed" } x)
            {
                if (failed != null) Report(CmlDiagnostics.ContentNotAllowed, x.NameSpan, "<Suspense> takes one <Failed>");
                failed = x;
            }
            else content.Add(child);
        }
        attrs.TryGetValue("fallback", out var fallbackText);
        if (fallback != null && fallbackText != null)
            Report(CmlDiagnostics.ContentNotAllowed, fallbackText.NameSpan, "use either fallback=\"...\" or a <Fallback> element, not both");

        string v = NewNode();
        Line($"var {v} = ui.View(ui.Style().FlexCol());");
        string baseStyle = StyleField(style?.Base);
        if (baseStyle != null) Line($"{v}.Styled({baseStyle});");
        string scope = v + "Scope";
        Line($"var {scope} = ui.BeginSuspense();");
        Line("try");
        Line("{");
        indent++;
        EmitContent(content, v);
        Line($"ui.EndSuspense({scope});");
        indent--;
        Line("}");
        Line("catch (global::Cosmiria.SuspendedException)");
        Line("{");
        indent++;
        Line($"ui.AbortSuspense({scope});");
        if (fallback != null) EmitContent(fallback.Children, v);
        else if (fallbackText != null)
        {
            Line($"{v}.Child(ui.Text(");
            EmitTextOrExpression(fallbackText);
            Line("));");
        }
        indent--;
        Line("}");
        if (failed != null)
        {
            Line("catch (global::Cosmiria.ResourceFailedException __failed)");
            Line("{");
            indent++;
            Line($"ui.AbortSuspense({scope});");
            Line("var error = __failed.InnerException;");
            EmitContent(failed.Children, v);
            indent--;
            Line("}");
        }
        return v;
    }

    /// <summary>
    /// &lt;Effect deps="LevelIndex" run="LoadLevel"/&gt;: runs the handler after the build where it first
    /// appears, and whenever deps change (no deps: once). Renders nothing.
    /// </summary>
    private string EmitEffect(CmlElement e)
    {
        var attrs = CheckAttributes(e, EffectAttributes);
        NoContent(e, "<Effect> cannot have content");
        if (!attrs.TryGetValue("run", out var run))
        {
            Report(CmlDiagnostics.MissingAttribute, e.NameSpan, "Effect", "run");
            return null;
        }
        string v = NewNode();
        Line($"var {v} = ui.EffectAt({SiteOf(e)},");
        if (attrs.TryGetValue("deps", out var deps) && CheckExpression(deps)) EmitMapped(deps.ValueSpan); else Line("0");
        Line(");");
        EmitHandler(v, "Run", run);
        return null;
    }

    /// <summary>An attribute that's text (a string literal), or "@Expr" / "@(expr)" for a C# string.</summary>
    private void EmitTextOrExpression(CmlAttributeSyntax attr)
    {
        string text = attr.Value.Trim();
        if (!text.StartsWith("@")) { Line(StringLiteral(attr.Value)); return; }
        int a = attr.Value.IndexOf('@') + 1, b = attr.Value.TrimEnd().Length;
        if (a < b && attr.Value[a] == '(' && attr.Value[b - 1] == ')') { a++; b--; }
        EmitMapped(new TextSpan(attr.ValueSpan.Start + a, b - a));
    }

    /// <summary>&lt;ProgressBar value="Loaded" min="0" max="1" orientation="vertical"/&gt;: display only.</summary>
    private string EmitProgressBar(CmlElement e)
    {
        var attrs = CheckAttributes(e, ProgressAttributes);
        NoContent(e, "<ProgressBar> cannot have content");
        if (!attrs.TryGetValue("value", out var value))
        {
            Report(CmlDiagnostics.MissingAttribute, e.NameSpan, "ProgressBar", "value");
            return null;
        }
        if (!CheckExpression(value)) return null;
        CompiledStyle style = CompileStyle(attrs);
        string v = NewNode();
        Line($"var {v} = ui.ProgressBar(");
        EmitMapped(value.ValueSpan);
        foreach (string n in new[] { "min", "max" })
        {
            Line($", {n}:");
            if (attrs.TryGetValue(n, out var a) && CheckExpression(a)) EmitMapped(a.ValueSpan); else Line(n == "min" ? "0" : "100");
        }
        Line($", style: {StyleField(style?.Base) ?? "default(global::Cosmiria.Style)"}, vertical: {(Orientation(attrs) ? "true" : "false")});");
        ApplyCommon(v, attrs, style);
        return v;
    }

    private bool Orientation(Dictionary<string, CmlAttributeSyntax> attrs)
    {
        if (!attrs.TryGetValue("orientation", out var o)) return false;
        switch (o.Value.Trim())
        {
            case "horizontal": return false;
            case "vertical": return true;
            default:
                Report(CmlDiagnostics.UnknownAttribute, o.ValueSpan, o.Value, "orientation", "horizontal, vertical");
                return false;
        }
    }

    /// <summary>A text input's buffer: value="Buffer", or its own (default="text" starts it).</summary>
    private void EmitTextValue(CmlElement e, Dictionary<string, CmlAttributeSyntax> attrs, CmlAttributeSyntax value)
    {
        attrs.TryGetValue("default", out var dflt);
        if (value != null)
        {
            if (dflt != null) Report(CmlDiagnostics.ConflictingAttributes, dflt.NameSpan, "'default' is the starting text of the input's own buffer; with value=\"...\" it has none");
            EmitMapped(value.ValueSpan);
            return;
        }
        string initial = dflt != null ? StringLiteral(dflt.Value) : "null";
        if (attrs.TryGetValue("key", out var key) && CheckExpression(key))
        {
            Line($"ui.OwnTextKeyed({SiteOf(e)},");
            EmitMapped(key.ValueSpan);
            Line($", {initial})");
        }
        else Line($"ui.OwnText({SiteOf(e)}, {initial})");
    }

    /// <summary>A stable id for an element's own value: its file and position (deterministic FNV-1a).</summary>
    private string SiteOf(CmlElement e)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in path) { h ^= c; h *= 16777619; }
            h ^= (uint)e.NameSpan.Start; h *= 16777619;
            return ((int)h).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Whether a bind: path's member is a Cosmiria.BindableValue (passed through instead of generating a handler).</summary>
    private bool IsBindable(BindPath bind)
    {
        ITypeSymbol type = ResolvePathType(bind.Attr.Value);
        return type is INamedTypeSymbol { Name: "BindableValue", ContainingNamespace: { Name: "Cosmiria" } };
    }

    /// <summary>Type of a member path like A.B[i].C on the component (null if it can't be followed).</summary>
    private ITypeSymbol ResolvePathType(string text)
    {
        ITypeSymbol current = SelfSymbol;
        if (current == null) return null;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == '.') { i++; continue; }
            if (c == '[')
            {
                int depth = 0;
                for (; i < text.Length; i++) { if (text[i] == '[') depth++; else if (text[i] == ']' && --depth == 0) break; }
                i++;
                if (current is IArrayTypeSymbol array) current = array.ElementType;
                else
                {
                    var indexer = current.GetMembers("this[]").OfType<IPropertySymbol>().FirstOrDefault();
                    if (indexer == null) return null;
                    current = indexer.Type;
                }
                continue;
            }
            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            if (i == start) return null;
            string name = text.Substring(start, i - start);
            ISymbol member = null;
            for (ITypeSymbol t = current; t != null && member == null; t = t.BaseType)
                member = t.GetMembers(name).FirstOrDefault(m => m is IFieldSymbol or IPropertySymbol);
            current = member switch { IFieldSymbol f => f.Type, IPropertySymbol pr => pr.Type, _ => null };
            if (current == null) return null;
        }
        return current;
    }

    /// <summary>&lt;Tabs selected="Page"&gt; or bind:selected="Page", containing &lt;Tab title="..."&gt; elements.</summary>
    private string EmitTabs(CmlElement e)
    {
        var attrs = CheckAttributes(e, TabsAttributes);
        CompiledStyle style = CompileStyle(attrs);
        BindPath bind = null;
        if (attrs.TryGetValue("bind:selected", out var bindAttr) && NoConflict(attrs, bindAttr, "selected"))
            bind = ParseBindPath(bindAttr);

        string v = NewNode();
        Line($"var {v} = ui.Tabs(");
        if (bind != null) EmitMapped(bind.Attr.ValueSpan);
        else if (attrs.TryGetValue("selected", out var selected) && CheckExpression(selected)) EmitMapped(selected.ValueSpan);
        else
        {
            EmitOwnStart(e, attrs); // selects itself
            if (attrs.TryGetValue("default", out var d) && CheckExpression(d)) EmitMapped(d.ValueSpan); else Line("0");
            Line(")");
        }
        if (bind != null && IsBindable(bind)) bind = null; // the BindableValue overload selects on click
        Line($", {StyleField(style?.Base) ?? "default(global::Cosmiria.Style)"});");
        if (style != null)
        {
            foreach (var dyn in style.Dynamic) { Line($"{v}.Root.Styled("); EmitDynamic(dyn); Line(");"); }
        }
        Line($"int {v}I = 0;"); // index of the next <Tab>, counted at runtime so @if/@foreach work
        bool savedTabsKeep = tabsKeepAlive;
        tabsKeepAlive = KeepAlive(attrs) && attrs.ContainsKey("keep-alive");

        string previous = tabsVar;
        BindPath previousBind = tabsBind;
        tabsVar = v;
        tabsBind = bind;
        foreach (var child in e.Children)
        {
            if (child is CmlText t && IsBlank(t)) continue;
            if (child is CmlText || (child is CmlElement ce && ce.Name != "Tab"))
            {
                Report(CmlDiagnostics.ContentNotAllowed, ChildSpan(child), "<Tabs> can only contain <Tab> elements (and @foreach/@if around them)");
                continue;
            }
            EmitContent(new List<CmlNode> { child }, null);
        }
        tabsVar = previous;
        tabsBind = previousBind;
        tabsKeepAlive = savedTabsKeep;
        return v;
    }

    private string EmitTab(CmlElement e)
    {
        if (tabsVar == null)
        {
            Report(CmlDiagnostics.ContentNotAllowed, e.NameSpan, "<Tab> must be inside a <Tabs>");
            return null;
        }
        var attrs = CheckAttributes(e, TabAttributes);
        CompiledStyle style = CompileStyle(attrs);
        string tabs = tabsVar;
        BindPath bind = tabsBind;
        if (bind != null && attrs.TryGetValue("onclick", out var click))
        {
            Report(CmlDiagnostics.ConflictingAttributes, click.NameSpan,
                $"'onclick' can't be used on a <Tab> inside <Tabs {bind.Name}=\"...\">, which already selects the tab on click");
            attrs.Remove("onclick");
        }

        string v = NewNode();
        string index = $"{v}I";
        Line($"int {index} = {tabs}I++;");
        string title = attrs.TryGetValue("title", out var t) ? StringLiteral(t.Value) : "null";
        Line($"var {v} = {tabs}.Tab({index}, {title});");
        string baseStyle = StyleField(style?.Base);
        if (baseStyle != null) Line($"{v}.Styled({baseStyle});");
        ApplyCommon(v, attrs, style);
        if (bind != null)
            EmitBoundListener(v, "OnClick", bind, new[] { Capture.Generated(index) }, n => $"{bind.Write(n)} = {n.Extra[0]}");

        // Only the selected tab's content is built, straight into the panel.
        Line($"if ({tabs}.Selected == {index})");
        Line("{");
        indent++;
        tabsVar = null; // nested elements are ordinary content
        tabsBind = null;
        bool savedDefault = keepAliveDefault;
        keepAliveDefault |= tabsKeepAlive; // components in a keep-alive tab survive while it's hidden
        EmitContent(e.Children, $"{tabs}.Panel");
        keepAliveDefault = savedDefault;
        tabsVar = tabs;
        tabsBind = bind;
        indent--;
        Line("}");
        return null; // already attached to the tab bar
    }

    /// <summary>&lt;TextArea value="Notes" placeholder="..." rows="5"/&gt;: a multi-line TextInput.</summary>
    private string EmitTextArea(CmlElement e)
    {
        var attrs = CheckAttributes(e, TextAreaAttributes);
        NoContent(e, "<TextArea> cannot have content; use placeholder=\"...\"");
        attrs.TryGetValue("value", out var value);
        if (value != null && !CheckExpression(value)) return null;
        CompiledStyle style = CompileStyle(attrs);
        string v = NewNode();
        Line($"var {v} = ui.TextArea(");
        EmitTextValue(e, attrs, value);
        string placeholder = attrs.TryGetValue("placeholder", out var ph) ? StringLiteral(ph.Value) : "null";
        Line($", {placeholder},");
        if (attrs.TryGetValue("rows", out var rows) && CheckExpression(rows)) EmitMapped(rows.ValueSpan); else Line("4");
        Line($", {StyleField(style?.Base) ?? "default(global::Cosmiria.Style)"});");
        ApplyCommon(v, attrs, style);
        return v;
    }

    private void NoContent(CmlElement e, string message)
    {
        foreach (var child in e.Children)
        {
            if (child is CmlText t && IsBlank(t)) continue;
            Report(CmlDiagnostics.ContentNotAllowed, ChildSpan(child), message);
        }
    }

    private string EmitComponent(CmlElement e)
    {
        INamedTypeSymbol type = ResolveComponent(e.Name, out string why);
        if (type == null)
        {
            if (why == null) Report(CmlDiagnostics.UnknownElement, e.NameSpan, e.Name);
            else Report(CmlDiagnostics.InvalidComponent, e.NameSpan, e.Name, why);
            return null;
        }

        // bind:Field="ParentPath" attributes are allowed on top of key/state, and so is every
        // Callback field of the child, by name (any case): <Picker onpick="Pick($e)"/>.
        var binds = e.Attributes.Where(a => a.Name.StartsWith("bind:", System.StringComparison.Ordinal)).ToList();
        var callbackFields = CallbackFields(type);
        var callbackAttrs = e.Attributes.Where(a => callbackFields.ContainsKey(a.Name)).ToList();
        var attrs = CheckAttributes(e, ComponentAttributes.Concat(binds.Select(b => b.Name)).Concat(callbackAttrs.Select(a => a.Name)).Distinct().ToArray());
        foreach (var child in e.Children)
        {
            if (child is CmlText t && IsBlank(t)) continue;
            Report(CmlDiagnostics.ContentNotAllowed, ChildSpan(child), $"Component <{e.Name}> cannot have content; it renders itself");
        }

        string fq = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string v = NewNode();
        attrs.TryGetValue("key", out var key);
        attrs.TryGetValue("state", out var state);
        if (state != null && !CheckExpression(state)) state = null;

        var bound = new List<(CmlAttributeSyntax attr, string field)>();
        foreach (var b in binds)
        {
            if (!attrs.TryGetValue(b.Name, out var attr) || attr != b) continue; // duplicate, already reported
            string field = b.Name.Substring("bind:".Length);
            string problem = CheckBindableField(type, field);
            if (problem != null) { Report(CmlDiagnostics.InvalidBinding, b.NameSpan, b.Name, problem); continue; }
            if (CheckExpression(b)) bound.Add((b, field));
        }

        var callbacks = callbackAttrs.Where(a => attrs.TryGetValue(a.Name, out var x) && x == a).ToList();

        if (bound.Count == 0 && callbacks.Count == 0)
        {
            Line($"var {v} = ui.Mount<{fq}>(");
            if (key != null && CheckExpression(key)) EmitMapped(key.ValueSpan); else Line("0");
            if (state != null)
            {
                Line(",");
                EmitMapped(state.ValueSpan);
            }
            Line(KeepAlive(attrs) ? ", keepAlive: true);" : ");");
            return v;
        }

        // Sync each binding against the child's stored state, then mount (and render) it.
        string k = $"{v}K", c = $"{v}C", id = $"{v}B";
        Line($"int {k} =");
        if (key != null && CheckExpression(key)) EmitMapped(key.ValueSpan); else Line("0");
        Line(";");
        Line($"ref var {c} = ref ui.State<{fq}>({k},");
        if (state != null) { EmitMapped(state.ValueSpan); Line(","); }
        Line($"out int {id});");
        for (int i = 0; i < bound.Count; i++)
        {
            // The call is mapped to the attribute name, so a type mismatch between the two sides
            // (CS0411 on Sync) is reported there; errors inside the expression map to the value.
            EmitMappedText(bound[i].attr.NameSpan.Start, $"ui.Sync({id}, {i}, ref");
            EmitMapped(bound[i].attr.ValueSpan);
            EmitMappedText(bound[i].attr.NameSpan.Start, $", ref {c}.@{bound[i].field});");
        }
        // Callbacks are created in every build (they target this component's current slot).
        foreach (var cb in callbacks)
        {
            var (field, argType) = callbackFields[cb.Name];
            EmitCallback($"{c}.@{field}", argType, cb);
        }
        Line($"var {v} = ui.Mount<{fq}>({k}{(KeepAlive(attrs) ? ", keepAlive: true" : "")});");
        return v;
    }

    /// <summary>keep-alive="true|false" on a component tag, else the enclosing Tabs/DockSpace default.</summary>
    private bool KeepAlive(Dictionary<string, CmlAttributeSyntax> attrs)
    {
        if (!attrs.TryGetValue("keep-alive", out var attr)) return keepAliveDefault;
        switch (attr.Value.Trim())
        {
            case "true": return true;
            case "false": return false;
            default:
                Report(CmlDiagnostics.UnknownAttribute, attr.ValueSpan, attr.Value, "keep-alive", "true, false");
                return keepAliveDefault;
        }
    }

    /// <summary>Null if <paramref name="field"/> can be bound on the child component; otherwise why not.</summary>
    /// <summary>
    /// The component's settable Cosmiria.Callback / Callback&lt;T&gt; fields, by name (case-insensitive),
    /// with the callback's argument type (null for Callback).
    /// </summary>
    private static Dictionary<string, (string field, string argType)> CallbackFields(INamedTypeSymbol type)
    {
        var result = new Dictionary<string, (string, string)>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var m in type.GetMembers())
        {
            if (m is not IFieldSymbol f || f.IsStatic || f.IsReadOnly || f.IsConst) continue;
            if (f.DeclaredAccessibility == Accessibility.Private || f.DeclaredAccessibility == Accessibility.Protected) continue;
            if (f.Type is not INamedTypeSymbol t || t.Name != "Callback" || t.ContainingNamespace?.ToDisplayString() != "Cosmiria") continue;
            string arg = t.TypeArguments.Length == 1 ? t.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null;
            result[f.Name] = (f.Name, arg);
        }
        return result;
    }

    /// <summary>
    /// <c>onpick="Pick(item.Id, $e)"</c> on a component: assign its Callback field a callback that
    /// calls this component's method. <c>$e</c> is the callback's argument; other arguments are
    /// captured now, as in event handlers.
    /// </summary>
    private void EmitCallback(string target, string argType, CmlAttributeSyntax attr)
    {
        if (!ParseHandler(attr, out TextSpan name, out int callPos, out List<(TextSpan span, bool isEvent)> args)) return;
        var captured = args.Where(a => !a.isEvent).ToList();
        bool hasEvent = args.Any(a => a.isEvent);
        if (hasEvent && argType == null)
        {
            Report(CmlDiagnostics.InvalidHandler, attr.ValueSpan, attr.Name, "this callback has no argument, so there is no $e (its field is a Callback, not a Callback<T>)");
            return;
        }
        if (!CheckCaptureCount(attr, captured.Count)) return;

        var call = new StringBuilder();
        int k = 0;
        foreach (var a in args)
        {
            if (call.Length > 0) call.Append(", ");
            if (a.isEvent) call.Append("__e");
            else call.Append(captured.Count == 1 ? "__a" : $"__a.Item{++k}");
        }

        if (AsyncHandler(name, out bool isStatic))
        {
            string self = isStatic ? SelfType + "." : "__h.Snapshot().";
            string withHandle = call.Length > 0 ? "__h, " + call : "__h";
            if (captured.Count == 0)
            {
                Line(argType == null
                    ? $"{target} = ev.Callback(static (global::Cosmiria.ComponentHandle<{SelfType}> __h) => {self}"
                    : $"{target} = ev.Callback(static (global::Cosmiria.ComponentHandle<{SelfType}> __h, {argType} __e) => {self}");
            }
            else
            {
                Line(argType == null ? $"__CmlCbAsync(ev, ref {target}," : $"__CmlCbAsyncA(ev, ref {target},");
                if (captured.Count > 1) Line("(");
                for (int i = 0; i < captured.Count; i++)
                {
                    if (i > 0) Line(",");
                    EmitMapped(captured[i].span);
                }
                if (captured.Count > 1) Line(")");
                Line(argType == null ? $", static (__h, __a) => {self}" : $", static (__h, __e, __a) => {self}");
            }
            EmitMapped(name);
            EmitMappedText(callPos, $"({withHandle}));");
            return;
        }
        if (captured.Count == 0)
        {
            Line(argType == null
                ? $"{target} = ev.Callback(static (ref {SelfType} __self) => __self."
                : $"{target} = ev.Callback(static (ref {SelfType} __self, {argType} __e) => __self.");
        }
        else
        {
            // Helpers fix the component type and take the field by ref, so the argument and
            // capture types are inferred.
            Line(argType == null ? $"__CmlCb(ev, ref {target}," : $"__CmlCbA(ev, ref {target},");
            if (captured.Count > 1) Line("(");
            for (int i = 0; i < captured.Count; i++)
            {
                if (i > 0) Line(",");
                EmitMapped(captured[i].span);
            }
            if (captured.Count > 1) Line(")");
            Line(argType == null ? ", static (ref __self, __a) => __self." : ", static (ref __self, __e, __a) => __self.");
        }
        EmitMapped(name);
        EmitMappedText(callPos, $"({call}));");
    }

    private static string CheckBindableField(INamedTypeSymbol type, string field)
    {
        if (field.Length == 0) return "name the child's field to bind, e.g. bind:Value=\"Total\"";
        var member = type.GetMembers(field).FirstOrDefault(m => !m.IsStatic);
        if (member is IFieldSymbol f)
        {
            if (f.IsReadOnly || f.IsConst) return $"{type.Name}.{field} is read-only";
            if (f.DeclaredAccessibility == Accessibility.Private || f.DeclaredAccessibility == Accessibility.Protected)
                return $"{type.Name}.{field} must be public or internal";
            return null;
        }
        if (member is IPropertySymbol)
            return $"{type.Name}.{field} is a property; bindings sync fields (they are passed by ref)";
        return $"{type.Name} has no instance field named '{field}'";
    }

    /// <summary>Text node from literal/expression parts. Expressions are hoisted into locals so each
    /// one can carry its own #line mapping, then combined through the zero-alloc interpolation handler.</summary>
    private string EmitText(List<CmlTextPart> parts, string style, bool allowEmpty = false)
    {
        // Trim the outer whitespace of the run (inner runs are already collapsed to one space).
        var list = new List<CmlTextPart>(parts);
        int first = list.FindIndex(p => p.IsExpression || p.Literal.Trim().Length > 0);
        int last = list.FindLastIndex(p => p.IsExpression || p.Literal.Trim().Length > 0);
        if (first < 0)
        {
            if (!allowEmpty) return null;
            list.Clear();
        }
        else
        {
            list = list.GetRange(first, last - first + 1);
            if (!list[0].IsExpression) list[0] = CmlTextPart.Text(list[0].Literal.TrimStart());
            if (!list[list.Count - 1].IsExpression) list[list.Count - 1] = CmlTextPart.Text(list[list.Count - 1].Literal.TrimEnd());
        }

        bool hasExpr = list.Exists(p => p.IsExpression);
        var holes = new List<string>();
        foreach (var part in list)
        {
            if (!part.IsExpression) continue;
            string h = $"__cmlH{holeId++}";
            holes.Add(h);
            Line($"var {h} =");
            EmitMapped(part.Expression);
            Line(";");
        }

        string v = NewNode();
        if (!hasExpr)
        {
            var sb = new StringBuilder();
            foreach (var p in list) sb.Append(p.Literal);
            Line($"var {v} = ui.Text({StringLiteral(sb.ToString())});");
        }
        else
        {
            var sb = new StringBuilder("$\"");
            int hole = 0;
            foreach (var p in list)
            {
                if (p.IsExpression) sb.Append('{').Append(holes[hole++]).Append('}');
                else sb.Append(EscapeInterpolated(p.Literal));
            }
            sb.Append('"');
            Line($"var {v} = ui.Text({sb});");
        }
        return v;
    }

    /// <summary>A runtime style part: prefix, the user's expression (mapped to the .cml), suffix.</summary>
    private void EmitDynamic(DynamicStyle part)
    {
        if (part.Prefix.Length > 0) Line(part.Prefix);
        EmitMapped(part.Span);
        if (part.Suffix.Length > 0) Line(part.Suffix);
    }

    /// <summary>
    /// hover:/active: styles: cached when static; with [@value] parts, built at runtime as the
    /// static chain merged with each dynamic part.
    /// </summary>
    private void EmitStateStyle(string node, string method, StringBuilder chain, List<DynamicStyle> dynamic)
    {
        if (dynamic.Count == 0)
        {
            string field = StyleField(chain);
            if (field != null) Line($"{node}.{method}({field});");
            return;
        }
        Line($"{node}.{method}(default(global::Cosmiria.Style){chain}");
        foreach (var part in dynamic)
        {
            Line(".MergedWith(");
            EmitDynamic(part);
            Line(")");
        }
        Line(");");
    }

    private void ApplyCommon(string v, Dictionary<string, CmlAttributeSyntax> attrs, CompiledStyle style)
    {
        if (attrs.TryGetValue("tooltip", out var tip))
        {
            // tooltip="Save the file" (text), or tooltip="@Expr" / "@(expr)" (a C# string).
            Line($"{v}.Tooltip(");
            EmitTextOrExpression(tip);
            Line(");");
        }
        if (attrs.TryGetValue("focusable", out var focusable))
        {
            switch (focusable.Value.Trim())
            {
                case "true": Line($"{v}.Focusable();"); break;
                case "false": break;
                default: Report(CmlDiagnostics.UnknownAttribute, focusable.ValueSpan, focusable.Value, "focusable", "true, false"); break;
            }
        }
        if (style != null)
        {
            foreach (var dyn in style.Dynamic)
            {
                Line($"{v}.Styled(");
                EmitDynamic(dyn);
                Line(");");
            }
            EmitStateStyle(v, "Hover", style.Hover, style.HoverDynamic);
            EmitStateStyle(v, "Active", style.Active, style.ActiveDynamic);
        }

        foreach (var pair in attrs)
        {
            if (pair.Key == "ontoggle" || pair.Key == "ondismiss") continue; // ComboBox-only, emitted there
            if (EventMethods.TryGetValue(pair.Key, out string method)) EmitHandler(v, method, pair.Value);
        }
        if (attrs.TryGetValue("ref", out var handle) && CheckExpression(handle))
        {
            // ref="NameBox": a field of this component is created at the top of Render, so it's
            // usable from the first build even while its element isn't shown. Another path
            // (item.Box) is created here, on first use; any other expression (a method call, an
            // indexer) must give an existing ElementRef.
            string path = handle.Value.Trim();
            bool assignable = System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)*$");
            bool field = assignable && path.IndexOf('.') < 0 && SelfSymbol != null && IsSelfMember(path);
            if (field) refFields.Add(path);
            Line($"{v}.Ref(");
            EmitMapped(handle.ValueSpan);
            Line(assignable && !field ? " ??= new global::Cosmiria.ElementRef());" : ");");
        }
        if (attrs.TryGetValue("id", out var id) && CheckExpression(id))
        {
            Line($"{v}.Id(");
            EmitMapped(id.ValueSpan);
            Line(");");
        }
    }

    private string SelfType => "global::" + (string.IsNullOrEmpty(target.Namespace) ? "" : target.Namespace + ".") + target.Name;

    /// <summary>
    /// onclick="Method" / "Method(a, b)" / "Method(i, $e)". Arguments are evaluated at render time
    /// and stored with the listener (no boxing); <c>$e</c> is the <see cref="T:Cosmiria.PointerEvent"/>
    /// at event time. The method is called on the component's stored state (by ref).
    /// </summary>
    private void EmitHandler(string node, string method, CmlAttributeSyntax attr)
    {
        if (!ParseHandler(attr, out TextSpan name, out int callPos, out List<(TextSpan span, bool isEvent)> args)) return;

        var captured = args.Where(a => !a.isEvent).ToList();
        bool hasEvent = args.Any(a => a.isEvent);
        if (!CheckCaptureCount(attr, captured.Count)) return;

        // Argument list of the call made inside the handler.
        var call = new StringBuilder();
        int k = 0;
        foreach (var a in args)
        {
            if (call.Length > 0) call.Append(", ");
            if (a.isEvent) call.Append("__e");
            else call.Append(captured.Count == 1 ? "__a" : $"__a.Item{++k}");
        }

        Line($"{node}.{method}(");
        if (AsyncHandler(name, out bool isStatic))
        {
            // An async method taking ComponentHandle<Self> first: an async listener. The method runs
            // on a copy of the state as it is now (its `this`) and writes back through the handle.
            string target = isStatic ? SelfType + "." : "__h.Snapshot().";
            string withHandle = call.Length > 0 ? "__h, " + call : "__h";
            if (captured.Count == 0)
            {
                Line(hasEvent
                    ? $"ev.AsyncListener(static (global::Cosmiria.ComponentHandle<{SelfType}> __h, global::Cosmiria.PointerEvent __e) => {target}"
                    : $"ev.AsyncListener(static (global::Cosmiria.ComponentHandle<{SelfType}> __h) => {target}");
            }
            else
            {
                Line(hasEvent ? "__CmlAsyncE(ev," : "__CmlAsync(ev,");
                if (captured.Count > 1) Line("(");
                for (int i = 0; i < captured.Count; i++)
                {
                    if (i > 0) Line(",");
                    EmitMapped(captured[i].span);
                }
                if (captured.Count > 1) Line(")");
                Line(hasEvent ? $", static (__h, __e, __a) => {target}" : $", static (__h, __a) => {target}");
            }
            EmitMapped(name);
            EmitMappedText(callPos, $"({withHandle})));");
            return;
        }
        if (captured.Count == 0)
        {
            Line(hasEvent
                ? $"ev.Listener(static (ref {SelfType} __self, in global::Cosmiria.PointerEvent __e) => __self."
                : $"ev.Listener(static (ref {SelfType} __self) => __self.");
        }
        else
        {
            Line(hasEvent ? "__CmlOnE(ev," : "__CmlOn(ev,");
            if (captured.Count > 1) Line("(");
            for (int i = 0; i < captured.Count; i++)
            {
                if (i > 0) Line(",");
                EmitMapped(captured[i].span);
            }
            if (captured.Count > 1) Line(")");
            Line(hasEvent ? ", static (ref __self, in __e, __a) => __self." : ", static (ref __self, __a) => __self.");
        }
        EmitMapped(name);
        // Map the generated argument list onto the handler's '(' so argument errors
        // (wrong type, wrong count) point at the user's arguments in the .cml file.
        EmitMappedText(callPos, $"({call})));");
    }

    // ================================================================== bind:

    /// <summary>
    /// A bind: target: a member path on the component, like <c>Agree</c>, <c>Settings.Dark</c> or
    /// <c>Rows[i].Done</c>. Index expressions are captured at render time (like handler arguments),
    /// so the generated listener can write the same element back on the component's stored state.
    /// </summary>
    private sealed class BindPath
    {
        public CmlAttributeSyntax Attr;
        public string Name => Attr.Name;
        public readonly List<TextSpan> Indexes = new();
        /// <summary>Path text around the index expressions (Indexes.Count + 1 pieces).</summary>
        public readonly List<string> Pieces = new();

        public string Write(Captures c)
        {
            var sb = new StringBuilder("__self.");
            int offset = c.Offsets[this];
            for (int i = 0; i < Pieces.Count; i++)
            {
                sb.Append(Pieces[i]);
                if (i < Indexes.Count) sb.Append(c.Names[offset + i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>A value captured by a generated listener: user source (mapped) or generated code.</summary>
    private readonly struct Capture
    {
        public readonly TextSpan Span;
        public readonly string Code;
        private Capture(TextSpan span, string code) { Span = span; Code = code; }
        public static implicit operator Capture(TextSpan span) => new(span, null);
        public static Capture Generated(string code) => new(default, code);
    }

    /// <summary>Names of the captured values inside a generated listener.</summary>
    private sealed class Captures
    {
        public string[] Names;
        public string[] Extra;
        public readonly Dictionary<BindPath, int> Offsets = new();
    }

    private BindPath ParseBindPath(CmlAttributeSyntax attr)
    {
        if (!CheckExpression(attr)) return null;
        string v = attr.Value;
        int start = attr.ValueSpan.Start;
        int end = v.Length;
        while (end > 0 && char.IsWhiteSpace(v[end - 1])) end--;
        int i = 0;
        while (i < end && char.IsWhiteSpace(v[i])) i++;

        const string Shape = "bind to a field or property of the component, like 'Agree', 'Settings.Dark' or 'Rows[i].Done'";
        var path = new BindPath { Attr = attr };
        var piece = new StringBuilder();
        string root = null;
        bool expectName = true;
        while (i < end)
        {
            char c = v[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (expectName)
            {
                if (!(char.IsLetter(c) || c == '_'))
                {
                    Report(CmlDiagnostics.InvalidBinding, new TextSpan(start + i, 1), attr.Name, $"expected a member name at '{c}'; {Shape}");
                    return null;
                }
                int n0 = i;
                while (i < end && (char.IsLetterOrDigit(v[i]) || v[i] == '_')) i++;
                string name = v.Substring(n0, i - n0);
                if (root == null)
                {
                    root = name;
                    if (!IsSelfMember(name))
                    {
                        Report(CmlDiagnostics.InvalidBinding, new TextSpan(start + n0, name.Length), attr.Name,
                            $"'{name}' is not an instance field or property of {target.Name}. Locals such as loop variables can't be bound; " +
                            "bind the component's own state instead, indexing it with the local if needed (Rows[i].Done)");
                        return null;
                    }
                }
                piece.Append(name);
                expectName = false;
            }
            else if (c == '.')
            {
                piece.Append('.');
                i++;
                expectName = true;
            }
            else if (c == '[')
            {
                int depth = 0, a = i + 1, j = i;
                for (; j < end; j++)
                {
                    char d = v[j];
                    if (d == '"' || d == '\'')
                    {
                        for (j++; j < end && v[j] != d; j++) if (v[j] == '\\') j++;
                        continue;
                    }
                    if (d == '[' || d == '(' || d == '{') depth++;
                    else if (d == ']' || d == ')' || d == '}') { if (--depth == 0) break; }
                }
                if (j >= end)
                {
                    Report(CmlDiagnostics.InvalidBinding, new TextSpan(start + i, 1), attr.Name, "missing ']'");
                    return null;
                }
                int b = j;
                while (a < b && char.IsWhiteSpace(v[a])) a++;
                while (b > a && char.IsWhiteSpace(v[b - 1])) b--;
                if (a == b)
                {
                    Report(CmlDiagnostics.InvalidBinding, new TextSpan(start + i, j - i + 1), attr.Name, "empty index");
                    return null;
                }
                piece.Append('[');
                path.Pieces.Add(piece.ToString());
                piece.Clear();
                path.Indexes.Add(new TextSpan(start + a, b - a));
                piece.Append(']');
                i = j + 1;
            }
            else
            {
                Report(CmlDiagnostics.InvalidBinding, new TextSpan(start + i, end - i), attr.Name, $"unexpected '{c}'; {Shape}");
                return null;
            }
        }
        if (expectName)
        {
            Report(CmlDiagnostics.InvalidBinding, attr.ValueSpan, attr.Name, $"expected a member name; {Shape}");
            return null;
        }
        path.Pieces.Add(piece.ToString());
        return path;
    }

    private INamedTypeSymbol SelfSymbol
    {
        get
        {
            if (!selfResolved)
            {
                selfResolved = true;
                selfSymbol = compilation.GetTypeByMetadataName(
                    string.IsNullOrEmpty(target.Namespace) ? target.Name : target.Namespace + "." + target.Name);
            }
            return selfSymbol;
        }
    }

    /// <summary>
    /// The handler names a method of the component that returns a task and takes a
    /// <c>ComponentHandle&lt;Self&gt;</c> first: it's bound as an async listener (or callback), and
    /// the handle is passed before the attribute's arguments.
    /// </summary>
    private bool AsyncHandler(TextSpan name, out bool isStatic)
    {
        isStatic = false;
        INamedTypeSymbol self = SelfSymbol;
        if (self == null) return false;
        string methodName = text.ToString(name).Trim();
        foreach (var m in self.GetMembers(methodName).OfType<IMethodSymbol>())
        {
            if (m.Parameters.Length == 0) continue;
            if (!(m.Parameters[0].Type is INamedTypeSymbol p && p.Name == "ComponentHandle" && p.ContainingNamespace?.ToDisplayString() == "Cosmiria")) continue;
            if (!(m.ReturnType is INamedTypeSymbol r && r.Name == "Task" &&
                  r.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks")) continue;
            isStatic = m.IsStatic;
            return true;
        }
        return false;
    }

    private bool IsSelfMember(string name)
    {
        INamedTypeSymbol self = SelfSymbol;
        if (self == null) return true; // can't tell; let the C# compiler decide
        return self.GetMembers(name).Any(m => !m.IsStatic && m is IFieldSymbol or IPropertySymbol);
    }

    /// <summary>
    /// Attach a generated listener that writes a bound path. Captured values: the index expressions
    /// of <paramref name="bind"/>, then <paramref name="extra"/>, then the indexes of <paramref name="also"/>.
    /// </summary>
    private void EmitBoundListener(string node, string method, BindPath bind, Capture[] extra, System.Func<Captures, string> body,
        BindPath also = null, bool pointer = false)
    {
        var spans = new List<Capture>();
        var captures = new Captures();
        captures.Offsets[bind] = 0;
        spans.AddRange(bind.Indexes.Select(i => (Capture)i));
        int extraStart = spans.Count;
        if (extra != null) spans.AddRange(extra);
        if (also != null)
        {
            captures.Offsets[also] = spans.Count;
            spans.AddRange(also.Indexes.Select(i => (Capture)i));
        }
        if (!CheckCaptureCount(bind.Attr, spans.Count)) return;

        captures.Names = new string[spans.Count];
        for (int i = 0; i < spans.Count; i++) captures.Names[i] = spans.Count == 1 ? "__a" : $"__a.Item{i + 1}";
        captures.Extra = captures.Names.Skip(extraStart).Take(extra?.Length ?? 0).ToArray();

        Line($"{node}.{method}(");
        if (spans.Count == 0)
        {
            Line(pointer
                ? $"ev.Listener(static (ref {SelfType} __self, in global::Cosmiria.PointerEvent __e) =>"
                : $"ev.Listener(static (ref {SelfType} __self) =>");
        }
        else
        {
            Line(pointer ? "__CmlOnE(ev," : "__CmlOn(ev,");
            if (spans.Count > 1) Line("(");
            for (int i = 0; i < spans.Count; i++)
            {
                if (i > 0) Line(",");
                if (spans[i].Code != null) Line(spans[i].Code); else EmitMapped(spans[i].Span);
            }
            if (spans.Count > 1) Line(")");
            Line(pointer ? ", static (ref __self, in __e, __a) =>" : ", static (ref __self, __a) =>");
        }
        // Errors in the generated assignment (read-only member, wrong value type) point at the binding.
        EmitMappedText(bind.Attr.ValueSpan.Start, body(captures) + "));");
    }

    private bool CheckCaptureCount(CmlAttributeSyntax attr, int count)
    {
        if (count > MaxHandlerArgs)
        {
            Report(CmlDiagnostics.InvalidHandler, attr.ValueSpan, attr.Name, $"At most {MaxHandlerArgs} arguments are supported; pass a struct for more.");
            return false;
        }
        if (count > 0 && (int)((Microsoft.CodeAnalysis.CSharp.CSharpCompilation)compilation).LanguageVersion < 1400)
        {
            Report(CmlDiagnostics.LanguageVersionTooLow, attr.ValueSpan,
                ((Microsoft.CodeAnalysis.CSharp.CSharpCompilation)compilation).LanguageVersion.ToString());
            return false;
        }
        return true;
    }

    /// <summary>Split "Name(arg, arg)" into the method name and top-level argument spans.</summary>
    private bool ParseHandler(CmlAttributeSyntax attr, out TextSpan name, out int callPos, out List<(TextSpan span, bool isEvent)> args)
    {
        args = new List<(TextSpan, bool)>();
        name = default;
        callPos = attr.ValueSpan.Start;
        string v = attr.Value;
        int start = attr.ValueSpan.Start;
        int i = 0;
        while (i < v.Length && char.IsWhiteSpace(v[i])) i++;
        if (i >= v.Length)
        {
            Report(CmlDiagnostics.EmptyExpression, attr.NameSpan, $"The '{attr.Name}' handler");
            return false;
        }
        if (!CheckNoEntities(attr)) return false;
        int n0 = i;
        if (!(char.IsLetter(v[i]) || v[i] == '_'))
        {
            Report(CmlDiagnostics.InvalidHandler, attr.ValueSpan, attr.Name, "Expected a method name.");
            return false;
        }
        while (i < v.Length && (char.IsLetterOrDigit(v[i]) || v[i] == '_')) i++;
        name = new TextSpan(start + n0, i - n0);
        callPos = name.End;
        while (i < v.Length && char.IsWhiteSpace(v[i])) i++;
        if (i == v.Length) return true; // "Method"

        if (v[i] != '(')
        {
            Report(CmlDiagnostics.InvalidHandler, new TextSpan(start + i, 1), attr.Name, $"Unexpected '{v[i]}'.");
            return false;
        }

        callPos = start + i;
        // Scan arguments, splitting on top-level commas (skipping strings/chars/brackets).
        i++;
        int argStart = i, depth = 0;
        bool closed = false;
        for (; i < v.Length; i++)
        {
            char c = v[i];
            if (c == '"' || c == '\'')
            {
                char q = c;
                for (i++; i < v.Length && v[i] != q; i++) if (v[i] == '\\') i++;
                continue;
            }
            if (c == '(' || c == '[' || c == '{') depth++;
            else if ((c == ')' || c == ']' || c == '}') && depth > 0) depth--;
            else if (c == ')' && depth == 0)
            {
                if (!AddArg(attr, start, argStart, i, args, allowEmpty: args.Count == 0)) return false;
                closed = true;
                i++;
                break;
            }
            else if (c == ',' && depth == 0)
            {
                if (!AddArg(attr, start, argStart, i, args, allowEmpty: false)) return false;
                argStart = i + 1;
            }
        }
        if (!closed)
        {
            Report(CmlDiagnostics.InvalidHandler, attr.ValueSpan, attr.Name, "Missing ')'.");
            return false;
        }
        while (i < v.Length && char.IsWhiteSpace(v[i])) i++;
        if (i < v.Length)
        {
            Report(CmlDiagnostics.InvalidHandler, new TextSpan(start + i, v.Length - i), attr.Name, "Unexpected text after ')'.");
            return false;
        }
        return true;
    }

    private bool AddArg(CmlAttributeSyntax attr, int valueStart, int from, int to, List<(TextSpan, bool)> args, bool allowEmpty)
    {
        string v = attr.Value;
        int a = from, b = to;
        while (a < b && char.IsWhiteSpace(v[a])) a++;
        while (b > a && char.IsWhiteSpace(v[b - 1])) b--;
        if (a == b)
        {
            if (allowEmpty) return true; // "Method()"
            Report(CmlDiagnostics.InvalidHandler, new TextSpan(valueStart + from, System.Math.Max(1, to - from)), attr.Name, "Empty argument.");
            return false;
        }
        string text = v.Substring(a, b - a);
        if (text.StartsWith("$") && text != "$e")
        {
            Report(CmlDiagnostics.InvalidHandler, new TextSpan(valueStart + a, b - a), attr.Name, $"Unknown special argument '{text}'. Use $e for the pointer event.");
            return false;
        }
        args.Add((new TextSpan(valueStart + a, b - a), text == "$e"));
        return true;
    }

    private void EmitUsing(CmlElement u)
    {
        var attrs = CheckAttributes(u, new[] { "namespace" });
        foreach (var child in u.Children)
        {
            if (child is CmlText t && IsBlank(t)) continue;
            Report(CmlDiagnostics.ContentNotAllowed, ChildSpan(child), "<using> cannot have content");
        }
        if (!attrs.TryGetValue("namespace", out var ns))
        {
            Report(CmlDiagnostics.MissingAttribute, u.NameSpan, "using", "namespace");
            return;
        }
        string name = ns.Value.Trim();
        if (name.Length == 0)
        {
            Report(CmlDiagnostics.EmptyExpression, ns.ValueSpan, "The namespace");
            return;
        }
        usingNamespaces.Add(name);

        // Place the namespace at its original column so CS0246 points into the .cml file.
        var lp = text.Lines.GetLinePosition(ns.ValueSpan.Start);
        usings.Append("#line ").Append(lp.Line + 1).Append(" \"").Append(linePath).AppendLine("\"");
        int pad = lp.Character - "using ".Length;
        if (pad > 0) usings.Append(' ', pad);
        usings.Append("using ").Append(ns.Value).AppendLine(";");
    }

    // ================================================================== binding helpers

    private Dictionary<string, CmlAttributeSyntax> CheckAttributes(CmlElement e, string[] allowed)
    {
        var result = new Dictionary<string, CmlAttributeSyntax>();
        foreach (var attr in e.Attributes)
        {
            if (System.Array.IndexOf(allowed, attr.Name) < 0)
            {
                Report(CmlDiagnostics.UnknownAttribute, attr.NameSpan, attr.Name, e.Name, string.Join(", ", allowed));
                continue;
            }
            if (result.ContainsKey(attr.Name))
            {
                Report(CmlDiagnostics.DuplicateAttribute, attr.NameSpan, attr.Name);
                continue;
            }
            result.Add(attr.Name, attr);
        }
        return result;
    }

    private bool CheckExpression(CmlAttributeSyntax attr)
    {
        if (attr.Value.Trim().Length == 0)
        {
            Report(CmlDiagnostics.EmptyExpression, attr.NameSpan, $"The '{attr.Name}' expression");
            return false;
        }
        return CheckNoEntities(attr);
    }

    private static readonly string[] Entities = { "&quot;", "&apos;", "&amp;", "&lt;", "&gt;" };

    /// <summary>C# attribute values are raw source; entities would reach the C# compiler verbatim.</summary>
    private bool CheckNoEntities(CmlAttributeSyntax attr)
    {
        foreach (string entity in Entities)
        {
            int i = attr.Value.IndexOf(entity, System.StringComparison.Ordinal);
            if (i >= 0)
            {
                Report(CmlDiagnostics.EntityInExpression, new TextSpan(attr.ValueSpan.Start + i, entity.Length), entity);
                return false;
            }
        }
        return true;
    }

    private CompiledStyle CompileStyle(Dictionary<string, CmlAttributeSyntax> attrs)
    {
        if (!attrs.TryGetValue("style", out var attr)) return null;
        return StyleCompiler.Compile(attr.Value, attr.ValueSpan.Start, path, text, diagnostics);
    }

    /// <summary>Hoist a constant style chain into the cached style array (built once per version).</summary>
    private string StyleField(StringBuilder chain)
    {
        if (chain == null || chain.Length == 0) return null;
        int index = styleId++;
        fields.Append("            /* ").Append(index).Append(" */ default(global::Cosmiria.Style)").Append(chain).AppendLine(",");
        return $"__cmlS[{index}]";
    }

    private INamedTypeSymbol ResolveComponent(string name, out string why)
    {
        why = null;
        var candidates = new List<string>();
        if (name.Contains(".")) candidates.Add(name);

        // C#-like lookup order: the component's namespace and its parents, then <using>s, then Cosmiria.
        string ns = target.Namespace ?? "";
        while (true)
        {
            candidates.Add(ns.Length == 0 ? name : ns + "." + name);
            if (ns.Length == 0) break;
            int dot = ns.LastIndexOf('.');
            ns = dot < 0 ? "" : ns.Substring(0, dot);
        }
        foreach (var u in usingNamespaces) candidates.Add(u + "." + name);
        candidates.Add("Cosmiria." + name);

        foreach (var metadataName in candidates)
        {
            var type = compilation.GetTypeByMetadataName(metadataName);
            if (type == null) continue;

            if (type.TypeKind != TypeKind.Struct) { why = "components must be structs"; return null; }
            if (type.IsGenericType) { why = "generic components are not supported"; return null; }
            if (type.IsRefLikeType) { why = "ref structs cannot be components"; return null; }
            bool isComponent = componentInterface != null && type.AllInterfaces.Contains(componentInterface, SymbolEqualityComparer.Default);
            bool isCml = type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Cosmiria.CmlAttribute");
            if (!isComponent && !isCml) { why = "it does not implement Cosmiria.IComponent"; return null; }
            return type;
        }
        return null;
    }

    // ================================================================== output helpers

    private void Attach(string parent, string child)
    {
        if (parent != null) Line($"{parent}.Child({child});");
    }

    private string NewNode() => $"__cmlN{nodeId++}";

    private void Line(string code)
    {
        body.Append(' ', indent * 4).AppendLine(code);
    }

    /// <summary>
    /// Emit source text verbatim at its original line/column behind '#line', so compiler errors
    /// inside it map back to the .cml file. Multi-line spans keep their original layout.
    /// </summary>
    private void EmitMapped(TextSpan span)
    {
        var lp = text.Lines.GetLinePosition(span.Start);
        body.Append("#line ").Append(lp.Line + 1).Append(" \"").Append(linePath).AppendLine("\"");
        body.Append(' ', lp.Character);
        body.Append(src, span.Start, span.Length);
        body.AppendLine();
        body.AppendLine("#line hidden");
    }

    /// <summary>Emit generated text positioned at a source location (line + column).</summary>
    private void EmitMappedText(int sourcePos, string code)
    {
        var lp = text.Lines.GetLinePosition(sourcePos);
        body.Append("#line ").Append(lp.Line + 1).Append(" \"").Append(linePath).AppendLine("\"");
        body.Append(' ', lp.Character).AppendLine(code);
        body.AppendLine("#line hidden");
    }

    private static bool IsBlank(CmlText t) => t.Parts.TrueForAll(p => !p.IsExpression && p.Literal.Trim().Length == 0);

    private static TextSpan ChildSpan(CmlNode node) => node is CmlElement e ? e.NameSpan : node.Span;

    private static string StringLiteral(string s) => "\"" + Escape(s) + "\"";

    private static string EscapeInterpolated(string s) => Escape(s).Replace("{", "{{").Replace("}", "}}");

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private void Report(DiagnosticDescriptor descriptor, TextSpan span, params object[] args) =>
        diagnostics.Add(CmlLocation.Create(descriptor, path, text, span, args));
}
