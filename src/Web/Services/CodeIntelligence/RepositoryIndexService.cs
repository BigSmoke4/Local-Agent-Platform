using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Models;
using Platform.Web.Services.Tools;

namespace Platform.Web.Services.CodeIntelligence;

public record IndexRunResult(int FilesScanned, int FilesReindexed, int FilesSkippedUnchanged, int SymbolsIndexed);

/// <summary>
/// Real incremental repository indexer per §60: hashes each .cs file,
/// compares to the hash already stored for that file's symbols, and only
/// re-parses files that actually changed. Uses RoslynSyntaxIndexer for real
/// AST-based extraction — persisted to CodeSymbol rows in PostgreSQL.
/// </summary>
public class RepositoryIndexService
{
    private readonly string _workspaceRoot;
    private readonly RoslynSyntaxIndexer _indexer;
    private readonly PlatformDbContext _db;
    private readonly ILogger<RepositoryIndexService> _logger;

    private static readonly string[] IgnoredSegments = { "bin", "obj", ".git", "node_modules", ".vs" };

    public RepositoryIndexService(
        IConfiguration config,
        RoslynSyntaxIndexer indexer,
        PlatformDbContext db,
        ILogger<RepositoryIndexService> logger)
    {
        _workspaceRoot = Path.GetFullPath(
            config["Workspace:Root"] ?? Path.Combine(AppContext.BaseDirectory, "workspace"));
        Directory.CreateDirectory(_workspaceRoot);
        _indexer = indexer;
        _db = db;
        _logger = logger;
    }

    public async Task<IndexRunResult> RunAsync(CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(_workspaceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IgnoredSegments.Any(seg => f.Contains($"{Path.DirectorySeparatorChar}{seg}{Path.DirectorySeparatorChar}")))
            .ToList();

        int reindexed = 0, skipped = 0, symbolCount = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(_workspaceRoot, file);
            var content = await File.ReadAllTextAsync(file, ct);
            var hash = FileWriteTool.ComputeHash(content);

            var existingHash = await _db.CodeSymbols
                .Where(s => s.FilePath == relativePath)
                .Select(s => s.FileContentHash)
                .FirstOrDefaultAsync(ct);

            if (existingHash == hash)
            {
                skipped++;
                continue;
            }

            // File changed (or never indexed) — remove stale symbols, re-parse.
            var stale = _db.CodeSymbols.Where(s => s.FilePath == relativePath);
            _db.CodeSymbols.RemoveRange(stale);

            List<IndexedSymbol> extracted;
            try
            {
                extracted = _indexer.IndexSource(content);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse {File} — skipping (malformed source, not indexed)", relativePath);
                continue;
            }

            foreach (var symbol in extracted)
            {
                _db.CodeSymbols.Add(new CodeSymbol
                {
                    FilePath = relativePath,
                    SymbolName = symbol.SymbolName,
                    Kind = symbol.Kind,
                    ContainingType = symbol.ContainingType,
                    Namespace = symbol.Namespace,
                    StartLine = symbol.StartLine,
                    EndLine = symbol.EndLine,
                    FileContentHash = hash
                });
                symbolCount++;
            }

            reindexed++;
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Repository index run: {Scanned} scanned, {Reindexed} reindexed, {Skipped} unchanged, {Symbols} symbols",
            files.Count, reindexed, skipped, symbolCount);

        return new IndexRunResult(files.Count, reindexed, skipped, symbolCount);
    }
}
