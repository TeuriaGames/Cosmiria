using Microsoft.CodeAnalysis;

namespace Cosmiria.Generator;

internal static class CmlDiagnostics
{
    private const string Syntax = "CML.Syntax";
    private const string Semantic = "CML.Semantic";
    private const string Usage = "CML.Usage";

    private static DiagnosticDescriptor Error(string id, string title, string message, string category) =>
        new(id, title, message, category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    // ---- syntax ---------------------------------------------------------------
    public static readonly DiagnosticDescriptor UnexpectedEof =
        Error("CML001", "Unexpected end of file", "Unexpected end of file: {0}", Syntax);
    public static readonly DiagnosticDescriptor MismatchedTag =
        Error("CML002", "Mismatched closing tag", "Expected '</{0}>' but found '</{1}>'", Syntax);
    public static readonly DiagnosticDescriptor UnexpectedToken =
        Error("CML003", "Unexpected token", "{0}", Syntax);
    public static readonly DiagnosticDescriptor MalformedAttribute =
        Error("CML004", "Malformed attribute", "{0}", Syntax);
    public static readonly DiagnosticDescriptor MalformedCode =
        Error("CML005", "Malformed code", "{0}", Syntax);
    public static readonly DiagnosticDescriptor UnknownEntity =
        Error("CML006", "Unknown character entity", "Unknown entity '&{0};'. Supported: &lt; &gt; &amp; &quot; &apos; &#N; &#xH;", Syntax);
    public static readonly DiagnosticDescriptor MissingRoot =
        Error("CML007", "Missing <cml> root", "A CML file must have a single <cml> root element", Syntax);

    // ---- semantic -------------------------------------------------------------
    public static readonly DiagnosticDescriptor UnknownElement =
        Error("CML100", "Unknown element", "Unknown element '<{0}>'. Expected View, Button, Text, or a component struct implementing Cosmiria.IComponent (check <using namespace=\"...\"/>).", Semantic);
    public static readonly DiagnosticDescriptor UnknownAttribute =
        Error("CML101", "Unknown attribute", "Unknown attribute '{0}' on <{1}>. Allowed: {2}", Semantic);
    public static readonly DiagnosticDescriptor DuplicateAttribute =
        Error("CML102", "Duplicate attribute", "Duplicate attribute '{0}'", Semantic);
    public static readonly DiagnosticDescriptor MissingAttribute =
        Error("CML103", "Missing attribute", "<{0}> requires the '{1}' attribute", Semantic);
    public static readonly DiagnosticDescriptor ContentNotAllowed =
        Error("CML104", "Content not allowed", "{0}", Semantic);
    public static readonly DiagnosticDescriptor UnknownUtility =
        Error("CML105", "Unknown style utility", "Unknown style utility '{0}'", Semantic);
    public static readonly DiagnosticDescriptor InvalidUtilityValue =
        Error("CML106", "Invalid style value", "Invalid value in '{0}': {1}", Semantic);
    public static readonly DiagnosticDescriptor EmptyExpression =
        Error("CML107", "Empty expression", "{0} must not be empty", Semantic);
    public static readonly DiagnosticDescriptor InvalidHandler =
        Error("CML109", "Invalid event handler", "'{0}' must call a method of the component: 'Method' or 'Method(arg1, arg2, ...)'. {1}", Semantic);
    public static readonly DiagnosticDescriptor LanguageVersionTooLow =
        Error("CML110", "C# 14 required", "Event handlers with arguments need C# 14 or later (current: {0}). Set <LangVersion>14</LangVersion> or 'latest'.", Semantic);
    public static readonly DiagnosticDescriptor EntityInExpression =
        Error("CML111", "Entity in C# attribute", "Character entities are not decoded in C# attribute values ('{0}'). Quote the attribute with the other quote instead, e.g. onclick='Say(\"hi\")'", Semantic);
    public static readonly DiagnosticDescriptor SvgNotFound =
        Error("CML112", "SVG file not found", "SVG file '{0}' was not found (looked for '{1}'). Add it with <AdditionalFiles Include=\"**/*.svg\" />.", Semantic);
    public static readonly DiagnosticDescriptor InvalidSvg =
        Error("CML113", "Invalid SVG", "'{0}' is not valid SVG: {1}", Semantic);
    public static readonly DiagnosticDescriptor ConflictingAttributes =
        Error("CML114", "Conflicting attributes", "{0}", Semantic);
    public static readonly DiagnosticDescriptor InvalidBinding =
        Error("CML115", "Invalid binding", "'{0}': {1}", Semantic);
    public static readonly DiagnosticDescriptor InvalidComponent =
        Error("CML108", "Invalid component", "'{0}' cannot be used as a component: {1}", Semantic);

    // ---- usage (C# side) ------------------------------------------------------
    public static readonly DiagnosticDescriptor FileNotFound =
        Error("CML200", "CML file not found", "CML file '{0}' was not found (looked for '{1}'). Add it to the project with <AdditionalFiles Include=\"**/*.cml\" />.", Usage);
    public static readonly DiagnosticDescriptor NotPartial =
        Error("CML201", "Type must be partial", "'{0}' must be declared 'partial' to use [Cml]", Usage);
    public static readonly DiagnosticDescriptor NotStruct =
        Error("CML202", "Type must be a struct", "'{0}' must be a struct to use [Cml] (components are value types)", Usage);
    public static readonly DiagnosticDescriptor UnsupportedShape =
        Error("CML203", "Unsupported type shape", "'{0}' {1}", Usage);
    public static readonly DiagnosticDescriptor RenderAlreadyDefined =
        Error("CML204", "Render already defined", "'{0}' already defines Render(UIStack, EventStack); remove it, [Cml] generates it", Usage);
    public static readonly DiagnosticDescriptor InvalidPath =
        Error("CML205", "Invalid CML path", "The [Cml] path must be a non-empty string literal", Usage);
}
