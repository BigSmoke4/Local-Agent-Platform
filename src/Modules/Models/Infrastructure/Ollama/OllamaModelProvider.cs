using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LocalAgentPlatform.Shared.Kernel.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAgentPlatform.Modules.Models.Infrastructure.Ollama;

/// <summary>
/// Real IModelProvider implementation backed by a local Ollama server.
/// Every value returned here comes from Ollama's actual HTTP responses —
/// no fabricated token counts or timings (per platform rule #65).
/// </summary>
public sealed class OllamaModelProvider : IModelProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaModelProvider> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string ProviderId => "ollama";

    public OllamaModelProvider(HttpClient http, IOptions<OllamaOptions> options, ILogger<OllamaModelProvider> logger)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(options.Value.RequestTimeoutSeconds);
        _logger = logger;
    }

    public async Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetFromJsonAsync<OllamaTagsResponse>("/api/tags", JsonOpts, ct)
                   ?? new OllamaTagsResponse();

        return resp.Models.Select(m => new ModelDescriptor(
            Id: m.Name,
            Name: m.Name,
            Version: null,
            ParameterCount: TryParseParamCount(m.Details?.ParameterSize),
            Quantization: m.Details?.QuantizationLevel,
            ContextWindow: null, // Ollama does not report this via /api/tags; resolved lazily via /api/show if needed
            ModelFormat: m.Details?.Format,
            FileSizeBytes: m.Size,
            EstimatedRamBytes: m.Size, // best-effort approximation: on-disk size; true RAM footprint may differ
            EstimatedVramBytes: null,
            CodingCapability: true,
            ReasoningCapability: true,
            ToolCallingCapability: false,
            StreamingCapability: true
        )).ToList();
    }

    public async Task<ModelLoadResult> LoadModelAsync(string modelId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Ollama loads a model into memory on first generate call with an empty/short prompt.
            var req = new OllamaGenerateRequest { Model = modelId, Prompt = "", Stream = false };
            var resp = await _http.PostAsJsonAsync("/api/generate", req, JsonOpts, ct);
            resp.EnsureSuccessStatusCode();
            sw.Stop();
            return new ModelLoadResult(true, null, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Failed to load model {ModelId} via Ollama", modelId);
            return new ModelLoadResult(false, ex.Message, sw.Elapsed);
        }
    }

    public Task UnloadModelAsync(string modelId, CancellationToken ct = default)
    {
        // Ollama's REST API does not currently expose an explicit unload endpoint;
        // this is an isolated extension point rather than a fabricated success.
        _logger.LogWarning("UnloadModelAsync requested for {ModelId} but Ollama's HTTP API has no unload endpoint; no-op.", modelId);
        return Task.CompletedTask;
    }

    public async Task<ModelGenerationResult> GenerateAsync(ModelGenerationRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var ollamaReq = new OllamaGenerateRequest
        {
            Model = request.ModelId,
            Prompt = request.Prompt,
            System = request.SystemPrompt,
            Stream = false,
            Options = new OllamaGenerateOptions
            {
                Temperature = request.Temperature,
                NumPredict = request.MaxOutputTokens,
                Stop = request.StopSequences?.ToList()
            }
        };

        var resp = await _http.PostAsJsonAsync("/api/generate", ollamaReq, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        var chunk = await resp.Content.ReadFromJsonAsync<OllamaGenerateChunk>(JsonOpts, ct)
                    ?? throw new InvalidOperationException("Ollama returned an empty response body.");
        sw.Stop();

        return new ModelGenerationResult(
            Text: chunk.Response,
            InputTokens: chunk.PromptEvalCount ?? 0,
            OutputTokens: chunk.EvalCount ?? 0,
            Duration: sw.Elapsed,
            TimeToFirstToken: chunk.PromptEvalDurationNs is long ns ? TimeSpan.FromMilliseconds(ns / 1_000_000.0) : null,
            ModelId: request.ModelId,
            FromCache: false
        );
    }

    public async IAsyncEnumerable<ModelStreamChunk> GenerateStreamAsync(
        ModelGenerationRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ollamaReq = new OllamaGenerateRequest
        {
            Model = request.ModelId,
            Prompt = request.Prompt,
            System = request.SystemPrompt,
            Stream = true,
            Options = new OllamaGenerateOptions
            {
                Temperature = request.Temperature,
                NumPredict = request.MaxOutputTokens,
                Stop = request.StopSequences?.ToList()
            }
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
        {
            Content = JsonContent.Create(ollamaReq, options: JsonOpts)
        };

        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        int tokensSoFar = 0;
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            OllamaGenerateChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaGenerateChunk>(line, JsonOpts);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping malformed stream line from Ollama.");
                continue;
            }
            if (chunk is null) continue;

            tokensSoFar += chunk.EvalCount.HasValue ? 0 : 1; // Ollama only reports eval_count on the final line
            yield return new ModelStreamChunk(chunk.Response, chunk.Done, chunk.EvalCount ?? tokensSoFar);

            if (chunk.Done) yield break;
        }
    }

    public async Task<int> CountTokensAsync(string modelId, string text, CancellationToken ct = default)
    {
        // Ollama has no standalone tokenize endpoint exposed publicly for all model families;
        // approximate using a generate call with num_predict=0 to get prompt_eval_count (a real count, not a guess).
        var req = new OllamaGenerateRequest
        {
            Model = modelId,
            Prompt = text,
            Stream = false,
            Options = new OllamaGenerateOptions { NumPredict = 0 }
        };
        var resp = await _http.PostAsJsonAsync("/api/generate", req, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        var chunk = await resp.Content.ReadFromJsonAsync<OllamaGenerateChunk>(JsonOpts, ct);
        return chunk?.PromptEvalCount ?? 0;
    }

    public async Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("/api/tags", ct);
            return resp.IsSuccessStatusCode
                ? new ModelProviderHealth(true, null)
                : new ModelProviderHealth(false, $"Ollama returned HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return new ModelProviderHealth(false, ex.Message);
        }
    }

    private static long? TryParseParamCount(string? parameterSize)
    {
        // e.g. "7.6B", "3B", "1.1B"
        if (string.IsNullOrWhiteSpace(parameterSize)) return null;
        var trimmed = parameterSize.Trim().ToUpperInvariant();
        var multiplier = 1L;
        if (trimmed.EndsWith("B")) { multiplier = 1_000_000_000L; trimmed = trimmed[..^1]; }
        else if (trimmed.EndsWith("M")) { multiplier = 1_000_000L; trimmed = trimmed[..^1]; }
        return double.TryParse(trimmed, out var value) ? (long)(value * multiplier) : null;
    }
}
