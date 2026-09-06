using System.Security.Cryptography;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Security;

public sealed record CreatedApiKey(Guid Id, string RawKey);

public sealed class ApiKeyService
{
    private readonly PlatformDbContext _db;
    public ApiKeyService(PlatformDbContext db) => _db = db;

    public async Task<CreatedApiKey> CreateAsync(Guid ownerUserId, string name, CancellationToken ct = default)
    {
        var rawKey = "lap_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var entity = new ApiKey
        {
            OwnerUserId = ownerUserId,
            Name = name,
            KeyHash = Hash(rawKey),
            KeyPrefix = rawKey[..12]
        };
        _db.ApiKeys.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new CreatedApiKey(entity.Id, rawKey);
    }

    public async Task<ApiKey?> ValidateAsync(string rawKey, CancellationToken ct = default)
    {
        var hash = Hash(rawKey);
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAtUtc == null, ct);
        if (key is null) return null;

        key.LastUsedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return key;
    }

    public async Task RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null) return;
        key.RevokedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static string Hash(string rawKey) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey)));
}
