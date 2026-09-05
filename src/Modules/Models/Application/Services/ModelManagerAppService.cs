using LocalAgentPlatform.Modules.Models.Domain;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Models;
using LocalAgentPlatform.Shared.Kernel.Telemetry;
using Microsoft.Extensions.Logging;

namespace LocalAgentPlatform.Modules.Models.Application.Services;

public sealed record ModelManagerRow(
    RegisteredModel Registered,
    ModelDescriptor? RuntimeDescriptor,
    bool AvailableInRuntime,
    ModelFitness Fitness,
    string FitnessReason
);

public sealed record ModelManagerViewData(
    IReadOnlyList<ModelManagerRow> Rows,
    IReadOnlyList<ModelDescriptor> UnregisteredRuntimeModels,
    long? AvailableRamBytes,
    string? RecommendationMessage
);

/// <summary>
/// Application-layer orchestrator for the Model Manager screen (spec Section 30/31).
/// Combines: what's registered in Postgres, what Ollama actually reports having on
/// disk right now, live RAM telemetry, and the pure recommendation engine. Every
/// number shown to the user traces back to a real source — nothing here invents data.
/// </summary>
public sealed class ModelManagerAppService
{
    private readonly IModelRegistryService _registry;
    private readonly IModelProvider _modelProvider;
    private readonly IHardwareTelemetryProvider _hardware;
    private readonly ILogger<ModelManagerAppService> _logger;

    public ModelManagerAppService(
        IModelRegistryService registry,
        IModelProvider modelProvider,
        IHardwareTelemetryProvider hardware,
        ILogger<ModelManagerAppService> logger)
    {
        _registry = registry;
        _modelProvider = modelProvider;
        _hardware = hardware;
        _logger = logger;
    }

    public async Task<ModelManagerViewData> BuildViewDataAsync(CancellationToken ct = default)
    {
        var registered = await _registry.ListAsync(ct);

        IReadOnlyList<ModelDescriptor> runtimeModels = Array.Empty<ModelDescriptor>();
        try
        {
            runtimeModels = await _modelProvider.ListModelsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list models from runtime; Model Manager will show registry-only data.");
        }

        var runtimeById = runtimeModels.ToDictionary(m => m.Id, m => m);

        var hw = await _hardware.GetSnapshotAsync(ct);
        var availableRam = (hw.RamTotalBytes.HasValue && hw.RamUsedBytes.HasValue)
            ? hw.RamTotalBytes.Value - hw.RamUsedBytes.Value
            : (long?)null;

        var candidates = registered.Select(r =>
        {
            runtimeById.TryGetValue(r.ModelId, out var descriptor);
            var estimatedRam = descriptor?.EstimatedRamBytes;
            return new CandidateModel(r.ModelId, estimatedRam);
        }).ToList();

        var recommendation = ModelRecommendationEngine.Recommend(availableRam, candidates);
        var verdictsById = recommendation.Verdicts.ToDictionary(v => v.ModelId, v => v);

        var rows = registered.Select(r =>
        {
            runtimeById.TryGetValue(r.ModelId, out var descriptor);
            var hasVerdict = verdictsById.TryGetValue(r.ModelId, out var verdict);
            return new ModelManagerRow(
                Registered: r,
                RuntimeDescriptor: descriptor,
                AvailableInRuntime: descriptor is not null,
                Fitness: hasVerdict ? verdict!.Fitness : ModelFitness.Unknown,
                FitnessReason: hasVerdict ? verdict!.Reason : "Not evaluated."
            );
        }).ToList();

        var registeredModelIds = registered.Select(r => r.ModelId).ToHashSet();
        var unregistered = runtimeModels.Where(m => !registeredModelIds.Contains(m.Id)).ToList();

        return new ModelManagerViewData(rows, unregistered, availableRam, recommendation.Message);
    }

    public Task<RegisteredModel> RegisterFromRuntimeAsync(ModelDescriptor descriptor, CancellationToken ct = default) =>
        _registry.RegisterAsync("ollama", descriptor.Id, descriptor.Name, descriptor.Quantization, descriptor.ContextWindow, ct);

    public Task SetDefaultAsync(Guid registeredModelId, CancellationToken ct = default) =>
        _registry.SetDefaultAsync(registeredModelId, ct);

    public Task DeleteAsync(Guid registeredModelId, CancellationToken ct = default) =>
        _registry.DeleteAsync(registeredModelId, ct);

    public Task<ModelLoadResult> LoadAsync(string modelId, CancellationToken ct = default) =>
        _modelProvider.LoadModelAsync(modelId, ct);

    public Task UnloadAsync(string modelId, CancellationToken ct = default) =>
        _modelProvider.UnloadModelAsync(modelId, ct);
}
