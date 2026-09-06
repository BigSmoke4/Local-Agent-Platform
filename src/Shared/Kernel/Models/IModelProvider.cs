namespace LocalAgentPlatform.Shared.Kernel.Models;

/// <summary>
/// Abstraction over any local inference runtime (Ollama, llama.cpp, ONNX Runtime, ...).
/// The Agent Engine and all application code must depend on this interface only —
/// never on a concrete runtime. This is the seam that keeps the platform
/// runtime-agnostic per the "no mandatory cloud LLM / swappable model" requirement.
/// </summary>
public interface IModelProvider
{
    /// <summary>Stable identifier for this provider implementation, e.g. "ollama".</summary>
    string ProviderId { get; }

    /// <summary>List models this provider currently knows about / has available.</summary>
    Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>Load a model into memory/VRAM so it is ready for inference. Idempotent.</summary>
    Task<ModelLoadResult> LoadModelAsync(string modelId, CancellationToken ct = default);

    /// <summary>Unload a model to free RAM/VRAM.</summary>
    Task UnloadModelAsync(string modelId, CancellationToken ct = default);

    /// <summary>Non-streaming single-shot generation. Returns full response + real usage stats.</summary>
    Task<ModelGenerationResult> GenerateAsync(ModelGenerationRequest request, CancellationToken ct = default);

    /// <summary>Streaming generation. Yields token chunks as they are produced by the runtime.</summary>
    IAsyncEnumerable<ModelStreamChunk> GenerateStreamAsync(ModelGenerationRequest request, CancellationToken ct = default);

    /// <summary>Best-effort token count for a piece of text, using the runtime's own tokenizer if exposed.</summary>
    Task<int> CountTokensAsync(string modelId, string text, CancellationToken ct = default);

    /// <summary>Reports whether the underlying runtime is reachable and healthy.</summary>
    Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct = default);
}

public sealed record ModelDescriptor(
    string Id,
    string Name,
    string? Version,
    long? ParameterCount,
    string? Quantization,
    int? ContextWindow,
    string? ModelFormat,
    long? FileSizeBytes,
    long? EstimatedRamBytes,
    long? EstimatedVramBytes,
    bool CodingCapability,
    bool ReasoningCapability,
    bool ToolCallingCapability,
    bool StreamingCapability
);

public sealed record ModelLoadResult(bool Success, string? Message, TimeSpan LoadDuration);

public sealed record ModelGenerationRequest(
    string ModelId,
    string Prompt,
    string? SystemPrompt = null,
    double Temperature = 0.2,
    int? MaxOutputTokens = null,
    IReadOnlyList<string>? StopSequences = null
);

public sealed record ModelGenerationResult(
    string Text,
    int InputTokens,
    int OutputTokens,
    TimeSpan Duration,
    TimeSpan? TimeToFirstToken,
    string ModelId,
    bool FromCache
);

public sealed record ModelStreamChunk(string DeltaText, bool IsFinal, int? TokensSoFar);

public sealed record ModelProviderHealth(bool IsHealthy, string? Detail);
