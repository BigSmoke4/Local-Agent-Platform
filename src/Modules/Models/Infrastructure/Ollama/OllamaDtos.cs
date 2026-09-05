using System.Text.Json.Serialization;

namespace LocalAgentPlatform.Modules.Models.Infrastructure.Ollama;

// Matches GET /api/tags
internal sealed class OllamaTagsResponse
{
    [JsonPropertyName("models")]
    public List<OllamaTagModel> Models { get; set; } = new();
}

internal sealed class OllamaTagModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("details")]
    public OllamaModelDetails? Details { get; set; }
}

internal sealed class OllamaModelDetails
{
    [JsonPropertyName("parameter_size")]
    public string? ParameterSize { get; set; }

    [JsonPropertyName("quantization_level")]
    public string? QuantizationLevel { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }
}

// POST /api/show
internal sealed class OllamaShowRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = default!;
}

internal sealed class OllamaShowResponse
{
    [JsonPropertyName("model_info")]
    public Dictionary<string, object>? ModelInfo { get; set; }
}

// POST /api/generate
internal sealed class OllamaGenerateRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = default!;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = default!;

    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("options")]
    public OllamaGenerateOptions? Options { get; set; }
}

internal sealed class OllamaGenerateOptions
{
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("num_predict")]
    public int? NumPredict { get; set; }

    [JsonPropertyName("stop")]
    public List<string>? Stop { get; set; }
}

// Streamed / final JSON line from /api/generate
internal sealed class OllamaGenerateChunk
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("response")]
    public string Response { get; set; } = string.Empty;

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int? PromptEvalCount { get; set; }

    [JsonPropertyName("eval_count")]
    public int? EvalCount { get; set; }

    [JsonPropertyName("total_duration")]
    public long? TotalDurationNs { get; set; }

    [JsonPropertyName("load_duration")]
    public long? LoadDurationNs { get; set; }

    [JsonPropertyName("prompt_eval_duration")]
    public long? PromptEvalDurationNs { get; set; }

    [JsonPropertyName("eval_duration")]
    public long? EvalDurationNs { get; set; }
}
