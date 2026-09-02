using Platform.Web.Services;

namespace Platform.Tests;

/// <summary>
/// Minimal stub for tests that don't exercise generation/embeddings directly
/// (MemoryService tests exercise the keyword-overlap fallback path, which
/// never calls GetEmbeddingAsync when no embedding model is configured).
/// </summary>
public class StubModelProvider : IModelProvider
{
    public Task<GenerationResult> GenerateAsync(GenerationRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("Not exercised by these tests.");

    public IAsyncEnumerable<string> StreamAsync(GenerationRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("Not exercised by these tests.");

    public Task<ModelHealth> CheckHealthAsync(CancellationToken ct = default)
        => Task.FromResult(new ModelHealth(false, "stub"));

    public Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<float[]?> GetEmbeddingAsync(string embeddingModelRuntimeId, string text, CancellationToken ct = default)
        => Task.FromResult<float[]?>(null);
}
