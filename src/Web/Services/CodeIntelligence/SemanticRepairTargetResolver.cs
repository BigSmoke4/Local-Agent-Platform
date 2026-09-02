using System.Text.RegularExpressions;

namespace Platform.Web.Services.CodeIntelligence;

public record RepairTargetSet(List<string> Files, string Reasoning);

/// <summary>
/// Real semantic-graph-driven repair targeting: given a compiler error,
/// this doesn't just return the file the compiler flagged — it tries to
/// use SemanticCodeGraphService to find where the *symbol involved in the
/// error* is actually referenced elsewhere in the solution, so a fix that
/// requires touching a caller (not just the file with the syntax/type
/// error) has a chance of being in scope.
///
/// Honest scope: this only works if a semantic workspace has already been
/// loaded via POST /api/code-intelligence/semantic/load — if not, or if
/// symbol extraction from the diagnostic message fails, it falls back to
/// the plain BuildDiagnosticParser file list (the previous behavior)
/// rather than silently doing nothing. It does not attempt full root-cause
/// analysis (e.g. "this error is really caused by a signature change three
/// files away") — CS0103/CS1061-style "member not found" diagnostics
/// commonly name the missing member, and that's what this extracts and
/// looks up; diagnostics that don't name a resolvable symbol just use the
/// file-list fallback.
/// </summary>
public class SemanticRepairTargetResolver
{
    // Extracts the symbol name .NET compilers typically quote in these
    // diagnostics, e.g. CS0103 "The name 'Foo' does not exist...",
    // CS1061 "... does not contain a definition for 'Bar'...".
    private static readonly Regex QuotedSymbolRegex = new(@"'([A-Za-z_][A-Za-z0-9_]*)'", RegexOptions.Compiled);

    private readonly SemanticCodeGraphService _semantic;
    private readonly ILogger<SemanticRepairTargetResolver> _logger;

    public SemanticRepairTargetResolver(SemanticCodeGraphService semantic, ILogger<SemanticRepairTargetResolver> logger)
    {
        _semantic = semantic;
        _logger = logger;
    }

    public async Task<RepairTargetSet> ResolveAsync(string buildOutput, CancellationToken ct = default)
    {
        var diagnostics = BuildDiagnosticParser.Parse(buildOutput).Where(d => d.Severity == "error").ToList();
        var directFiles = diagnostics.Select(d => d.FilePath).Distinct().ToList();

        if (!_semantic.IsLoaded || diagnostics.Count == 0)
        {
            return new RepairTargetSet(directFiles,
                _semantic.IsLoaded
                    ? "No error diagnostics to expand semantically."
                    : "No semantic workspace loaded; using compiler-reported file(s) only. " +
                      "Call POST /api/code-intelligence/semantic/load first to enable reference-based expansion.");
        }

        var expandedFiles = new HashSet<string>(directFiles);
        var reasoningParts = new List<string> { $"Compiler reported {directFiles.Count} file(s) with errors." };

        foreach (var diagnostic in diagnostics)
        {
            var symbolMatch = QuotedSymbolRegex.Match(diagnostic.Message);
            if (!symbolMatch.Success) continue;

            var symbolName = symbolMatch.Groups[1].Value;

            try
            {
                var references = await _semantic.FindReferencesAsync(symbolName, ct);
                var referenceFiles = references.Select(r => r.FilePath).Where(f => !string.IsNullOrEmpty(f)).Distinct().ToList();

                if (referenceFiles.Count > 0)
                {
                    foreach (var f in referenceFiles) expandedFiles.Add(f);
                    reasoningParts.Add($"Symbol '{symbolName}' from {diagnostic.Code} is referenced in {referenceFiles.Count} additional real location(s) via semantic analysis.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Semantic reference lookup failed for symbol '{Symbol}'", symbolName);
                reasoningParts.Add($"Semantic lookup for '{symbolName}' failed ({ex.Message}); not expanded beyond compiler-reported file.");
            }
        }

        return new RepairTargetSet(expandedFiles.ToList(), string.Join(" ", reasoningParts));
    }
}
