using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cosmiria.Generator;

/// <summary>What the generator needs to know about a [Cml]-annotated type.</summary>
internal sealed class CmlTarget
{
    public string Name;
    public string Namespace;
    public string CmlPath;
    public string SourceFilePath;
    public readonly List<Diagnostic> Diagnostics = new();
    /// <summary>Errors that make generating code pointless (wrong type shape, bad attribute).</summary>
    public bool Fatal;
    public Location PathLocation;
}

/// <summary>
/// Generates <c>Render(UIStack, EventStack)</c> for structs annotated with <c>[Cml("File.cml")]</c>
/// from the referenced CML markup (an AdditionalFiles item).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class CmlGenerator : IIncrementalGenerator
{
    private const string AttributeName = "Cosmiria.CmlAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var targets = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, ct) => CreateTarget(ctx, ct));

        // .cml markup plus .svg files that <Icon src="..."> embeds.
        var files = context.AdditionalTextsProvider
            .Where(static f => f.Path.EndsWith(".cml", StringComparison.OrdinalIgnoreCase)
                            || f.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .Collect();

        context.RegisterSourceOutput(targets.Combine(files).Combine(context.CompilationProvider),
            static (spc, input) => Execute(spc, input.Left.Left, input.Left.Right, input.Right));
    }

    private static CmlTarget CreateTarget(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var syntax = (TypeDeclarationSyntax)ctx.TargetNode;
        var target = new CmlTarget
        {
            Name = symbol.Name,
            Namespace = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
            SourceFilePath = syntax.SyntaxTree.FilePath,
        };
        Location nameLocation = syntax.Identifier.GetLocation();

        // Attribute argument.
        AttributeData attribute = ctx.Attributes[0];
        var attributeSyntax = attribute.ApplicationSyntaxReference?.GetSyntax(ct) as AttributeSyntax;
        target.PathLocation = attributeSyntax?.ArgumentList?.Arguments.FirstOrDefault()?.GetLocation()
                              ?? attributeSyntax?.GetLocation() ?? nameLocation;
        target.CmlPath = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
        if (string.IsNullOrWhiteSpace(target.CmlPath))
        {
            target.Diagnostics.Add(Diagnostic.Create(CmlDiagnostics.InvalidPath, target.PathLocation));
            target.Fatal = true;
        }

        // Type shape.
        if (!syntax.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            target.Diagnostics.Add(Diagnostic.Create(CmlDiagnostics.NotPartial, nameLocation, symbol.Name));
            target.Fatal = true;
        }
        if (symbol.TypeKind != TypeKind.Struct)
        {
            target.Diagnostics.Add(Diagnostic.Create(CmlDiagnostics.NotStruct, nameLocation, symbol.Name));
            target.Fatal = true;
        }
        if (symbol.ContainingType != null)
        {
            target.Diagnostics.Add(Diagnostic.Create(CmlDiagnostics.UnsupportedShape, nameLocation, symbol.Name, "must be a top-level type (nested [Cml] types are not supported)"));
            target.Fatal = true;
        }
        if (symbol.IsGenericType)
        {
            target.Diagnostics.Add(Diagnostic.Create(CmlDiagnostics.UnsupportedShape, nameLocation, symbol.Name, "cannot be generic"));
            target.Fatal = true;
        }

        foreach (var member in symbol.GetMembers())
        {
            if (member is not IMethodSymbol m || m.IsStatic) continue;
            if (m.Name == "Render" && m.Parameters.Length == 2 && !m.IsImplicitlyDeclared && m.Locations.Any(l => l.IsInSource))
            {
                target.Diagnostics.Add(Diagnostic.Create(CmlDiagnostics.RenderAlreadyDefined, m.Locations[0], symbol.Name));
                target.Fatal = true;
            }
        }
        return target;
    }

    private static void Execute(SourceProductionContext spc, CmlTarget target, ImmutableArray<AdditionalText> files, Compilation compilation)
    {
        foreach (var d in target.Diagnostics) spc.ReportDiagnostic(d);
        if (target.Fatal) return;

        string hintName = (target.Namespace != null ? target.Namespace + "." : "") + target.Name + ".cml.g.cs";
        AdditionalText file = FindFile(target, files, out string expectedPath);
        SourceText text = file?.GetText(spc.CancellationToken);

        var diagnostics = new List<Diagnostic>();
        var svgFiles = files.Where(f => f.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)).ToList();
        var emitter = new CmlEmitter(text ?? SourceText.From(""), file?.Path ?? "", diagnostics, compilation, target, svgFiles);

        if (text == null)
        {
            spc.ReportDiagnostic(Diagnostic.Create(CmlDiagnostics.FileNotFound, target.PathLocation, target.CmlPath, expectedPath));
            // Still implement IComponent so the only error is the missing file.
            var missing = new[] { $"File '{target.CmlPath}' not found (CML200)" };
            spc.AddSource(hintName, SourceText.From(emitter.BuildSource(includeBody: false, missing), Encoding.UTF8));
            return;
        }

        CmlDocument doc = CmlParser.Parse(text, file.Path, diagnostics);
        bool syntaxOk = !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        if (syntaxOk) emitter.EmitDocument(doc);

        bool ok = !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        foreach (var d in diagnostics) spc.ReportDiagnostic(d);

        // On CML errors, emit a Render that shows the errors in-app (no C# error cascade).
        List<string> errorLines = null;
        if (!ok)
        {
            errorLines = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d =>
                {
                    var pos = d.Location.GetLineSpan().StartLinePosition;
                    return $"({pos.Line + 1},{pos.Character + 1}) {d.Id}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}";
                })
                .ToList();
        }
        spc.AddSource(hintName, SourceText.From(emitter.BuildSource(includeBody: ok, errorLines), Encoding.UTF8));
    }

    private static AdditionalText FindFile(CmlTarget target, ImmutableArray<AdditionalText> files, out string expectedPath)
    {
        string relative = target.CmlPath.Replace('\\', '/');
        expectedPath = relative;
        if (!string.IsNullOrEmpty(target.SourceFilePath))
        {
            try
            {
                string dir = Path.GetDirectoryName(target.SourceFilePath) ?? "";
                expectedPath = Path.GetFullPath(Path.Combine(dir, relative));
            }
            catch (Exception)
            {
                // Invalid path characters; fall through to suffix matching.
            }
        }

        AdditionalText match = null;
        foreach (var f in files)
        {
            if (Normalize(f.Path) == Normalize(expectedPath)) return f;
        }
        foreach (var f in files)
        {
            if (string.Equals(Normalize(f.Path), Normalize(expectedPath), StringComparison.OrdinalIgnoreCase)) return f;
        }
        // In-memory or unusual layouts: unique suffix match.
        foreach (var f in files)
        {
            if (Normalize(f.Path).EndsWith("/" + relative.TrimStart('/'), StringComparison.Ordinal))
            {
                if (match != null) return null;
                match = f;
            }
        }
        return match;
    }

    private static string Normalize(string p)
    {
        try { p = Path.GetFullPath(p); } catch (Exception) { }
        return p.Replace('\\', '/');
    }
}
