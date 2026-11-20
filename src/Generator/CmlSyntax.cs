using System.Collections.Generic;
using Microsoft.CodeAnalysis.Text;

namespace Cosmiria.Generator;

internal abstract class CmlNode
{
    public TextSpan Span;
}

internal sealed class CmlAttributeSyntax
{
    public string Name;
    public TextSpan NameSpan;
    public string Value;
    /// <summary>Span of the value, excluding quotes.</summary>
    public TextSpan ValueSpan;
}

internal sealed class CmlElement : CmlNode
{
    public string Name;
    public TextSpan NameSpan;
    public readonly List<CmlAttributeSyntax> Attributes = new();
    public readonly List<CmlNode> Children = new();
    public bool SelfClosing;
}

/// <summary>A run of text: literals and @expressions.</summary>
internal sealed class CmlText : CmlNode
{
    public readonly List<CmlTextPart> Parts = new();
}

internal readonly struct CmlTextPart
{
    public readonly string Literal;     // null when this is an expression
    public readonly TextSpan Expression; // span of the C# expression in the source

    private CmlTextPart(string literal, TextSpan expression)
    {
        Literal = literal;
        Expression = expression;
    }

    public bool IsExpression => Literal == null;
    public static CmlTextPart Text(string s) => new(s, default);
    public static CmlTextPart Expr(TextSpan span) => new(null, span);
}

/// <summary>@if / else if / else / @foreach / @for / @while with a markup body.</summary>
internal sealed class CmlControl : CmlNode
{
    public string Keyword;
    /// <summary>C# header copied verbatim, e.g. "if (x == 2)" or "else".</summary>
    public TextSpan Header;
    public readonly List<CmlNode> Body = new();
    public CmlControl Else;
}

/// <summary>@{ statements } — C# statements emitted verbatim.</summary>
internal sealed class CmlCode : CmlNode
{
    public TextSpan Code;
}

internal sealed class CmlDocument
{
    public CmlElement Root;
}
