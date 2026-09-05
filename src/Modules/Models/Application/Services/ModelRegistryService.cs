using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Modules.Models.Application.Services;

public interface IModelRegistryService
{
    Task<IReadOnlyList<RegisteredModel>> ListAsync(CancellationToken ct = default);
    Task<RegisteredModel?> GetAsync(Guid id, CancellationToken ct = default);
    Task<RegisteredModel> RegisterAsync(string providerId, string modelId, string name, string? quantization, int? contextWindow, CancellationToken ct = default);
    Task SetDefaultAsync(Guid id, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Real CRUD implementation backed by PostgreSQL via EF Core. This is the persistence
/// layer for "which models has the user registered" — separate from
/// IModelProvider.ListModelsAsync, which reports what the *runtime* currently has
/// on disk. The Model Manager UI reconciles both.
/// </summary>
public sealed class ModelRegistryService : IModelRegistryService
{
    private readonly PlatformDbContext _db;

    public ModelRegistryService(PlatformDbContext db) => _db = db;

    public async Task<IReadOnlyList<RegisteredModel>> ListAsync(CancellationToken ct = default) =>
        await _db.RegisteredModels.OrderByDescending(m => m.IsDefault).ThenBy(m => m.Name).ToListAsync(ct);

    public Task<RegisteredModel?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.RegisteredModels.FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<RegisteredModel> RegisterAsync(
        string providerId, string modelId, string name, string? quantization, int? contextWindow, CancellationToken ct = default)
    {
        var existing = await _db.RegisteredModels
            .FirstOrDefaultAsync(m => m.ProviderId == providerId && m.ModelId == modelId, ct);
        if (existing is not null)
        {
            existing.Name = name;
            existing.Quantization = quantization;
            existing.ContextWindow = contextWindow;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        var entity = new RegisteredModel
        {
            ProviderId = providerId,
            ModelId = modelId,
            Name = name,
            Quantization = quantization,
            ContextWindow = contextWindow,
            IsDefault = !await _db.RegisteredModels.AnyAsync(ct) // first registered model becomes default
        };
        _db.RegisteredModels.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task SetDefaultAsync(Guid id, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var all = await _db.RegisteredModels.ToListAsync(ct);
        var target = all.FirstOrDefault(m => m.Id == id)
            ?? throw new InvalidOperationException($"Registered model {id} not found.");

        foreach (var m in all) m.IsDefault = m.Id == id;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.RegisteredModels.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (entity is null) return;
        _db.RegisteredModels.Remove(entity);
        await _db.SaveChangesAsync(ct);
    }
}
