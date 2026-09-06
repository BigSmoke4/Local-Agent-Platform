using System.Net.Http.Json;
using System.Text.Json;
using LocalAgentPlatform.Shared.Kernel.Models;
using Microsoft.Extensions.Options;

namespace LocalAgentPlatform.Modules.Models.Infrastructure.Ollama;

public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    public string ProviderId => "ollama";
    public string DefaultEmbeddingModelId => _options.EmbeddingModel;

    public OllamaEmbeddingProvider(HttpClient http, IOptions<OllamaOptions> options)
    {
        _options = options.Value;
        _http = http;
        _http.BaseAddress = new Uri(_options.BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);
    }

    public async Task<float[]> EmbedAsync(string text, string? modelId = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/embed", new { model = modelId ?? DefaultEmbeddingModelId, input = text }, JsonOpts, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Ollama returned an empty embedding response.");
        var vector = payload.Embeddings?.FirstOrDefault();
        return vector is { Length: > 0 } ? vector : throw new InvalidOperationException("Ollama returned no embedding vector.");
    }

    private sealed class EmbedResponse { public float[][]? Embeddings { get; set; } }
}
