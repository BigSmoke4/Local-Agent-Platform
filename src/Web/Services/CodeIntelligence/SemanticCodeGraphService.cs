using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

namespace Platform.Web.Services.CodeIntelligence;

public record SemanticSymbolInfo(
    string Name, string Kind, string ContainingType, string FilePath, int Line,
    List<string> BaseTypes, List<string> Interfaces);

public record SemanticReference(string FilePath, int Line, string ContextText);

public record SemanticIndexResult(bool Succeeded, string? Error, int ProjectsLoaded, int DocumentsLoaded, List<string> LoadWarnings);

/// <summary>
/// Real cross-file semantic analysis via Microsoft.CodeAnalysis.MSBuild —
/// the actual MSBuildWorkspace machinery, not a syntax-only approximation.
/// This is what RoslynSyntaxIndexer explicitly says it is NOT (see
/// CODE_INTELLIGENCE.md). It resolves real symbols across the loaded
/// solution/project: base types, implemented interfaces, and "find all
/// references" across files.
///
/// Honest environment caveat: MSBuildWorkspace needs a real MSBuild
/// installation resolvable via Microsoft.Build.Locator (i.e. a .NET SDK
/// installed on the host — the same one you're using to `dotnet build`
/// this repo). If registration or project load fails (missing SDK,
/// unresolvable NuGet restore, multi-targeting issues), this returns a
/// failed SemanticIndexResult with the real exception message rather than
/// silently falling back to pretending semantic data exists.
/// </summary>
public class SemanticCodeGraphService
{
    private static bool _msbuildRegistered;
    private static readonly object RegisterLock = new();

    private MSBuildWorkspace? _workspace;
    private readonly ILogger<SemanticCodeGraphService> _logger;
    private bool _loadedSuccessfully;

    /// <summary>True only after LoadSolutionAsync has actually succeeded — not just after
    /// MSBuildWorkspace.Create(), which can succeed even if the subsequent solution
    /// open fails.</summary>
    public bool IsLoaded => _loadedSuccessfully;

    public SemanticCodeGraphService(ILogger<SemanticCodeGraphService> logger)
    {
        _logger = logger;
    }

    private static void EnsureMsBuildRegistered()
    {
        if (_msbuildRegistered) return;
        lock (RegisterLock)
        {
            if (_msbuildRegistered) return;
            if (!MSBuildLocator.IsRegistered)
            {
                var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
                if (instances.Count == 0)
                    throw new InvalidOperationException(
                        "No MSBuild/.NET SDK instance found on this machine. " +
                        "SemanticCodeGraphService requires a real .NET SDK install " +
                        "(the same one used to `dotnet build` this repo).");

                MSBuildLocator.RegisterInstance(instances.First());
            }
            _msbuildRegistered = true;
        }
    }

    public async Task<SemanticIndexResult> LoadSolutionAsync(string solutionOrProjectPath, CancellationToken ct = default)
    {
        var warnings = new List<string>();

        try
        {
            EnsureMsBuildRegistered();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MSBuild registration failed");
            return new SemanticIndexResult(false, ex.Message, 0, 0, warnings);
        }

        _workspace?.Dispose();
        _workspace = MSBuildWorkspace.Create();
        _loadedSuccessfully = false;
        _workspace.WorkspaceFailed += (_, e) => warnings.Add(e.Diagnostic.Message);

        try
        {
            Solution solution;
            if (solutionOrProjectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                solution = await _workspace.OpenSolutionAsync(solutionOrProjectPath, cancellationToken: ct);
            }
            else
            {
                var project = await _workspace.OpenProjectAsync(solutionOrProjectPath, cancellationToken: ct);
                solution = project.Solution;
            }

            var documentCount = solution.Projects.Sum(p => p.Documents.Count());
            _logger.LogInformation(
                "Semantic workspace loaded: {Projects} project(s), {Documents} document(s), {Warnings} warning(s)",
                solution.Projects.Count(), documentCount, warnings.Count);

            _loadedSuccessfully = true;
            return new SemanticIndexResult(true, null, solution.Projects.Count(), documentCount, warnings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load solution/project {Path}", solutionOrProjectPath);
            return new SemanticIndexResult(false, ex.Message, 0, 0, warnings);
        }
    }    /// <summary>Real semantic lookup: base types and interfaces actually resolved by the compiler, not text-guessed.</summary>
    public async Task<List<SemanticSymbolInfo>> FindTypeAsync(string typeName, CancellationToken ct = default)
    {
        if (_workspace is null)
            throw new InvalidOperationException("Call LoadSolutionAsync first.");

        var results = new List<SemanticSymbolInfo>();

        foreach (var project in _workspace.CurrentSolution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            var symbols = compilation.GetSymbolsWithName(n => n == typeName, SymbolFilter.Type, ct);

            foreach (var symbol in symbols.OfType<INamedTypeSymbol>())
            {
                var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is null) continue;

                results.Add(new SemanticSymbolInfo(
                    Name: symbol.Name,
                    Kind: symbol.TypeKind.ToString(),
                    ContainingType: symbol.ContainingType?.Name ?? string.Empty,
                    FilePath: location.SourceTree?.FilePath ?? string.Empty,
                    Line: location.GetLineSpan().StartLinePosition.Line + 1,
                    BaseTypes: symbol.BaseType is { SpecialType: SpecialType.None } bt ? new List<string> { bt.ToDisplayString() } : new List<string>(),
                    Interfaces: symbol.AllInterfaces.Select(i => i.ToDisplayString()).ToList()));
            }
        }

        return results;
    }

    /// <summary>Real "find all references" across the loaded solution via Roslyn's SymbolFinder.</summary>
    public async Task<List<SemanticReference>> FindReferencesAsync(string symbolName, CancellationToken ct = default)
    {
        if (_workspace is null)
            throw new InvalidOperationException("Call LoadSolutionAsync first.");

        var solution = _workspace.CurrentSolution;
        var results = new List<SemanticReference>();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            var candidates = compilation.GetSymbolsWithName(n => n == symbolName, cancellationToken: ct);

            foreach (var symbol in candidates)
            {
                var references = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
                foreach (var reference in references)
                {
                    foreach (var location in reference.Locations)
                    {
                        var lineSpan = location.Location.GetLineSpan();
                        var sourceText = await location.Document.GetTextAsync(ct);
                        var lineText = lineSpan.StartLinePosition.Line < sourceText.Lines.Count
                            ? sourceText.Lines[lineSpan.StartLinePosition.Line].ToString().Trim()
                            : string.Empty;

                        results.Add(new SemanticReference(
                            FilePath: location.Document.FilePath ?? string.Empty,
                            Line: lineSpan.StartLinePosition.Line + 1,
                            ContextText: lineText));
                    }
                }
            }
        }

        return results;
    }
}
