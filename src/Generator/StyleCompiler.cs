using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Cosmiria.Generator;

/// <summary>
/// A style part evaluated at runtime: <c>Prefix + (user expression) + Suffix</c> is a C# Style
/// expression. A bare <c>@expr</c> token has empty prefix/suffix; an arbitrary C# value like
/// <c>translate-x-[@Offset]</c> wraps the expression in the utility's builder call.
/// </summary>
internal sealed class DynamicStyle
{
    public string Prefix = "", Suffix = "";
    public TextSpan Span;
}

/// <summary>Compiled form of a style attribute.</summary>
internal sealed class CompiledStyle
{
    public readonly StringBuilder Base = new();
    public readonly StringBuilder Hover = new();
    public readonly StringBuilder Active = new();
    /// <summary>Styles evaluated at runtime (@expr tokens, [@value] utilities), merged over Base.</summary>
    public readonly List<DynamicStyle> Dynamic = new();
    /// <summary>[@value] utilities under hover: / active: (their state style is then built at runtime).</summary>
    public readonly List<DynamicStyle> HoverDynamic = new();
    public readonly List<DynamicStyle> ActiveDynamic = new();
}

/// <summary>
/// Compiles Tailwind-like utility strings into Cosmiria <c>Style</c> fluent chains at build time.
/// Numbers are pixels (<c>p-8</c> = 8px). Prefix <c>hover:</c> / <c>active:</c> for state styles.
/// <c>@expr</c> / <c>@(expr)</c> tokens are C# <c>Style</c> values merged at runtime.
/// </summary>
internal static class StyleCompiler
{
    private const string LengthType = "global::Cosmiria.Length";

    public static readonly string[] Utilities =
    {
        "flex", "hidden", "flex-row", "flex-col", "flex-wrap", "flex-nowrap", "flex-1", "grow[-N]", "shrink[-N]",
        "justify-{start|center|end|between|around|evenly}", "items-{start|center|end|stretch}", "self-{start|center|end|stretch}",
        "gap-N", "gap-x-N", "gap-y-N", "p|px|py|pt|pb|pl|pr-N", "m|mx|my|mt|mb|ml|mr-N",
        "w|h|size|min-w|min-h|max-w|max-h-{N|N%|full|auto}", "relative", "absolute", "fixed", "inset|top|left|right|bottom-N",
        "overflow-{hidden|scroll}", "bg-COLOR", "border[-N]", "border-COLOR", "rounded[-N|-full|-none]", "shadow[-sm|-md|-lg|-xl|-2xl|-none]", "shadow-COLOR",
        "text-{left|center|right}", "text-{wrap|nowrap}", "text-N", "text-{xs|sm|base|lg|xl|2xl|3xl|4xl}", "text-COLOR", "font-N",
        "bg-gradient-to-{t|tr|r|br|b|bl|l|tl}", "bg-linear-{N|to-DIR}", "bg-radial", "from-COLOR", "via-COLOR", "to-COLOR",
        "transition[-all|-colors|-opacity|-transform|-none]", "duration-N", "delay-N", "ease-{linear|in|out|in-out}", "opacity-N",
        "[-]translate-{x|y}-{N|N%|full}", "[-]scale[-x|-y]-N", "[-]rotate-N", "origin-{center|top|top-right|right|bottom-right|bottom|bottom-left|left|top-left}",
        "blur[-none|-sm|-md|-lg|-xl|-2xl|-3xl|-N]", "backdrop-blur[-none|-sm|-md|-lg|-xl|-2xl|-3xl|-N]",
        "grid", "grid-cols-N", "grid-cols-fill-N", "grid-cols-fit-N", "grid-rows-N", "col-span-{N|full}", "row-span-{N|full}", "col-start-N", "row-start-N",
    };

    // Tailwind's font-size scale, in pixels.
    private static readonly (string name, string method)[] TextSizes =
    {
        ("xs", ".TextXs()"), ("sm", ".TextSm()"), ("base", ".TextBase()"), ("lg", ".TextLg()"),
        ("xl", ".TextXl()"), ("2xl", ".Text2Xl()"), ("3xl", ".Text3Xl()"), ("4xl", ".Text4Xl()"),
    };

    private static readonly (string name, float x, float y)[] Origins =
    {
        ("center", 0.5f, 0.5f), ("top", 0.5f, 0f), ("top-right", 1f, 0f), ("right", 1f, 0.5f), ("bottom-right", 1f, 1f),
        ("bottom", 0.5f, 1f), ("bottom-left", 0f, 1f), ("left", 0f, 0.5f), ("top-left", 0f, 0f),
    };

