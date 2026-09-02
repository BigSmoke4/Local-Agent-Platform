using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Models;

namespace Platform.Web.Services.Tools;

public record SafeEditOutcome(string RelativePath, DiffResult Diff, FileWriteResult Write, Guid SnapshotId);

/// <summary>
/// Orchestrates FileReadTool + FileWriteTool + DiffTool + FileSnapshot
/// persistence so every agent-authored edit is diffed, hash-checked against
/// concurrent external changes, and rollback-capable — real implementations
/// of §16, §17, §59, not separate stubs that don't talk to each other.
/// </summary>
public class SafeFileEditService
{
    private readonly FileReadTool _reader;
    private readonly FileWriteTool _writer;
    private readonly DiffTool _diff;
    private readonly PlatformDbContext _db;
    private readonly ILogger<SafeFileEditService> _logger;

    public SafeFileEditService(
        FileReadTool reader,
        FileWriteTool writer,
        DiffTool diff,
        PlatformDbContext db,
        ILogger<SafeFileEditService> logger)
    {
        _reader = reader;
        _writer = writer;
        _diff = diff;
        _db = db;
        _logger = logger;
    }

    public async Task<SafeEditOutcome> ApplyAsync(
        Guid? agentSessionId,
        string relativePath,
        string newContent,
        CancellationToken ct = default)
    {
        string oldContent;
        try
        {
            oldContent = await _reader.ReadAsync(relativePath, ct);
        }
        catch (FileReadToolException)
        {
            oldContent = string.Empty; // new file
        }

        var expectedHash = oldContent.Length > 0 ? FileWriteTool.ComputeHash(oldContent) : null;

        // Snapshot BEFORE writing, so rollback is always possible even if the
        // write below is the first and only edit made this session.
        var snapshot = new FileSnapshot
        {
            AgentSessionId = agentSessionId,
            RelativePath = relativePath,
            Content = oldContent,
            ContentHash = expectedHash ?? string.Empty
        };
        _db.FileSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);

        var writeResult = await _writer.WriteAsync(relativePath, newContent, expectedHash, ct);
        var diffResult = _diff.Compute(oldContent, newContent);

        _logger.LogInformation(
            "SafeFileEditService applied edit to {Path}: +{Added}/-{Removed} lines",
            relativePath, diffResult.LinesAdded, diffResult.LinesRemoved);

        return new SafeEditOutcome(relativePath, diffResult, writeResult, snapshot.Id);
    }

    public async Task<bool> RollbackAsync(Guid snapshotId, CancellationToken ct = default)
    {
        var snapshot = await _db.FileSnapshots.FirstOrDefaultAsync(s => s.Id == snapshotId, ct);
        if (snapshot is null) return false;

        await _writer.WriteAsync(snapshot.RelativePath, snapshot.Content, expectedHash: null, ct);
        _logger.LogInformation("Rolled back {Path} to snapshot {SnapshotId}", snapshot.RelativePath, snapshotId);
        return true;
    }
}
