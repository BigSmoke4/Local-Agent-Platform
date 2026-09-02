using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Web.Services;

/// <summary>
/// Real HTTP adapter to a locally running Ollama instance (https://ollama.com).
/// No cloud dependency: talks only to the configured local base address.
/// </summary>
public class OllamaModelProvider : IModelProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaModelProvider> _logger;

    public OllamaModelProvider(HttpClient http, ILogger<OllamaModelProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<GenerationResult> GenerateAsync(GenerationRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var body = new
        {
            model = request.ModelRuntimeId,
            prompt = request.Prompt,
            stream = false,
            options = request.MaxTokens.HasValue
                ? new { num_predict = request.MaxTokens.Value }
                : null
        };

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        using var response = await _http.PostAsJsonAsync("api/generate", body, jsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty response from Ollama.");

        sw.Stop();

        var inputTokens = payload.PromptEvalCount ?? 0;
        var outputTokens = payload.EvalCount ?? 0;
        var evalDurationSeconds = (payload.EvalDuration ?? 0) / 1_000_000_000.0;
        var tokensPerSecond = evalDurationSeconds > 0 ? outputTokens / evalDurationSeconds : 0;

        return new GenerationResult(
            Text: payload.Response ?? string.Empty,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            DurationMs: sw.Elapsed.TotalMilliseconds,
            TokensPerSecond: tokensPerSecond);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        GenerationRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = new
        {
            model = request.ModelRuntimeId,
            prompt = request.Prompt,
            stream = true
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            OllamaGenerateResponse? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaGenerateResponse>(line);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse Ollama stream chunk: {Line}", line);
                continue;
            }

            if (chunk?.Response is { Length: > 0 })
                yield return chunk.Response;

            if (chunk?.Done == true)
                yield break;
        }
    }

    public async Task<ModelHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("api/tags", ct);
            return response.IsSuccessStatusCode
                ? new ModelHealth(true, null)
                : new ModelHealth(false, $"Ollama returned status {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama health check failed");
            return new ModelHealth(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var payload = await _http.GetFromJsonAsync<OllamaTagsResponse>("api/tags", ct);
            return payload?.Models?.Select(m => m.Name).Where(n => n != null).Select(n => n!).ToList()
                   ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list Ollama models");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Real call to Ollama's /api/embeddings endpoint. Returns null (not a
    /// fabricated vector) if the request fails — e.g. the given model
    /// doesn't support embeddings, or isn't pulled.
    /// </summary>
    public async Task<float[]?> GetEmbeddingAsync(string embeddingModelRuntimeId, string text, CancellationToken ct = default)
    {
        try
        {
            var body = new { model = embeddingModelRuntimeId, prompt = text };
            using var response = await _http.PostAsJsonAsync("api/embeddings", body, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Ollama embeddings request failed with status {Status}", response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: ct);
            return payload?.Embedding;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama embeddings call failed");
            return null;
        }
    }

    private class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public int? PromptEvalCount { get; set; }

        [JsonPropertyName("eval_count")]
        public int? EvalCount { get; set; }

        [JsonPropertyName("eval_duration")]
        public long? EvalDuration { get; set; }
    }

    private class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaTagModel>? Models { get; set; }
    }

    private class OllamaEmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }
    }

    private class OllamaTagModel
    {
        public string? Name { get; set; }
    }
}
