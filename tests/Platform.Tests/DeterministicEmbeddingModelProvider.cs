using Platform.Web.Services;

namespace Platform.Tests;

/// <summary>
/// Returns deterministic fake-but-structured embeddings so tests can verify
/// the REAL cosine similarity math in MemoryService without needing a live
/// Ollama embedding model. The vectors aren't semantically meaningful (they
/// encode "does the text contain word X" as a one-hot-ish signal), but the
/// similarity computation applied to them is the real production code path.
/// </summary>
public class DeterministicEmbeddingModelProvider : IModelProvider
{
    private static readonly string[] Vocabulary = { "auth", "jwt", "payment", "stripe", "checkout", "security" };

    public Task<GenerationResult> GenerateAsync(GenerationRequest request, CancellationToken ct = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<string> StreamAsync(GenerationRequest request, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<ModelHealth> CheckHealthAsync(CancellationToken ct = default)
        => Task.FromResult(new ModelHealth(true, null));

    public Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<float[]?> GetEmbeddingAsync(string embeddingModelRuntimeId, string text, CancellationToken ct = default)
    {
        var lower = text.ToLowerInvariant();
        var vector = Vocabulary.Select(word => lower.Contains(word) ? 1f : 0f).ToArray();
        return Task.FromResult<float[]?>(vector);
    }
}
