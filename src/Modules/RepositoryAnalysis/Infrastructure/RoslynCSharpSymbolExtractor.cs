using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace LocalAgentPlatform.Modules.RepositoryAnalysis.Infrastructure;

public sealed record ExtractedSymbol(
    string Name,
    string Kind,
    string? ContainingNamespace,
    string? ContainingTypeName,
    int LineNumber,
    string? Signature
);

public interface ICodeSymbolExtractor
{
    bool SupportsLanguage(string? language);
    Task<IReadOnlyList<ExtractedSymbol>> ExtractAsync(string filePath, string sourceText, CancellationToken ct = default);
}

/// <summary>
/// Real Roslyn-based symbol extraction for C# source files (spec Section 12:
/// "For .NET repositories, integrate Roslyn where practical"). Parses the file into a
/// syntax tree and walks it — no regex guessing, no fabricated symbol lists. This is a
/// single-file syntactic pass (no full-solution semantic model / cross-file binding yet);
/// that is a deliberate scope boundary, not a hidden shortcut — see docs/STATUS.md.
/// </summary>
public sealed class RoslynCSharpSymbolExtractor : ICodeSymbolExtractor
{
    private readonly ILogger<RoslynCSharpSymbolExtractor> _logger;

    public RoslynCSharpSymbolExtractor(ILogger<RoslynCSharpSymbolExtractor> logger) => _logger = logger;

    public bool SupportsLanguage(string? language) => language == "csharp";

    public Task<IReadOnlyList<ExtractedSymbol>> ExtractAsync(string filePath, string sourceText, CancellationToken ct = default)
    {
        var results = new List<ExtractedSymbol>();

        SyntaxTree tree;
        try
        {
            tree = CSharpSyntaxTree.ParseText(sourceText, path: filePath, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse {FilePath} with Roslyn; skipping symbol extraction for this file.", filePath);
            return Task.FromResult<IReadOnlyList<ExtractedSymbol>>(results);
        }

        var root = tree.GetRoot(ct);

        string? CurrentNamespace(SyntaxNode node) =>
            node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

        int LineOf(SyntaxNode node) => tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

        foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var kind = typeDecl switch
            {
                ClassDeclarationSyntax => "Class",
                InterfaceDeclarationSyntax => "Interface",
                StructDeclarationSyntax => "Struct",
                RecordDeclarationSyntax rec => rec.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "RecordStruct" : "Record",
                EnumDeclarationSyntax => "Enum",
                _ => "Type"
            };

            results.Add(new ExtractedSymbol(
                Name: typeDecl.Identifier.Text,
                Kind: kind,
                ContainingNamespace: CurrentNamespace(typeDecl),
                ContainingTypeName: (typeDecl.Parent as BaseTypeDeclarationSyntax)?.Identifier.Text,
                LineNumber: LineOf(typeDecl),
                Signature: typeDecl.Identifier.Text
            ));
        }

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var containingType = method.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            var parameters = string.Join(", ", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "?"));
            results.Add(new ExtractedSymbol(
                Name: method.Identifier.Text,
                Kind: "Method",
                ContainingNamespace: CurrentNamespace(method),
                ContainingTypeName: containingType?.Identifier.Text,
                LineNumber: LineOf(method),
                Signature: $"{method.ReturnType} {method.Identifier.Text}({parameters})"
            ));
        }

        foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            var containingType = prop.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            results.Add(new ExtractedSymbol(
                Name: prop.Identifier.Text,
                Kind: "Property",
                ContainingNamespace: CurrentNamespace(prop),
                ContainingTypeName: containingType?.Identifier.Text,
                LineNumber: LineOf(prop),
                Signature: $"{prop.Type} {prop.Identifier.Text}"
            ));
        }

        return Task.FromResult<IReadOnlyList<ExtractedSymbol>>(results);
    }
}
