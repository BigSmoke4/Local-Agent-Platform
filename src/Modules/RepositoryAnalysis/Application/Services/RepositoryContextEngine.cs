using System.Text;
using System.Text.RegularExpressions;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Kernel.Files;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Modules.RepositoryAnalysis.Application.Services;

public sealed record RepositoryContextSnippet(string RelativePath, string Content, double Score);

public interface IRepositoryContextEngine
{
    Task<IReadOnlyList<RepositoryContextSnippet>> BuildContextAsync(Guid repositoryId, string query, int maxFiles = 6, int maxChars = 12000, CancellationToken ct = default);
}

/// <summary>
/// Repository-wide context engine. It uses the persisted file/symbol index to select
/// relevant files across the entire repository, then reads bounded excerpts from disk.
/// This is intentionally separate from durable Memory: context describes the current
/// codebase, while Memory stores previous decisions/executions/preferences.
/// </summary>
public sealed class RepositoryContextEngine : IRepositoryContextEngine
{
    private readonly PlatformDbContext _db;
    private readonly IWorkspaceRootPolicy _workspacePolicy;

    public RepositoryContextEngine(PlatformDbContext db, IWorkspaceRootPolicy workspacePolicy)
    {
        _db = db;
        _workspacePolicy = workspacePolicy;
    }

    public async Task<IReadOnlyList<RepositoryContextSnippet>> BuildContextAsync(
        Guid repositoryId, string query, int maxFiles = 6, int maxChars = 12000, CancellationToken ct = default)
    {
        var repo = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct)
            ?? throw new InvalidOperationException("Repository not found.");
        if (!_workspacePolicy.IsAllowed(repo.LocalPath))
            throw new InvalidOperationException("Repository is outside the configured workspace roots.");
        var tokens = Regex.Matches(query.ToLowerInvariant(), "[a-z0-9_]{3,}").Select(m => m.Value).Distinct().Take(20).ToArray();
        if (tokens.Length == 0) return Array.Empty<RepositoryContextSnippet>();

        var files = await _db.FileSnapshots.Where(f => f.RepositoryId == repositoryId && !f.IsDeleted)
            .Select(f => new { f.Id, f.RelativePath, f.SizeBytes }).ToListAsync(ct);
        var symbols = await _db.CodeSymbols.Where(s => s.RepositoryId == repositoryId)
            .Select(s => new { s.FileSnapshotId, s.Name, s.Signature, s.ContainingTypeName }).ToListAsync(ct);

        var symbolScores = symbols.GroupBy(s => s.FileSnapshotId).ToDictionary(g => g.Key, g => g.Sum(s =>
            tokens.Count(t => s.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                              (s.Signature?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
                              (s.ContainingTypeName?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)) * 3.0));

        var ranked = files.Select(f => new
        {
            File = f,
            Score = tokens.Count(t => f.RelativePath.Contains(t, StringComparison.OrdinalIgnoreCase)) * 2.0 + symbolScores.GetValueOrDefault(f.Id)
        }).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.File.SizeBytes).Take(maxFiles * 2).ToList();

        var result = new List<RepositoryContextSnippet>();
        var chars = 0;
        foreach (var item in ranked)
        {
            if (!WorkspacePathGuard.IsWithinWorkspace(repo.LocalPath, item.File.RelativePath)) continue;
            var fullPath = Path.GetFullPath(Path.Combine(repo.LocalPath, item.File.RelativePath));
            if (!WorkspacePathGuard.IsRegularFile(fullPath)) continue;
            try
            {
                if (new FileInfo(fullPath).Length > 1024 * 1024) continue;
                var text = await File.ReadAllTextAsync(fullPath, ct);
                if (text.Length > 6000) text = text[..6000] + "\n... [context excerpt truncated]";
                if (chars + text.Length > maxChars && result.Count > 0) continue;
                result.Add(new RepositoryContextSnippet(item.File.RelativePath, text, item.Score));
                chars += text.Length;
                if (result.Count >= maxFiles) break;
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
        }
        return result;
    }

    public static string FormatForPrompt(IReadOnlyList<RepositoryContextSnippet> snippets)
    {
        if (snippets.Count == 0) return string.Empty;
        var sb = new StringBuilder("Relevant current repository context:\n");
        foreach (var s in snippets)
            sb.AppendLine($"--- {s.RelativePath} ---\n{s.Content}");
        return sb.ToString();
    }
}
