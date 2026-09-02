using Platform.Web.Services;

namespace Platform.Tests;

/// <summary>Returns a fixed GenerateAsync response so PlannerService's real
/// JSON-parsing/filtering logic can be tested without a live model call.</summary>
public class FixedResponseModelProvider : IModelProvider
{
    private readonly string _responseText;

    public FixedResponseModelProvider(string responseText)
    {
        _responseText = responseText;
    }

    public Task<GenerationResult> GenerateAsync(GenerationRequest request, CancellationToken ct = default)
        => Task.FromResult(new GenerationResult(_responseText, 10, 20, 100, 200));

    public IAsyncEnumerable<string> StreamAsync(GenerationRequest request, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<ModelHealth> CheckHealthAsync(CancellationToken ct = default)
        => Task.FromResult(new ModelHealth(true, null));

    public Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<float[]?> GetEmbeddingAsync(string embeddingModelRuntimeId, string text, CancellationToken ct = default)
        => Task.FromResult<float[]?>(null);
}
