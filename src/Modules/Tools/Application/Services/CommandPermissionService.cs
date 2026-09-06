using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Modules.Tools.Application.Services;

public enum PersistedCommandDecision { None, AlwaysAllow, AlwaysDeny }

/// <summary>
/// Real persistence for the "Always Allow / Always Deny" scoping spec Section 11
/// asks for, on top of CommandPolicyEngine's static in-code policy. Scoped per user
/// so one person's blanket approval for e.g. `npm` doesn't silently apply to
/// another account.
/// </summary>
public sealed class CommandPermissionService
{
    private readonly PlatformDbContext _db;
    public CommandPermissionService(PlatformDbContext db) => _db = db;

    public async Task<PersistedCommandDecision> CheckAsync(Guid ownerUserId, string executableName, CancellationToken ct = default)
    {
        var rule = await _db.CommandPermissionRules
            .FirstOrDefaultAsync(r => r.OwnerUserId == ownerUserId && r.ExecutableName == executableName, ct);

        return rule?.Decision switch
        {
            "AlwaysAllow" => PersistedCommandDecision.AlwaysAllow,
            "AlwaysDeny" => PersistedCommandDecision.AlwaysDeny,
            _ => PersistedCommandDecision.None
        };
    }

    public async Task SetAsync(Guid ownerUserId, string executableName, PersistedCommandDecision decision, CancellationToken ct = default)
    {
        var rule = await _db.CommandPermissionRules
            .FirstOrDefaultAsync(r => r.OwnerUserId == ownerUserId && r.ExecutableName == executableName, ct);

        var decisionText = decision switch
        {
            PersistedCommandDecision.AlwaysAllow => "AlwaysAllow",
            PersistedCommandDecision.AlwaysDeny => "AlwaysDeny",
            _ => null
        };

        if (decisionText is null)
        {
            if (rule is not null) _db.CommandPermissionRules.Remove(rule);
        }
        else if (rule is null)
        {
            _db.CommandPermissionRules.Add(new CommandPermissionRule
            {
                OwnerUserId = ownerUserId, ExecutableName = executableName, Decision = decisionText
            });
        }
        else
        {
            rule.Decision = decisionText;
        }

        await _db.SaveChangesAsync(ct);
    }

    public Task<List<CommandPermissionRule>> ListAsync(Guid ownerUserId, CancellationToken ct = default) =>
        _db.CommandPermissionRules.Where(r => r.OwnerUserId == ownerUserId).OrderBy(r => r.ExecutableName).ToListAsync(ct);
}
