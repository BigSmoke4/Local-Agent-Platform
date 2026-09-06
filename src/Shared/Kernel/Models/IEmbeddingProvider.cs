namespace LocalAgentPlatform.Shared.Kernel.Models;

public interface IEmbeddingProvider
{
    string ProviderId { get; }
    string DefaultEmbeddingModelId { get; }
    Task<float[]> EmbedAsync(string text, string? modelId = null, CancellationToken ct = default);
}
