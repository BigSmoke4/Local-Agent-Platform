using LocalAgentPlatform.Modules.RepositoryAnalysis.Infrastructure;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LocalAgentPlatform.Modules.RepositoryAnalysis.Application.Services;

public interface IRepositoryIndexingService
{
    /// <summary>
    /// Runs a full incremental index pass over a registered repository: scans the
    /// filesystem, hashes every file, compares against the last-known FileSnapshot per
    /// path, and only re-parses symbols for files whose hash actually changed
    /// (spec Section 60). Deleted files are soft-marked, not silently dropped.
    /// </summary>
    Task<RepositoryIndexingJob> RunIndexingAsync(Guid repositoryId, CancellationToken ct = default);
}

public sealed class RepositoryIndexingService : IRepositoryIndexingService
{
    private readonly PlatformDbContext _db;
    private readonly IRepositoryFileScanner _scanner;
    private readonly IReadOnlyList<ICodeSymbolExtractor> _extractors;
    private readonly ILogger<RepositoryIndexingService> _logger;

    public RepositoryIndexingService(
        PlatformDbContext db,
        IRepositoryFileScanner scanner,
        IEnumerable<ICodeSymbolExtractor> extractors,
        ILogger<RepositoryIndexingService> logger)
    {
        _db = db;
        _scanner = scanner;
        _extractors = extractors.ToList();
        _logger = logger;
    }

    public async Task<RepositoryIndexingJob> RunIndexingAsync(Guid repositoryId, CancellationToken ct = default)
    {
        var repository = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found.");

        var job = new RepositoryIndexingJob { RepositoryId = repositoryId, Status = "Scanning", StartedAtUtc = DateTimeOffset.UtcNow };
        _db.RepositoryIndexingJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        try
        {
            var existingSnapshots = await _db.FileSnapshots
                .Where(f => f.RepositoryId == repositoryId && !f.IsDeleted)
                .ToDictionaryAsync(f => f.RelativePath, ct);

            var seenPaths = new HashSet<string>();
            int filesScanned = 0, filesChanged = 0, symbolsExtracted = 0;

            await foreach (var scanned in _scanner.ScanAsync(repository.LocalPath, ct))
            {
                ct.ThrowIfCancellationRequested();
                filesScanned++;
                seenPaths.Add(scanned.RelativePath);

                existingSnapshots.TryGetValue(scanned.RelativePath, out var existing);

                if (existing is not null && existing.ContentHash == scanned.ContentHash)
                {
                    continue; // unchanged — skip re-parsing entirely (Section 60 requirement)
                }

                filesChanged++;

                FileSnapshot snapshot;
                if (existing is not null)
                {
                    snapshot = existing;
                    snapshot.ContentHash = scanned.ContentHash;
                    snapshot.SizeBytes = scanned.SizeBytes;
                    snapshot.Language = scanned.Language;
                    snapshot.LastIndexedAtUtc = DateTimeOffset.UtcNow;
                    // Remove stale symbols for this file before re-extracting
                    var staleSymbols = await _db.CodeSymbols
                        .Where(s => s.FileSnapshotId == snapshot.Id).ToListAsync(ct);
                    _db.CodeSymbols.RemoveRange(staleSymbols);
                }
                else
                {
                    snapshot = new FileSnapshot
                    {
                        RepositoryId = repositoryId,
                        RelativePath = scanned.RelativePath,
                        ContentHash = scanned.ContentHash,
                        SizeBytes = scanned.SizeBytes,
                        Language = scanned.Language
                    };
                    _db.FileSnapshots.Add(snapshot);
                }

                var extractor = _extractors.FirstOrDefault(e => e.SupportsLanguage(scanned.Language));
                if (extractor is not null && scanned.SizeBytes <= Domain.IndexingIgnoreRules.MaxParsableFileSizeBytes)
                {
                    var fullPath = Path.Combine(repository.LocalPath, scanned.RelativePath);
                    try
                    {
                        var text = await File.ReadAllTextAsync(fullPath, ct);
                        var extracted = await extractor.ExtractAsync(fullPath, text, ct);
                        foreach (var sym in extracted)
                        {
                            _db.CodeSymbols.Add(new CodeSymbol
                            {
                                RepositoryId = repositoryId,
                                FileSnapshotId = snapshot.Id,
                                Name = sym.Name,
                                Kind = sym.Kind,
                                ContainingNamespace = sym.ContainingNamespace,
                                ContainingTypeName = sym.ContainingTypeName,
                                LineNumber = sym.LineNumber,
                                Signature = sym.Signature
                            });
                            symbolsExtracted++;
                        }
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "Could not read {Path} for symbol extraction; hash/metadata still recorded.", fullPath);
                    }
                }

                // Save incrementally so a crash mid-scan doesn't lose all progress (Section 58).
                await _db.SaveChangesAsync(ct);
            }

            var deletedPaths = existingSnapshots.Keys.Except(seenPaths).ToList();
            int filesDeleted = 0;
            foreach (var path in deletedPaths)
            {
                existingSnapshots[path].IsDeleted = true;
                filesDeleted++;
            }
            if (filesDeleted > 0) await _db.SaveChangesAsync(ct);

            await RebuildCrossFileRelationshipsAsync(repository, ct);

            repository.LastIndexedAtUtc = DateTimeOffset.UtcNow;
            job.Status = "Completed";
            job.FilesScanned = filesScanned;
            job.FilesChanged = filesChanged;
            job.FilesDeleted = filesDeleted;
            job.SymbolsExtracted = symbolsExtracted;
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Repository indexing failed for {RepositoryId}", repositoryId);
            job.Status = "Failed";
            job.ErrorMessage = ex.Message;
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return job;
    }

    private async Task RebuildCrossFileRelationshipsAsync(Repository repository, CancellationToken ct)
    {
        var previous = await _db.CodeRelationships.Where(r => r.RepositoryId == repository.Id).ToListAsync(ct);
        _db.CodeRelationships.RemoveRange(previous);

        var symbols = await _db.CodeSymbols.Where(s => s.RepositoryId == repository.Id)
            .Where(s => s.Name.Length >= 4).Take(5000).ToListAsync(ct);
        var files = await _db.FileSnapshots.Where(f => f.RepositoryId == repository.Id && !f.IsDeleted)
            .Select(f => new { f.Id, f.RelativePath }).ToListAsync(ct);
        var symbolsByName = symbols.GroupBy(s => s.Name, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var added = new HashSet<(Guid From, Guid To)>();
        var relationshipCount = 0;

        foreach (var file in files)
        {
            if (relationshipCount >= 20000) break;
            var path = Path.Combine(repository.LocalPath, file.RelativePath);
            string text;
            try { text = await File.ReadAllTextAsync(path, ct); } catch { continue; }
            var fromSymbols = symbols.Where(s => s.FileSnapshotId == file.Id).ToList();
            if (fromSymbols.Count == 0) continue;

            foreach (var pair in symbolsByName)
            {
                var target = pair.Value;
                if (target.FileSnapshotId == file.Id) continue;
                if (!System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(pair.Key)}\b")) continue;
                foreach (var from in fromSymbols.Take(20))
                {
                    if (!added.Add((from.Id, target.Id))) continue;
                    _db.CodeRelationships.Add(new CodeRelationship
                    {
                        RepositoryId = repository.Id, FromSymbolId = from.Id, ToSymbolId = target.Id, RelationshipType = "References"
                    });
                    relationshipCount++;
                    if (relationshipCount >= 20000) break;
                }
                if (relationshipCount >= 20000) break;
            }
        }
        await _db.SaveChangesAsync(ct);
    }
}

