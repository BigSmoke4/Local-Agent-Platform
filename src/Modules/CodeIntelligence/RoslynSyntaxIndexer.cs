using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Platform.Web.Services.CodeIntelligence;

public record IndexedSymbol(string SymbolName, string Kind, string? ContainingType, string? Namespace, int StartLine, int EndLine);

/// <summary>
/// Real Roslyn syntax-tree walker (Microsoft.CodeAnalysis.CSharp). This
/// parses actual C# syntax and extracts real declarations with real line
/// spans — it is NOT a regex approximation like SearchSymbolTool.
///
/// Scope honesty: this is syntax-level analysis only. It does not build a
/// full Compilation across project references, so it cannot resolve
/// "which overload", "what does this type inherit at runtime", or
/// cross-assembly symbols — that requires MSBuildWorkspace + full project
/// loading, a materially larger undertaking (real MSBuild resolution,
/// NuGet restore awareness, multi-target frameworks) that is not done here.
/// </summary>
public class RoslynSyntaxIndexer
{
    public List<IndexedSymbol> IndexSource(string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetCompilationUnitRoot();

        var walker = new DeclarationWalker(tree);
        walker.Visit(root);
        return walker.Symbols;
    }

    private class DeclarationWalker : CSharpSyntaxWalker
    {
        private readonly SyntaxTree _tree;
        private readonly Stack<string> _typeStack = new();
        private string? _namespace;

        public List<IndexedSymbol> Symbols { get; } = new();

        public DeclarationWalker(SyntaxTree tree)
        {
            _tree = tree;
        }

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            _namespace = node.Name.ToString();
            base.VisitFileScopedNamespaceDeclaration(node);
        }

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            var previous = _namespace;
            _namespace = node.Name.ToString();
            base.VisitNamespaceDeclaration(node);
            _namespace = previous;
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) => VisitType(node, node.Identifier.Text, "Class", () => base.VisitClassDeclaration(node));
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitType(node, node.Identifier.Text, "Interface", () => base.VisitInterfaceDeclaration(node));
        public override void VisitStructDeclaration(StructDeclarationSyntax node) => VisitType(node, node.Identifier.Text, "Struct", () => base.VisitStructDeclaration(node));
        public override void VisitRecordDeclaration(RecordDeclarationSyntax node) => VisitType(node, node.Identifier.Text, "Record", () => base.VisitRecordDeclaration(node));

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            AddSymbol(node.Identifier.Text, "Enum", node.Span);
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            AddSymbol(node.Identifier.Text, "Method", node.Span);
            base.VisitMethodDeclaration(node);
        }

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            AddSymbol(node.Identifier.Text, "Constructor", node.Span);
            base.VisitConstructorDeclaration(node);
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            AddSymbol(node.Identifier.Text, "Property", node.Span);
            base.VisitPropertyDeclaration(node);
        }

        private void VisitType(SyntaxNode node, string name, string kind, Action visitChildren)
        {
            AddSymbol(name, kind, node.Span);
            _typeStack.Push(name);
            visitChildren();
            _typeStack.Pop();
        }

        private void AddSymbol(string name, string kind, TextSpan span)
        {
            var startLine = _tree.GetLineSpan(span).StartLinePosition.Line + 1;
            var endLine = _tree.GetLineSpan(span).EndLinePosition.Line + 1;

            Symbols.Add(new IndexedSymbol(
                SymbolName: name,
                Kind: kind,
                ContainingType: _typeStack.Count > 0 ? _typeStack.Peek() : null,
                Namespace: _namespace,
                StartLine: startLine,
                EndLine: endLine));
        }
    }
}
