namespace Platform.Web.Services;

public record GenerationRequest(string ModelRuntimeId, string Prompt, int? MaxTokens = null);

public record GenerationResult(
    string Text,
    int InputTokens,
    int OutputTokens,
    double DurationMs,
    double TokensPerSecond);

public record ModelHealth(bool IsHealthy, string? Message);

/// <summary>
/// Abstraction over a local model inference runtime. Controllers and application
/// services depend on this interface only — never on a concrete runtime.
/// </summary>
public interface IModelProvider
{
    Task<GenerationResult> GenerateAsync(GenerationRequest request, CancellationToken ct = default);

    IAsyncEnumerable<string> StreamAsync(GenerationRequest request, CancellationToken ct = default);

    Task<ModelHealth> CheckHealthAsync(CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Real embedding vector for the given text, via the runtime's embedding
    /// endpoint. Returns null if the runtime/model doesn't support embeddings
    /// rather than fabricating a vector — callers must handle that honestly
    /// (e.g. MemoryService falls back to keyword overlap).
    /// </summary>
    Task<float[]?> GetEmbeddingAsync(string embeddingModelRuntimeId, string text, CancellationToken ct = default);
}