    public static CompiledStyle Compile(string value, int valueStart, string path, SourceText text, List<Diagnostic> diagnostics)
    {
        var result = new CompiledStyle();
        int i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
            if (i >= value.Length) break;
            int start = i;

            if (value[i] == '@')
            {
                i = ScanExpressionToken(value, i, out TextSpan exprSpan);
                var abs = new TextSpan(valueStart + exprSpan.Start, exprSpan.Length);
                if (exprSpan.Length == 0)
                    diagnostics.Add(CmlLocation.Create(CmlDiagnostics.EmptyExpression, path, text, new TextSpan(valueStart + start, i - start), "A style expression"));
                else
                    result.Dynamic.Add(new DynamicStyle { Span = abs });
                continue;
            }

            // A token ends at whitespace outside brackets/parentheses: translate-x-[@(a + b)] is one token.
            for (int depth = 0; i < value.Length && (depth > 0 || !char.IsWhiteSpace(value[i])); i++)
            {
                if (value[i] == '[' || value[i] == '(') depth++;
                else if ((value[i] == ']' || value[i] == ')') && depth > 0) depth--;
            }
            string token = value.Substring(start, i - start);
            var span = new TextSpan(valueStart + start, token.Length);

            StringBuilder target = result.Base;
            List<DynamicStyle> dynamicTarget = result.Dynamic;
            string utility = token;
            int utilityOffset = 0;
            if (token.StartsWith("hover:")) { target = result.Hover; dynamicTarget = result.HoverDynamic; utility = token.Substring(6); utilityOffset = 6; }
            else if (token.StartsWith("active:")) { target = result.Active; dynamicTarget = result.ActiveDynamic; utility = token.Substring(7); utilityOffset = 7; }

            string error;
            string code;
            if (IsArbitrary(utility, out string name, out string inner, out bool negative, out int innerStart))
            {
                if (inner.StartsWith("@"))
                {
                    // A C# value: becomes a Style built at runtime around the (mapped) expression.
                    error = ArbitraryDynamic(name, inner, negative, out DynamicStyle part, out int exprStart, out int exprLength);
                    if (error == null)
                    {
                        part.Span = new TextSpan(valueStart + start + utilityOffset + innerStart + exprStart, exprLength);
                        dynamicTarget.Add(part);
                        continue;
                    }
                    code = null;
                }
                else error = ArbitraryLiteral(name, inner, negative, out code);
            }
            else if (utility.Contains("-[") && !utility.EndsWith("]"))
            {
                error = "units go inside the brackets, e.g. w-[50%], rotate-[@Angle], duration-[@(t)ms]";
                code = null;
            }
            else error = Translate(utility, out code);
            if (error == null)
            {
                target.Append(code);
            }
            else if (error.Length == 0)
            {
                diagnostics.Add(CmlLocation.Create(CmlDiagnostics.UnknownUtility, path, text, span, token));
            }
            else
            {
                diagnostics.Add(CmlLocation.Create(CmlDiagnostics.InvalidUtilityValue, path, text, span, token, error));
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ arbitrary values: name-[value]

    private enum ValueKind { Length, Float, Color, TextSizeOrColor, BorderWidthOrColor, Fraction, Milliseconds, Factor }

    /// <summary>Utilities that take [arbitrary] values: name (with its trailing dash), builder, kind, negatable.</summary>
    private static readonly (string name, string method, ValueKind kind, bool negatable)[] ArbitraryUtilities =
    {
        ("min-w-", ".MinW", ValueKind.Length, false), ("min-h-", ".MinH", ValueKind.Length, false),
        ("max-w-", ".MaxW", ValueKind.Length, false), ("max-h-", ".MaxH", ValueKind.Length, false),
        ("size-", ".Size", ValueKind.Length, false), ("w-", ".W", ValueKind.Length, false), ("h-", ".H", ValueKind.Length, false),
        ("inset-", ".Inset", ValueKind.Length, true), ("top-", ".Top", ValueKind.Length, true), ("left-", ".Left", ValueKind.Length, true),
        ("right-", ".Right", ValueKind.Length, true), ("bottom-", ".Bottom", ValueKind.Length, true),
        ("translate-x-", ".TranslateXOf", ValueKind.Length, true), ("translate-y-", ".TranslateYOf", ValueKind.Length, true),
        ("gap-x-", ".GapX", ValueKind.Float, false), ("gap-y-", ".GapY", ValueKind.Float, false), ("gap-", ".Gap", ValueKind.Float, false),
        ("px-", ".Px", ValueKind.Float, false), ("py-", ".Py", ValueKind.Float, false), ("pt-", ".Pt", ValueKind.Float, false),
        ("pb-", ".Pb", ValueKind.Float, false), ("pl-", ".Pl", ValueKind.Float, false), ("pr-", ".Pr", ValueKind.Float, false),
        ("p-", ".Padding", ValueKind.Float, false),
        ("mx-", ".Mx", ValueKind.Float, true), ("my-", ".My", ValueKind.Float, true), ("mt-", ".Mt", ValueKind.Float, true),
        ("mb-", ".Mb", ValueKind.Float, true), ("ml-", ".Ml", ValueKind.Float, true), ("mr-", ".Mr", ValueKind.Float, true),
        ("m-", ".Margin", ValueKind.Float, true),
        ("rounded-", ".Rounded", ValueKind.Float, false), ("grow-", ".Grow", ValueKind.Float, false), ("shrink-", ".Shrink", ValueKind.Float, false),
        ("rotate-", ".Rotate", ValueKind.Float, true), ("bg-linear-", ".LinearGradient", ValueKind.Float, true),
        ("scale-x-", ".ScaleXOf", ValueKind.Factor, true), ("scale-y-", ".ScaleYOf", ValueKind.Factor, true), ("scale-", ".Scale", ValueKind.Factor, true),
        ("opacity-", ".Opacity", ValueKind.Fraction, false),
        ("backdrop-blur-", ".BackdropBlur", ValueKind.Float, false), ("blur-", ".Blur", ValueKind.Float, false),
        ("duration-", ".Duration", ValueKind.Milliseconds, false), ("delay-", ".Delay", ValueKind.Milliseconds, false),
        ("bg-", ".Bg", ValueKind.Color, false), ("from-", ".From", ValueKind.Color, false), ("via-", ".Via", ValueKind.Color, false),
        ("to-", ".To", ValueKind.Color, false),
        ("text-", ".TextValue", ValueKind.TextSizeOrColor, false), ("border-", ".BorderValue", ValueKind.BorderWidthOrColor, false),
    };

    /// <summary>"translate-x-[-40]" -> name "translate-x-", inner "-40"; a leading '-' negates.</summary>
    private static bool IsArbitrary(string u, out string name, out string inner, out bool negative, out int innerStart)
    {
        name = inner = null;
        negative = u.StartsWith("-", System.StringComparison.Ordinal);
        int open = u.IndexOf("-[", System.StringComparison.Ordinal);
        innerStart = open + 2;
        if (open < 0 || !u.EndsWith("]")) return false;
        name = u.Substring(negative ? 1 : 0, open + 1 - (negative ? 1 : 0));
        inner = u.Substring(innerStart, u.Length - innerStart - 1);
        return true;
    }

    private static (string name, string method, ValueKind kind, bool negatable)? FindArbitrary(string name)
    {
        foreach (var entry in ArbitraryUtilities) if (entry.name == name) return entry;
        return null;
    }

    /// <summary>A literal [value]: a number with an optional unit (px, %, deg, ms, s), or a color.</summary>
    private static string ArbitraryLiteral(string name, string inner, bool negative, out string code)
    {
        code = null;
        var entry = FindArbitrary(name);
        if (entry == null) return "";
        var (_, method, kind, negatable) = entry.Value;
        if (negative && !negatable) return $"'{name.TrimEnd('-')}' can't be negative";
        inner = inner.Trim();
        if (inner.Length == 0) return "the [value] is empty";

        bool colorish = kind == ValueKind.Color || ((kind == ValueKind.TextSizeOrColor || kind == ValueKind.BorderWidthOrColor) && !StartsNumeric(inner));
        if (colorish)
        {
            if (negative) return "a color can't be negative";
            string colorMethod = kind == ValueKind.TextSizeOrColor ? ".TextColorOf" : kind == ValueKind.BorderWidthOrColor ? ".BorderColorOf" : method;
            return ColorArg(inner, colorMethod, out code);
        }

        string unit = "";
        foreach (string u in new[] { "px", "%", "deg", "ms", "s" })
            if (inner.EndsWith(u, System.StringComparison.Ordinal)) { unit = u; inner = inner.Substring(0, inner.Length - u.Length).Trim(); break; }
        if (!float.TryParse(inner, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || float.IsNaN(v) || float.IsInfinity(v))
            return $"'{inner}' is not a number";
        if (negative) v = -v;

        switch (kind)
        {
            case ValueKind.Length:
                if (unit != "" && unit != "px" && unit != "%") return $"use px or % here, not '{unit}'";
                code = unit == "%" ? $"{method}({LengthType}.Percent({Float(v)}))" : $"{method}({LengthType}.Px({Float(v)}))";
                return null;
            case ValueKind.Milliseconds:
                if (unit != "" && unit != "ms" && unit != "s") return $"use ms or s here, not '{unit}'";
                code = $"{method}({Float(unit == "s" ? v * 1000 : v)})";
                return null;
            case ValueKind.Fraction:
                if (unit == "%") v /= 100f; else if (unit != "") return $"use a fraction (0.5) or a percentage (50%), not '{unit}'";
                code = $"{method}({Float(v)})";
                return null;
            case ValueKind.TextSizeOrColor:
                code = $".TextSizeOf({Float(v)})";
                return unit == "" || unit == "px" ? null : $"use px here, not '{unit}'";
            case ValueKind.BorderWidthOrColor:
                code = $".BorderWidthOf({Float(v)})";
                return unit == "" || unit == "px" ? null : $"use px here, not '{unit}'";
            default:
                if (unit == "%" && kind == ValueKind.Factor) v /= 100f; // scale-[150%] = 1.5
                else if (unit != "" && unit != "px" && unit != "deg") return $"unexpected unit '{unit}'";
                code = $"{method}({Float(v)})";
                return null;
        }
    }

    private static bool StartsNumeric(string s) => s.Length > 0 && (char.IsDigit(s[0]) || s[0] == '.' || s[0] == '-' || s[0] == '+');

    /// <summary>
    /// A C# [@value]: "@Name.Chain" or "@(expr)", optionally followed by a unit (%, ms, s, deg, px).
    /// The expression's position inside <paramref name="inner"/> is returned for mapping.
    /// </summary>
    private static string ArbitraryDynamic(string name, string inner, bool negative, out DynamicStyle part, out int exprStart, out int exprLength)
    {
        part = null;
        exprStart = 1;
        exprLength = 0;
        var entry = FindArbitrary(name);
        if (entry == null) return "";
        var (_, method, kind, negatable) = entry.Value;
        if (negative && !negatable) return $"'{name.TrimEnd('-')}' can't be negative";

        // The expression, then an optional unit.
        int i = 1;
        if (i < inner.Length && inner[i] == '(')
        {
            int depth = 0;
            for (; i < inner.Length; i++)
            {
                if (inner[i] == '(') depth++;
                else if (inner[i] == ')' && --depth == 0) { i++; break; }
            }
            if (depth != 0) return "missing ')'";
            exprStart = 2;
            exprLength = i - 3;
        }
        else
        {
            while (i < inner.Length && (char.IsLetterOrDigit(inner[i]) || inner[i] == '_' || inner[i] == '.')) i++;
            exprLength = i - 1;
        }
        if (exprLength <= 0) return "the C# value after '@' is empty";
        string unit = inner.Substring(i).Trim();

        string sign = negative ? "-" : "";
        string open, close;
        switch (kind)
        {
            case ValueKind.Length:
                if (unit == "%") { open = $"{LengthType}.Percent({sign}(float)("; close = "))"; }
                else if (unit == "" || unit == "px") { open = negative ? "global::Cosmiria.StyleValues.Negate(global::Cosmiria.StyleValues.Len(" : "global::Cosmiria.StyleValues.Len("; close = negative ? "))" : ")"; }
                else return $"use px or % here, not '{unit}'";
                break;
            case ValueKind.Color:
            case ValueKind.TextSizeOrColor:
            case ValueKind.BorderWidthOrColor:
                if (unit != "") return $"unexpected unit '{unit}'";
                if (negative) return "this value can't be negative";
                open = "("; close = ")"; // overloads pick color or size (TextValue/BorderValue) by type
                break;
            case ValueKind.Milliseconds:
                if (unit == "s") { open = "1000f * (float)("; close = ")"; }
                else if (unit == "" || unit == "ms") { open = "(float)("; close = ")"; }
                else return $"use ms or s here, not '{unit}'";
                break;
            case ValueKind.Fraction:
                if (unit == "%") { open = "0.01f * (float)("; close = ")"; }
                else if (unit == "") { open = "(float)("; close = ")"; }
                else return $"use a fraction or %, not '{unit}'";
                break;
            default:
                if (unit == "%" && kind == ValueKind.Factor) { open = $"{sign}0.01f * (float)("; close = ")"; }
                else if (unit == "" || unit == "px" || unit == "deg") { open = $"{sign}(float)("; close = ")"; }
                else return $"unexpected unit '{unit}'";
                break;
        }
        part = new DynamicStyle { Prefix = $"default(global::Cosmiria.Style){method}({open}", Suffix = $"{close})" };
        return null;
    }

    /// <summary>Scan "@ident.chain" or "@(balanced)" starting at '@'. Returns the end index.</summary>
    private static int ScanExpressionToken(string v, int i, out TextSpan expr)
    {
        i++; // '@'
        if (i < v.Length && v[i] == '(')
        {
            int depth = 0, start = i + 1;
            for (; i < v.Length; i++)
            {
                if (v[i] == '(') depth++;
                else if (v[i] == ')' && --depth == 0) { expr = TextSpan.FromBounds(start, i); return i + 1; }
            }
            expr = TextSpan.FromBounds(start, v.Length);
            return v.Length;
        }
        int s0 = i;
        while (i < v.Length && !char.IsWhiteSpace(v[i])) i++;
        expr = TextSpan.FromBounds(s0, i);
        return i;
    }

    /// <summary>Returns null on success; "" for an unknown utility; otherwise an error message.</summary>
    private static string Translate(string u, out string code)
    {
        code = null;
        switch (u)
        {
            case "flex": code = ".Flex()"; return null;
            case "hidden": code = ".Hidden()"; return null;
            case "flex-row": code = ".FlexRow()"; return null;
            case "flex-col": code = ".FlexCol()"; return null;
            case "flex-wrap": code = ".FlexWrap()"; return null;
            case "flex-nowrap": code = ".FlexNoWrap()"; return null;
            case "flex-1": code = ".Flex1()"; return null;
            case "grid": code = ".Grid()"; return null;
            case "col-span-full": code = ".ColSpanOf(short.MaxValue)"; return null;
            case "row-span-full": code = ".RowSpanOf(short.MaxValue)"; return null;
            case "grow": code = ".Grow()"; return null;
            case "shrink": code = ".Shrink()"; return null;
            case "relative": code = ".Relative()"; return null;
            case "absolute": code = ".Absolute()"; return null;
            case "fixed": code = ".Fixed()"; return null;
            case "overlay": code = ".Overlay()"; return null;
            case "overflow-hidden": code = ".OverflowHidden()"; return null;
            case "overflow-scroll": code = ".OverflowScroll()"; return null;
            case "border": code = ".BorderWidthOf(1f)"; return null;
            case "rounded": code = ".Rounded(4f)"; return null;
            case "rounded-full": code = ".RoundedFull()"; return null;
            case "rounded-none": code = ".Rounded(0f)"; return null;
            case "shadow": code = ".Shadow()"; return null;
            case "shadow-sm": code = ".ShadowSm()"; return null;
            case "shadow-md": code = ".ShadowMd()"; return null;
            case "shadow-lg": code = ".ShadowLg()"; return null;
            case "shadow-xl": code = ".ShadowXl()"; return null;
            case "shadow-2xl": code = ".Shadow2Xl()"; return null;
            case "shadow-none": code = ".ShadowNone()"; return null;
            case "text-left": code = ".TextLeft()"; return null;
            case "text-center": code = ".TextCenter()"; return null;
            case "text-right": code = ".TextRight()"; return null;
            case "text-wrap": code = ".TextWrap()"; return null;
            case "text-nowrap":
            case "whitespace-nowrap": code = ".NoWrap()"; return null;
            case "w-full": code = ".WFull()"; return null;
            case "h-full": code = ".HFull()"; return null;
            case "size-full": code = ".SizeFull()"; return null;
        }

        if (TryPrefix(u, "justify-", out string rest))
            return Keyword(rest, out code, ("start", ".JustifyStart()"), ("center", ".JustifyCenter()"), ("end", ".JustifyEnd()"),
                ("between", ".JustifyBetween()"), ("around", ".JustifyAround()"), ("evenly", ".JustifyEvenly()"));
        if (TryPrefix(u, "items-", out rest))
            return Keyword(rest, out code, ("start", ".ItemsStart()"), ("center", ".ItemsCenter()"), ("end", ".ItemsEnd()"), ("stretch", ".ItemsStretch()"));
        if (TryPrefix(u, "self-", out rest))
            return Keyword(rest, out code, ("start", ".SelfStart()"), ("center", ".SelfCenter()"), ("end", ".SelfEnd()"), ("stretch", ".SelfStretch()"));
        if (TryPrefix(u, "overflow-", out rest))
            return Keyword(rest, out code, ("hidden", ".OverflowHidden()"), ("scroll", ".OverflowScroll()"));

        // Lengths (order matters: longer prefixes first).
        foreach (var (prefix, method) in LengthUtilities)
        {
            if (TryPrefix(u, prefix, out rest)) return LengthArg(rest, method, out code);
        }

        // Plain numeric utilities.
        foreach (var (prefix, method) in NumberUtilities)
        {
            if (TryPrefix(u, prefix, out rest)) return NumberArg(rest, method, out code);
        }

        string gradient = TranslateGradient(u, out code);
        if (gradient != "") return gradient;
        if (TryPrefix(u, "bg-", out rest)) return ColorArg(rest, ".Bg", out code);
        if (TryPrefix(u, "border-", out rest))
        {
            if (IsNumber(rest)) return NumberArg(rest, ".BorderWidthOf", out code);
            return ColorArg(rest, ".BorderColorOf", out code);
        }
        if (TryPrefix(u, "rounded-", out rest)) return NumberArg(rest, ".Rounded", out code);
        if (TryPrefix(u, "shadow-", out rest)) return ColorArg(rest, ".ShadowColorOf", out code); // shadow-black/50
        if (TryPrefix(u, "text-", out rest))
        {
            if (IsNumber(rest)) return NumberArg(rest, ".TextSizeOf", out code);
            foreach (var (name, method) in TextSizes)
            {
                if (rest == name) { code = method; return null; }
            }
            if (rest.EndsWith("xl") || rest == "xs" || rest == "md")
                return "text sizes are xs, sm, base, lg, xl, 2xl, 3xl, 4xl (or text-N for pixels)";
            return ColorArg(rest, ".TextColorOf", out code);
        }
        if (TryPrefix(u, "font-", out rest))
        {
            if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int id)) return "expected a font id (integer)";
            code = $".Font({id})";
            return null;
        }
        switch (u)
        {
            case "transition": code = ".Transition(global::Cosmiria.TransitionParts.Default)"; return null;
            case "transition-all": code = ".Transition(global::Cosmiria.TransitionParts.All)"; return null;
            case "transition-colors": code = ".Transition(global::Cosmiria.TransitionParts.Colors)"; return null;
            case "transition-opacity": code = ".Transition(global::Cosmiria.TransitionParts.Opacity)"; return null;
            case "transition-transform": code = ".Transition(global::Cosmiria.TransitionParts.Transform)"; return null;
            case "transition-none": code = ".Transition(global::Cosmiria.TransitionParts.None)"; return null;
            case "ease-linear": code = ".Ease(global::Cosmiria.Easing.Linear)"; return null;
            case "ease-in": code = ".Ease(global::Cosmiria.Easing.In)"; return null;
            case "ease-out": code = ".Ease(global::Cosmiria.Easing.Out)"; return null;
            case "ease-in-out": code = ".Ease(global::Cosmiria.Easing.InOut)"; return null;
        }
        if (TryPrefix(u, "ease-", out rest)) return "expected one of: linear, in, out, in-out";
        if (TryPrefix(u, "duration-", out rest)) return Milliseconds(rest, ".Duration", out code);
        if (TryPrefix(u, "delay-", out rest)) return Milliseconds(rest, ".Delay", out code);
        if (TryPrefix(u, "opacity-", out rest))
        {
            if (!float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out float pct) || pct < 0 || pct > 100)
                return $"expected a percentage 0-100 (opacity-50) but found '{rest}'";
            code = $".Opacity({Float(pct / 100f)})";
            return null;
        }

        if (TryPrefix(u, "grid-cols-fill-", out rest)) return NumberArg(rest, ".GridColsFill", out code);
        if (TryPrefix(u, "grid-cols-fit-", out rest)) return NumberArg(rest, ".GridColsFit", out code);
        foreach (var (prefix, method) in GridUtilities)
        {
            if (!TryPrefix(u, prefix, out rest)) continue;
            if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1)
                return $"expected a whole number of 1 or more ({prefix}3) but found '{rest}'";
            code = $"{method}({count})";
            return null;
        }
        if (u == "blur" || u == "backdrop-blur") { code = u == "blur" ? ".Blur(8f)" : ".BackdropBlur(8f)"; return null; }
        if (TryPrefix(u, "backdrop-blur-", out rest)) return BlurArg(rest, ".BackdropBlur", out code);
        if (TryPrefix(u, "blur-", out rest)) return BlurArg(rest, ".Blur", out code);

