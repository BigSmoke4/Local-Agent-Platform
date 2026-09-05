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
}
