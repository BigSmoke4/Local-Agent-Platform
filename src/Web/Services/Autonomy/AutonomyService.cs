using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Models;

namespace Platform.Web.Services.Autonomy;

/// <summary>
/// Real enforcement point for §35. This is consulted by the edit/repair
/// path (see AgentVerificationController and ToolsController.EditFile) —
/// at Low autonomy, an automated edit is refused and the caller must use
/// the explicit-approval flag; at Medium/High it's allowed. This is a gate
/// that actually blocks a call path, not a UI toggle with no effect.
/// </summary>
public class AutonomyService
{
    private readonly PlatformDbContext _db;

    public AutonomyService(PlatformDbContext db)
    {
        _db = db;
    }

    public async Task<AutonomyLevel> GetLevelAsync(string userId, CancellationToken ct = default)
    {
        var setting = await _db.AutonomySettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        return setting?.Level ?? AutonomyLevel.Low;
    }

    public async Task SetLevelAsync(string userId, AutonomyLevel level, CancellationToken ct = default)
    {
        var setting = await _db.AutonomySettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (setting is null)
        {
            _db.AutonomySettings.Add(new AutonomySetting { UserId = userId, Level = level });
        }
        else
        {
            setting.Level = level;
            setting.UpdatedAtUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>True if an automated (non-explicitly-approved) file edit is permitted at this level.</summary>
    public bool CanAutoEditFiles(AutonomyLevel level) => level != AutonomyLevel.Low;

    /// <summary>True if unlisted/dangerous-tier commands may run without a fresh explicit approval.</summary>
    public bool CanAutoRunUnlistedCommands(AutonomyLevel level) => level == AutonomyLevel.High;
}