        string transform = TranslateTransform(u, out code);
        if (transform != "") return transform;
        if (TryPrefix(u, "grow-", out rest)) return NumberArg(rest, ".Grow", out code);
        if (TryPrefix(u, "shrink-", out rest)) return NumberArg(rest, ".Shrink", out code);

        return "";
    }

    private static readonly (string prefix, string method)[] GridUtilities =
    {
        ("grid-cols-", ".GridCols"), ("grid-rows-", ".GridRows"), ("col-span-", ".ColSpanOf"), ("row-span-", ".RowSpanOf"),
        ("col-start-", ".ColStartOf"), ("row-start-", ".RowStartOf"),
    };

    private static readonly (string name, float radius)[] BlurSizes =
    {
        ("none", 0), ("sm", 4), ("md", 12), ("lg", 16), ("xl", 24), ("2xl", 40), ("3xl", 64),
    };

    /// <summary>blur-sm, blur-lg, ... (Tailwind's scale) or blur-N in pixels.</summary>
    private static string BlurArg(string rest, string method, out string code)
    {
        code = null;
        foreach (var (name, radius) in BlurSizes)
        {
            if (rest == name) { code = $"{method}({Float(radius)})"; return null; }
        }
        if (IsNumber(rest)) return NumberArg(rest, method, out code);
        return "blur sizes are none, sm, md, lg, xl, 2xl, 3xl (or a number of pixels)";
    }

    private static readonly (string dir, int angle)[] GradientDirections =
    {
        ("t", 0), ("tr", 45), ("r", 90), ("br", 135), ("b", 180), ("bl", 225), ("l", 270), ("tl", 315),
    };

    /// <summary>bg-gradient-to-r / bg-linear-to-r / bg-linear-45 / bg-radial / from-* / via-* / to-*. "" if not one.</summary>
    private static string TranslateGradient(string u, out string code)
    {
        code = null;
        string rest;
        bool negative = u.StartsWith("-", System.StringComparison.Ordinal);
        string t = negative ? u.Substring(1) : u;
        if (t == "bg-radial") { code = ".RadialGradient()"; return negative ? "bg-radial can't be negative" : null; }
        if (TryPrefix(t, "bg-gradient-to-", out rest) || TryPrefix(t, "bg-linear-to-", out rest))
        {
            foreach (var (dir, angle) in GradientDirections)
                if (rest == dir) { code = $".LinearGradient({angle}f)"; return null; }
            return "expected a direction: t, tr, r, br, b, bl, l, tl";
        }
        if (TryPrefix(t, "bg-linear-", out rest))
        {
            if (!float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out float deg) || deg < 0)
                return $"expected an angle in degrees (bg-linear-45) or bg-linear-to-DIR, found '{rest}'";
            code = $".LinearGradient({(negative ? "-" : "")}{Float(deg)})";
            return null;
        }
        if (negative) return "";
        if (TryPrefix(t, "from-", out rest)) return ColorArg(rest, ".From", out code);
        if (TryPrefix(t, "via-", out rest)) return ColorArg(rest, ".Via", out code);
        if (TryPrefix(t, "to-", out rest)) return ColorArg(rest, ".To", out code);
        return "";
    }

    private static string Milliseconds(string rest, string method, out string code)
    {
        code = null;
        if (!float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out float ms) || ms < 0 || float.IsInfinity(ms))
            return $"expected milliseconds (duration-300) but found '{rest}'";
        code = $"{method}({Float(ms)})";
        return null;
    }

    /// <summary>translate-x-N, scale-N, rotate-N, origin-* (a leading '-' negates). "" if not a transform.</summary>
    private static string TranslateTransform(string u, out string code)
    {
        code = null;
        bool negative = u.StartsWith("-", System.StringComparison.Ordinal);
        string t = negative ? u.Substring(1) : u;
        string sign = negative ? "-" : "";
        string rest;

        if (TryPrefix(t, "translate-x-", out rest) || TryPrefix(t, "translate-y-", out rest))
        {
            string method = t[10] == 'x' ? ".TranslateXOf" : ".TranslateYOf";
            string arg;
            if (rest == "full") arg = $"{LengthType}.Percent({sign}100f)";
            else if (rest.EndsWith("%") && float.TryParse(rest.Substring(0, rest.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct) && pct >= 0)
                arg = $"{LengthType}.Percent({sign}{Float(pct)})";
            else if (float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out float px) && px >= 0 && !float.IsInfinity(px))
                arg = $"{LengthType}.Px({sign}{Float(px)})";
            else return $"expected pixels, a percentage (50%) or 'full' but found '{rest}'";
            code = $"{method}({arg})";
            return null;
        }
        if (TryPrefix(t, "rotate-", out rest))
        {
            if (!float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out float deg) || deg < 0 || float.IsInfinity(deg))
                return $"expected degrees (rotate-45) but found '{rest}'";
            code = $".Rotate({sign}{Float(deg)})";
            return null;
        }
        if (TryPrefix(t, "scale-", out rest))
        {
            string method = ".Scale";
            if (TryPrefix(rest, "x-", out string rx)) { method = ".ScaleXOf"; rest = rx; }
            else if (TryPrefix(rest, "y-", out string ry)) { method = ".ScaleYOf"; rest = ry; }
            if (!float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out float pct) || pct < 0 || float.IsInfinity(pct))
                return $"expected a percentage like scale-150 (= 1.5x) but found '{rest}'";
            code = $"{method}({sign}{Float(pct / 100f)})";
            return null;
        }
        if (TryPrefix(t, "origin-", out rest))
        {
            if (negative) return "origin can't be negative";
            foreach (var (name, x, y) in Origins)
            {
                if (rest == name) { code = $".Origin({Float(x)}, {Float(y)})"; return null; }
            }
            return "expected one of: center, top, top-right, right, bottom-right, bottom, bottom-left, left, top-left";
        }
        return "";
    }

    private static readonly (string, string)[] LengthUtilities =
    {
        ("min-w-", ".MinW"), ("min-h-", ".MinH"), ("max-w-", ".MaxW"), ("max-h-", ".MaxH"),
        ("size-", ".Size"), ("w-", ".W"), ("h-", ".H"),
    };

    private static readonly (string, string)[] NumberUtilities =
    {
        ("gap-x-", ".GapX"), ("gap-y-", ".GapY"), ("gap-", ".Gap"),
        ("px-", ".Px"), ("py-", ".Py"), ("pt-", ".Pt"), ("pb-", ".Pb"), ("pl-", ".Pl"), ("pr-", ".Pr"), ("p-", ".Padding"),
        ("mx-", ".Mx"), ("my-", ".My"), ("mt-", ".Mt"), ("mb-", ".Mb"), ("ml-", ".Ml"), ("mr-", ".Mr"), ("m-", ".Margin"),
        ("inset-", ".Inset"), ("top-", ".Top"), ("left-", ".Left"), ("right-", ".Right"), ("bottom-", ".Bottom"),
    };

    private static bool TryPrefix(string u, string prefix, out string rest)
    {
        if (u.StartsWith(prefix, System.StringComparison.Ordinal))
        {
            rest = u.Substring(prefix.Length);
            return true;
        }
        rest = null;
        return false;
    }

    private static string Keyword(string rest, out string code, params (string word, string code)[] options)
    {
        foreach (var (word, c) in options)
        {
            if (rest == word) { code = c; return null; }
        }
        code = null;
        var names = new StringBuilder();
        foreach (var (word, _) in options) names.Append(names.Length > 0 ? ", " : "").Append(word);
        return $"expected one of: {names}";
    }

    private static bool IsNumber(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static string NumberArg(string rest, string method, out string code)
    {
        code = null;
        if (!float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || float.IsNaN(v) || float.IsInfinity(v))
            return $"'{rest}' is not a number";
        code = $"{method}({Float(v)})";
        return null;
    }

    private static string LengthArg(string rest, string method, out string code)
    {
        code = null;
        string arg;
        if (rest == "full") arg = LengthType + ".Full";
        else if (rest == "auto") arg = LengthType + ".Auto";
        else if (rest.EndsWith("%"))
        {
            if (!float.TryParse(rest.Substring(0, rest.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float p) || p < 0)
                return $"'{rest}' is not a valid percentage";
            arg = $"{LengthType}.Percent({Float(p)})";
        }
        else if (float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out float px) && px >= 0)
        {
            arg = $"{LengthType}.Px({Float(px)})";
        }
        else return $"expected pixels, a percentage (50%), 'full' or 'auto' but found '{rest}'";

        code = $"{method}({arg})";
        return null;
    }

    /// <summary>
    /// Colors: #RGB / #RRGGBB / #RRGGBBAA, Tailwind names ("blue-500", "slate-950"), a bare family
    /// ("blue" = blue-500), black / white / transparent. Any color takes a Tailwind opacity
    /// modifier: "blue-500/50", "#ff0000/25", "white/10".
    /// </summary>
    private static string ColorArg(string rest, string method, out string code)
    {
        code = null;
        float opacity = -1;
        int slash = rest.LastIndexOf('/');
        if (slash >= 0)
        {
            string op = rest.Substring(slash + 1);
            if (!float.TryParse(op, NumberStyles.Float, CultureInfo.InvariantCulture, out opacity) || opacity < 0 || opacity > 100)
                return $"opacity '/{op}' must be a number from 0 to 100";
            rest = rest.Substring(0, slash);
        }

        string error = ResolveColor(rest, out uint rgba);
        if (error != null) return error;
        if (opacity >= 0) rgba = Tailwind.WithOpacity(rgba, opacity / 100f);

        code = $"{method}(new global::Riateu.Graphics.Color(0x{rgba:X8}u))";
        return null;
    }

    private static string ResolveColor(string rest, out uint rgba)
    {
        rgba = 0;
        if (rest.StartsWith("#"))
        {
            string hex = rest.Substring(1);
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint raw))
                return $"'{rest}' is not a hex color";
            switch (hex.Length)
            {
                case 3: // #RGB
                    rgba = (Dup((raw >> 8) & 0xF) << 24) | (Dup((raw >> 4) & 0xF) << 16) | (Dup(raw & 0xF) << 8) | 0xFF;
                    break;
                case 6: rgba = (raw << 8) | 0xFF; break;
                case 8: rgba = raw; break;
                default: return $"'{rest}' must be #RGB, #RRGGBB or #RRGGBBAA";
            }
        }
        else if (Tailwind.ByName.TryGetValue(rest, out rgba))
        {
            return null;
        }
        else if (System.Array.IndexOf(Tailwind.Families, rest) >= 0)
        {
            rgba = Tailwind.ByName[rest + "-500"]; // bare family = its 500 shade
            return null;
        }
        else
        {
            int dash = rest.LastIndexOf('-');
            string family = dash > 0 ? rest.Substring(0, dash) : rest;
            if (dash > 0 && System.Array.IndexOf(Tailwind.Families, family) >= 0)
                return $"'{rest}': shade must be one of {string.Join(", ", Tailwind.Shades)}";

            string suggestion = Closest(family, Tailwind.Families);
            string hint = suggestion != null ? $" Did you mean '{suggestion}{(dash > 0 ? rest.Substring(dash) : "")}'?" : "";
            return $"unknown color '{rest}'.{hint} Use #RRGGBB[AA], a Tailwind color like blue-500 " +
                   $"({string.Join(", ", Tailwind.Families)}), black, white or transparent; add /50 for opacity";
        }
        return null;
    }

    /// <summary>Closest name by edit distance (≤ 2), for "did you mean" hints.</summary>
    private static string Closest(string value, string[] candidates)
    {
        string best = null;
        int bestDistance = 3;
        foreach (string c in candidates)
        {
            int d = Levenshtein(value, c);
            if (d < bestDistance) { bestDistance = d; best = c; }
        }
        return best;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = System.Math.Min(System.Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static uint Dup(uint nibble) => (nibble << 4) | nibble;

    private static string Float(float v) => v.ToString("R", CultureInfo.InvariantCulture) + "f";
}
