using LocalAgentPlatform.Modules.RepositoryAnalysis.Infrastructure;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Files;
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
    private static readonly System.Text.RegularExpressions.Regex CodeIdentifierRegex = new(
        @"[\p{L}_][\p{L}\p{N}_]*",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly PlatformDbContext _db;
    private readonly IRepositoryFileScanner _scanner;
    private readonly IReadOnlyList<ICodeSymbolExtractor> _extractors;
    private readonly ILogger<RepositoryIndexingService> _logger;
    private readonly IWorkspaceRootPolicy _workspacePolicy;

    public RepositoryIndexingService(
        PlatformDbContext db,
        IRepositoryFileScanner scanner,
        IEnumerable<ICodeSymbolExtractor> extractors,
        ILogger<RepositoryIndexingService> logger,
        IWorkspaceRootPolicy workspacePolicy)
    {
        _db = db;
        _scanner = scanner;
        _extractors = extractors.ToList();
        _logger = logger;
        _workspacePolicy = workspacePolicy;
    }

    public async Task<RepositoryIndexingJob> RunIndexingAsync(Guid repositoryId, CancellationToken ct = default)
    {
        var repository = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found.");
        if (!_workspacePolicy.IsAllowed(repository.LocalPath))
            throw new InvalidOperationException("Repository is outside the configured workspace roots.");

        var job = new RepositoryIndexingJob { RepositoryId = repositoryId, Status = "Scanning", StartedAtUtc = DateTimeOffset.UtcNow };
        _db.RepositoryIndexingJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        try
        {
            // Keep soft-deleted rows in the path map so a file that reappears reuses
            // its unique (RepositoryId, RelativePath) snapshot instead of violating the
            // database's uniqueness constraint.
            var existingSnapshots = await _db.FileSnapshots
                .Where(f => f.RepositoryId == repositoryId)
                .ToDictionaryAsync(f => f.RelativePath, ct);

            var seenPaths = new HashSet<string>();
            int filesScanned = 0, filesChanged = 0, symbolsExtracted = 0;

            await foreach (var scanned in _scanner.ScanAsync(repository.LocalPath, ct))
            {
                ct.ThrowIfCancellationRequested();
                filesScanned++;
                seenPaths.Add(scanned.RelativePath);
                if (!WorkspacePathGuard.IsWithinWorkspace(repository.LocalPath, scanned.RelativePath)) continue;

                existingSnapshots.TryGetValue(scanned.RelativePath, out var existing);

                if (existing is not null && !existing.IsDeleted && existing.ContentHash == scanned.ContentHash)
                {
                    continue; // unchanged — skip re-parsing entirely (Section 60 requirement)
                }

                filesChanged++;

                FileSnapshot snapshot;
                if (existing is not null)
                {
                    snapshot = existing;
                    snapshot.IsDeleted = false;
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
                        // Re-check immediately before opening in case a repository entry
                        // changed to a FIFO/device after the scanner hashed it.
                        if (!WorkspacePathGuard.IsRegularFile(fullPath)) continue;
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
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger.LogWarning(ex, "Could not access {Path} for symbol extraction; hash/metadata still recorded.", fullPath);
                    }
                }

                // Save incrementally so a crash mid-scan doesn't lose all progress (Section 58).
                await _db.SaveChangesAsync(ct);
            }

            var deletedPaths = existingSnapshots
                .Where(pair => !pair.Value.IsDeleted)
                .Select(pair => pair.Key)
                .Except(seenPaths)
                .ToList();
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Status = "Cancelled";
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
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
        var rebuilt = new List<CodeRelationship>();

        var symbols = await _db.CodeSymbols
            .Where(s => s.RepositoryId == repository.Id && s.FileSnapshot != null && !s.FileSnapshot.IsDeleted)
            .Where(s => s.Name.Length >= 4).Take(5000).ToListAsync(ct);
        var files = await _db.FileSnapshots
            .Where(f => f.RepositoryId == repository.Id && !f.IsDeleted &&
                        f.SizeBytes <= Domain.IndexingIgnoreRules.MaxParsableFileSizeBytes)
            .Select(f => new { f.Id, f.RelativePath }).ToListAsync(ct);
        var symbolsByFile = symbols.GroupBy(s => s.FileSnapshotId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var symbolsByName = symbols.GroupBy(s => s.Name, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var added = new HashSet<(Guid From, Guid To)>();
        var relationshipCount = 0;

        foreach (var file in files)
        {
            if (relationshipCount >= 20000) break;
            if (!WorkspacePathGuard.IsWithinWorkspace(repository.LocalPath, file.RelativePath) ||
                !symbolsByFile.TryGetValue(file.Id, out var fromSymbols)) continue;
            var path = Path.Combine(repository.LocalPath, file.RelativePath);
            if (!WorkspacePathGuard.IsRegularFile(path)) continue;
            string text;
            try { text = await File.ReadAllTextAsync(path, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException ex) { _logger.LogDebug(ex, "Skipping unreadable indexed file {Path} while rebuilding cross-file references.", file.RelativePath); continue; }
            catch (UnauthorizedAccessException ex) { _logger.LogDebug(ex, "Skipping inaccessible indexed file {Path} while rebuilding cross-file references.", file.RelativePath); continue; }
            var identifiers = CodeIdentifierRegex.Matches(text).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

            foreach (var pair in symbolsByName)
            {
                var target = pair.Value;
                if (target.FileSnapshotId == file.Id || !identifiers.Contains(pair.Key)) continue;
                foreach (var from in fromSymbols.Take(20))
                {
                    if (!added.Add((from.Id, target.Id))) continue;
                    rebuilt.Add(new CodeRelationship
                    {
                        RepositoryId = repository.Id, FromSymbolId = from.Id, ToSymbolId = target.Id, RelationshipType = "References"
                    });
                    relationshipCount++;
                    if (relationshipCount >= 20000) break;
                }
                if (relationshipCount >= 20000) break;
            }
        }
        // Replace the persisted graph only after the full rebuild completed. If a
        // cancellation or read error interrupts enumeration, the previous complete
        // graph remains instead of committing a partial relationship set.
        _db.CodeRelationships.RemoveRange(previous);
        _db.CodeRelationships.AddRange(rebuilt);
        await _db.SaveChangesAsync(ct);
    }
}

