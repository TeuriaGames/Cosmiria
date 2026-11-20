using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Cosmiria.Generator;

/// <summary>
/// Hand-written CML parser. CML is XML-shaped markup mixed with Razor-style C#:
/// <c>@if (...) { markup } else { markup }</c>, <c>@foreach/@for/@while</c>, <c>@{ statements }</c>,
/// <c>@expr</c> / <c>@(expr)</c> in text, and <c>@@</c> for a literal '@'. Because C# conditions may
/// contain '&lt;', a real XML parser can't be used. Every node keeps its source span, and the parser
/// recovers from errors so a single mistake doesn't hide the rest.
/// </summary>
internal sealed class CmlParser
{
    private readonly string s;
    private readonly SourceText text;
    private readonly string path;
    private readonly List<Diagnostic> diagnostics;
    private int pos;
    private readonly List<string> openElements = new();

    private CmlParser(SourceText text, string path, List<Diagnostic> diagnostics)
    {
        this.text = text;
        this.path = path;
        this.diagnostics = diagnostics;
        s = text.ToString();
    }

    public static CmlDocument Parse(SourceText text, string path, List<Diagnostic> diagnostics) =>
        new CmlParser(text, path, diagnostics).ParseDocument();

    // ================================================================== document

    private CmlDocument ParseDocument()
    {
        var doc = new CmlDocument();
        SkipMisc();

        if (pos < s.Length && s[pos] == '<' && IsNameStart(Peek(1)))
        {
            CmlElement root = ParseElement();
            if (root.Name == "cml") doc.Root = root;
            else Report(CmlDiagnostics.MissingRoot, root.NameSpan);
        }
        else
        {
            Report(CmlDiagnostics.MissingRoot, new TextSpan(pos, s.Length > pos ? 1 : 0));
            return doc;
        }

        SkipMisc();
        if (pos < s.Length)
        {
            Report(CmlDiagnostics.UnexpectedToken, new TextSpan(pos, 1), "Unexpected content after the </cml> root element");
        }
        return doc;
    }

    /// <summary>Skip whitespace, comments and an optional &lt;?xml ...?&gt; prolog.</summary>
    private void SkipMisc()
    {
        while (true)
        {
            SkipWhitespace();
            if (StartsWith("<!--")) SkipComment();
            else if (StartsWith("<?"))
            {
                int end = s.IndexOf("?>", pos, System.StringComparison.Ordinal);
                pos = end < 0 ? s.Length : end + 2;
            }
            else break;
        }
    }

    // ================================================================== markup

    private CmlElement ParseElement()
    {
        var el = new CmlElement();
        int start = pos;
        pos++; // '<'
        int nameStart = pos;
        while (pos < s.Length && IsNameChar(s[pos])) pos++;
        el.Name = s.Substring(nameStart, pos - nameStart);
        el.NameSpan = TextSpan.FromBounds(nameStart, pos);

        // attributes
        while (true)
        {
            SkipWhitespace();
            if (pos >= s.Length)
            {
                Report(CmlDiagnostics.UnexpectedEof, el.NameSpan, $"start tag '<{el.Name}' is not closed with '>'");
                el.Span = TextSpan.FromBounds(start, pos);
                return el;
            }
            char c = s[pos];
            if (c == '/' && Peek(1) == '>') { pos += 2; el.SelfClosing = true; break; }
            if (c == '>') { pos++; break; }
            if (c == '<')
            {
                Report(CmlDiagnostics.UnexpectedToken, el.NameSpan, $"Start tag '<{el.Name}' is missing '>'");
                el.Span = TextSpan.FromBounds(start, pos);
                return el;
            }
            if (IsNameStart(c))
            {
                ParseAttribute(el);
                continue;
            }
            Report(CmlDiagnostics.UnexpectedToken, new TextSpan(pos, 1), $"Unexpected character '{c}' in tag '<{el.Name}>'");
            pos++;
        }

        if (!el.SelfClosing)
        {
            openElements.Add(el.Name);
            ParseContent(el.Children, inCodeBody: false);
            openElements.RemoveAt(openElements.Count - 1);
            if (pos >= s.Length)
            {
                Report(CmlDiagnostics.UnexpectedEof, el.NameSpan, $"element '<{el.Name}>' is never closed");
            }
            else
            {
                int closeStart = pos;
                pos += 2;
                int n0 = pos;
                while (pos < s.Length && IsNameChar(s[pos])) pos++;
                string closeName = s.Substring(n0, pos - n0);
                if (closeName != el.Name)
                {
                    if (openElements.Contains(closeName))
                    {
                        // It closes an ancestor: this element was left open. Don't consume the
                        // tag, so the ancestor closes normally and parsing stays in sync.
                        Report(CmlDiagnostics.MismatchedTag, el.NameSpan, el.Name, closeName);
                        pos = closeStart;
                        el.Span = TextSpan.FromBounds(start, pos);
                        return el;
                    }
                    Report(CmlDiagnostics.MismatchedTag, TextSpan.FromBounds(closeStart, pos), el.Name, closeName);
                }
                SkipWhitespace();
                if (pos < s.Length && s[pos] == '>') pos++;
                else Report(CmlDiagnostics.UnexpectedToken, new TextSpan(pos, 0), $"Expected '>' to close '</{closeName}'");
            }
        }

        el.Span = TextSpan.FromBounds(start, pos);
        return el;
    }

    private void ParseAttribute(CmlElement el)
    {
        var attr = new CmlAttributeSyntax();
        int n0 = pos;
        while (pos < s.Length && IsNameChar(s[pos])) pos++;
        attr.Name = s.Substring(n0, pos - n0);
        attr.NameSpan = TextSpan.FromBounds(n0, pos);

        SkipWhitespace();
        if (pos >= s.Length || s[pos] != '=')
        {
            Report(CmlDiagnostics.MalformedAttribute, attr.NameSpan, $"Attribute '{attr.Name}' needs a value: {attr.Name}=\"...\"");
            attr.Value = "";
            attr.ValueSpan = new TextSpan(pos, 0);
            el.Attributes.Add(attr);
            return;
        }
        pos++;
        SkipWhitespace();
        if (pos >= s.Length || (s[pos] != '"' && s[pos] != '\''))
        {
            Report(CmlDiagnostics.MalformedAttribute, new TextSpan(pos, pos < s.Length ? 1 : 0), $"Attribute '{attr.Name}' value must be quoted");
            // Recover: take the bare word.
            int b0 = pos;
            while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] != '>' && s[pos] != '/') pos++;
            attr.Value = s.Substring(b0, pos - b0);
            attr.ValueSpan = TextSpan.FromBounds(b0, pos);
            el.Attributes.Add(attr);
            return;
        }

        char quote = s[pos++];
        int v0 = pos;
        int end = s.IndexOf(quote, pos);
        if (end < 0)
        {
            Report(CmlDiagnostics.MalformedAttribute, attr.NameSpan, $"Attribute '{attr.Name}' value is missing its closing {quote}");
            end = s.Length;
        }
        attr.Value = s.Substring(v0, end - v0);
        attr.ValueSpan = TextSpan.FromBounds(v0, end);
        pos = end < s.Length ? end + 1 : end;
        el.Attributes.Add(attr);
    }

    /// <summary>
    /// Parse mixed content until '&lt;/' (end tag), '}' when inside a code body, or EOF.
    /// </summary>
    private void ParseContent(List<CmlNode> nodes, bool inCodeBody)
    {
        CmlText run = null;
        var literal = new StringBuilder();
        int runStart = pos;

        void AppendLiteral(string value)
        {
            if (run == null) { run = new CmlText(); runStart = pos; }
            literal.Append(value);
        }
        void AppendExpr(TextSpan span, int partStart)
        {
            if (run == null) { run = new CmlText(); runStart = partStart; }
            FlushLiteral();
            run.Parts.Add(CmlTextPart.Expr(span));
        }
        void FlushLiteral()
        {
            if (literal.Length > 0) { run.Parts.Add(CmlTextPart.Text(literal.ToString())); literal.Clear(); }
        }
        void FlushRun()
        {
            if (run == null) return;
            FlushLiteral();
            run.Span = TextSpan.FromBounds(runStart, pos);
            nodes.Add(run);
            run = null;
        }

        while (pos < s.Length)
        {
            char c = s[pos];
            if (c == '<')
            {
                if (StartsWith("<!--")) { SkipComment(); continue; }
                if (StartsWith("</")) break;
                if (IsNameStart(Peek(1)))
                {
                    FlushRun();
                    nodes.Add(ParseElement());
                    continue;
                }
                Report(CmlDiagnostics.UnexpectedToken, new TextSpan(pos, 1), "Unexpected '<'. Use &lt; for a literal '<'");
                AppendLiteral("<");
                pos++;
                continue;
            }

            if (c == '}' && inCodeBody) break;

            if (c == '@')
            {
                char n = Peek(1);
                if (n == '@' || n == '}')
                {
                    AppendLiteral(n.ToString());
                    pos += 2;
                    continue;
                }
                if (n == '{')
                {
                    FlushRun();
                    nodes.Add(ParseCodeBlock());
                    continue;
                }
                if (n == '(')
                {
                    int at = pos;
                    pos++;
                    int open = pos;
                    if (!SkipBalanced())
                    {
                        Report(CmlDiagnostics.MalformedCode, new TextSpan(at, 2), "Unterminated '@(' expression: missing ')'");
                        RecoverToLineEnd(open, stopAtBrace: false);
                    }
                    var span = TextSpan.FromBounds(open + 1, System.Math.Max(open + 1, pos - 1));
                    if (string.IsNullOrWhiteSpace(s.Substring(span.Start, span.Length)))
                        Report(CmlDiagnostics.EmptyExpression, TextSpan.FromBounds(at, pos), "'@()'");
                    AppendExpr(span, at);
                    continue;
                }
                if (IsIdentStart(n))
                {
                    int at = pos;
                    int w0 = pos + 1, w1 = w0;
                    while (w1 < s.Length && IsIdentChar(s[w1])) w1++;
                    string word = s.Substring(w0, w1 - w0);
                    if (word is "if" or "foreach" or "for" or "while")
                    {
                        FlushRun();
                        pos = w0;
                        nodes.Add(ParseControl(word, at));
                        continue;
                    }
                    if (word == "else")
                    {
                        Report(CmlDiagnostics.MalformedCode, TextSpan.FromBounds(at, w1), "'@else' must directly follow the '}' of an @if block (write '} else {')");
                        pos = w1;
                        continue;
                    }
                    pos = w0;
                    AppendExpr(ParseImplicitExpression(), at);
                    continue;
                }
                Report(CmlDiagnostics.MalformedCode, new TextSpan(pos, 1),
                    "Expected an expression, '(', '{' or a keyword after '@'. Use @@ for a literal '@'");
                AppendLiteral("@");
                pos++;
                continue;
            }

            if (c == '&')
            {
                int semi = s.IndexOf(';', pos);
                if (semi > pos && semi - pos <= 10)
                {
                    string name = s.Substring(pos + 1, semi - pos - 1);
                    string decoded = DecodeEntity(name);
                    if (decoded != null) AppendLiteral(decoded);
                    else Report(CmlDiagnostics.UnknownEntity, TextSpan.FromBounds(pos, semi + 1), name);
                    pos = semi + 1;
                    continue;
                }
                AppendLiteral("&");
                pos++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                // HTML-like: a run of source whitespace becomes one space (entities stay exact).
                while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
                AppendLiteral(" ");
                continue;
            }

            AppendLiteral(c.ToString());
            pos++;
        }

        FlushRun();
    }

    /// <summary>@if (...) { } else if (...) { } else { }, @foreach (...) { }, @for, @while.</summary>
    private CmlControl ParseControl(string keyword, int atPos)
    {
        var node = new CmlControl { Keyword = keyword };
        int headerStart = pos; // at the keyword (after '@')
        pos += keyword.Length;
        SkipWhitespace();

        if (pos < s.Length && s[pos] == '(')
        {
            int open = pos;
            if (!SkipBalanced())
            {
                Report(CmlDiagnostics.MalformedCode, TextSpan.FromBounds(atPos, headerStart + keyword.Length), $"Missing ')' in '@{keyword} (...)'");
                RecoverToLineEnd(open, stopAtBrace: true);
            }
        }
        else
        {
            Report(CmlDiagnostics.MalformedCode, TextSpan.FromBounds(atPos, headerStart + keyword.Length), $"Expected '(' after '@{keyword}'");
        }
        node.Header = TextSpan.FromBounds(headerStart, pos);

        ParseBody(node, TextSpan.FromBounds(atPos, headerStart + keyword.Length), $"@{keyword}");

        if (keyword == "if") ParseElseChain(node);
        node.Span = TextSpan.FromBounds(atPos, pos);
        return node;
    }

    private void ParseElseChain(CmlControl ifNode)
    {
        int save = pos;
        SkipWhitespaceAndComments();
        if (!StartsWithWord("else"))
        {
            pos = save;
            return;
        }

        int elseStart = pos;
        pos += 4;
        int afterElse = pos;
        SkipWhitespace();
        var elseNode = new CmlControl { Keyword = "else" };
        if (StartsWithWord("if"))
        {
            pos += 2;
            SkipWhitespace();
            if (pos < s.Length && s[pos] == '(')
            {
                int open = pos;
                if (!SkipBalanced())
                {
                    Report(CmlDiagnostics.MalformedCode, TextSpan.FromBounds(elseStart, open), "Missing ')' in 'else if (...)'");
                    RecoverToLineEnd(open, stopAtBrace: true);
                }
            }
            else
            {
                Report(CmlDiagnostics.MalformedCode, TextSpan.FromBounds(elseStart, pos), "Expected '(' after 'else if'");
            }
            elseNode.Keyword = "else if";
            elseNode.Header = TextSpan.FromBounds(elseStart, pos);
            ParseBody(elseNode, TextSpan.FromBounds(elseStart, pos), "else if");
            ParseElseChain(elseNode);
        }
        else
        {
            pos = afterElse;
            elseNode.Header = TextSpan.FromBounds(elseStart, afterElse);
            ParseBody(elseNode, elseNode.Header, "else");
        }
        elseNode.Span = TextSpan.FromBounds(elseStart, pos);
        ifNode.Else = elseNode;
    }

    private void ParseBody(CmlControl node, TextSpan keywordSpan, string what)
    {
        SkipWhitespace();
        if (pos >= s.Length || s[pos] != '{')
        {
            Report(CmlDiagnostics.MalformedCode, keywordSpan, $"Expected '{{' to start the {what} block");
            return;
        }
        pos++;
        ParseContent(node.Body, inCodeBody: true);
        if (pos < s.Length && s[pos] == '}')
        {
            pos++;
        }
        else if (pos >= s.Length)
        {
            Report(CmlDiagnostics.UnexpectedEof, keywordSpan, $"the {what} block is missing its closing '}}'");
        }
        else
        {
            // Hit '</...>' of an enclosing element before the '}'.
            Report(CmlDiagnostics.MalformedCode, keywordSpan, $"The {what} block is missing its closing '}}' before '{CloseTagAt()}'");
        }
    }

    private CmlCode ParseCodeBlock()
    {
        int at = pos;
        pos++; // '@' -> now at '{'
        int open = pos;
        if (!SkipBalanced())
        {
            Report(CmlDiagnostics.MalformedCode, new TextSpan(at, 2), "Unterminated '@{' code block: missing '}'");
        }
        var code = TextSpan.FromBounds(open + 1, System.Math.Max(open + 1, pos - 1));
        return new CmlCode { Code = code, Span = TextSpan.FromBounds(at, pos) };
    }

    /// <summary>@Name.Member[0].Call(x) — identifier followed by member/index/call chains.</summary>
    private TextSpan ParseImplicitExpression()
    {
        int start = pos;
        while (pos < s.Length && IsIdentChar(s[pos])) pos++;
        while (pos < s.Length)
        {
            char c = s[pos];
            if (c == '.' && IsIdentStart(Peek(1)))
            {
                pos++;
                while (pos < s.Length && IsIdentChar(s[pos])) pos++;
            }
            else if (c == '(' || c == '[')
            {
                int p0 = pos;
                if (!SkipBalanced())
                {
                    Report(CmlDiagnostics.MalformedCode, new TextSpan(p0, 1), $"Unbalanced '{c}' in expression");
                    RecoverToLineEnd(p0, stopAtBrace: false);
                    break;
                }
            }
            else break;
        }
        return TextSpan.FromBounds(start, pos);
    }

    // ================================================================== C#-aware scanning

    /// <summary>
    /// With pos at an opening bracket, advance past its matching close bracket. Skips C# string,
    /// char, verbatim/interpolated string literals and comments. Returns false at EOF.
    /// </summary>
    private bool SkipBalanced()
    {
        var stack = new Stack<char>();
        while (pos < s.Length)
        {
            char c = s[pos];
            switch (c)
            {
                case '(': stack.Push(')'); pos++; break;
                case '[': stack.Push(']'); pos++; break;
                case '{': stack.Push('}'); pos++; break;
                case ')':
                case ']':
                case '}':
                    pos++;
                    if (stack.Count == 0 || stack.Pop() != c) return false;
                    if (stack.Count == 0) return true;
                    break;
                case '"': SkipString(verbatim: false); break;
                case '@' when Peek(1) == '"': pos++; SkipString(verbatim: true); break;
                case '$' when Peek(1) == '"': pos++; SkipString(verbatim: false); break;
                case '$' when Peek(1) == '@' && Peek(2) == '"': pos += 2; SkipString(verbatim: true); break;
                case '\'': SkipCharLiteral(); break;
                case '/' when Peek(1) == '/':
                    while (pos < s.Length && s[pos] != '\n') pos++;
                    break;
                case '/' when Peek(1) == '*':
                    int end = s.IndexOf("*/", pos + 2, System.StringComparison.Ordinal);
                    pos = end < 0 ? s.Length : end + 2;
                    break;
                default: pos++; break;
            }
        }
        return false;
    }

    private void SkipString(bool verbatim)
    {
        pos++; // opening quote
        while (pos < s.Length)
        {
            char c = s[pos];
            if (verbatim)
            {
                if (c == '"')
                {
                    if (Peek(1) == '"') { pos += 2; continue; }
                    pos++;
                    return;
                }
            }
            else
            {
                if (c == '\\') { pos += 2; continue; }
                if (c == '"') { pos++; return; }
                if (c == '\n') return; // unterminated; let the C# compiler report it
            }
            pos++;
        }
    }

    private void SkipCharLiteral()
    {
        pos++;
        while (pos < s.Length)
        {
            char c = s[pos];
            if (c == '\\') { pos += 2; continue; }
            pos++;
            if (c == '\'' || c == '\n') return;
        }
    }

    // ================================================================== helpers

    /// <summary>After an unbalanced bracket, resume at the end of its line (or at '{').</summary>
    private void RecoverToLineEnd(int openPos, bool stopAtBrace)
    {
        pos = openPos + 1;
        while (pos < s.Length && s[pos] != '\n' && !(stopAtBrace && s[pos] == '{')) pos++;
    }

    private void SkipComment()
    {
        int start = pos;
        int end = s.IndexOf("-->", pos + 4, System.StringComparison.Ordinal);
        if (end < 0)
        {
            Report(CmlDiagnostics.UnexpectedEof, new TextSpan(start, 4), "comment '<!--' is never closed with '-->'");
            pos = s.Length;
        }
        else pos = end + 3;
    }

    private void SkipWhitespace()
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    private void SkipWhitespaceAndComments()
    {
        while (true)
        {
            SkipWhitespace();
            if (StartsWith("<!--")) SkipComment();
            else return;
        }
    }

    private string CloseTagAt()
    {
        int end = s.IndexOf('>', pos);
        return end < 0 ? "</" : s.Substring(pos, end - pos + 1);
    }

    private char Peek(int offset) => pos + offset < s.Length ? s[pos + offset] : '\0';

    private bool StartsWith(string value) => string.CompareOrdinal(s, pos, value, 0, value.Length) == 0;

    private bool StartsWithWord(string word) =>
        StartsWith(word) && (pos + word.Length >= s.Length || !IsIdentChar(s[pos + word.Length]));

    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ':';
    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string DecodeEntity(string name)
    {
        switch (name)
        {
            case "lt": return "<";
            case "gt": return ">";
            case "amp": return "&";
            case "quot": return "\"";
            case "apos": return "'";
        }
        if (name.Length > 1 && name[0] == '#')
        {
            bool hex = name.Length > 2 && (name[1] == 'x' || name[1] == 'X');
            string digits = name.Substring(hex ? 2 : 1);
            if (int.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)
                && code >= 0 && code <= 0x10FFFF)
            {
                return char.ConvertFromUtf32(code);
            }
        }
        return null;
    }

    private void Report(DiagnosticDescriptor descriptor, TextSpan span, params object[] args)
    {
        diagnostics.Add(CmlLocation.Create(descriptor, path, text, span, args));
    }
}

internal static class CmlLocation
{
    public static Diagnostic Create(DiagnosticDescriptor descriptor, string path, SourceText text, TextSpan span, params object[] args)
    {
        if (span.End > text.Length) span = TextSpan.FromBounds(System.Math.Min(span.Start, text.Length), text.Length);
        var location = Location.Create(path, span, text.Lines.GetLinePositionSpan(span));
        return Diagnostic.Create(descriptor, location, args);
    }
}
