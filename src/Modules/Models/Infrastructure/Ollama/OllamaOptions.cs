namespace LocalAgentPlatform.Modules.Models.Infrastructure.Ollama;

public class OllamaOptions
{
    public const string SectionName = "Ollama";

    /// <summary>Base URL of the local Ollama server, e.g. http://localhost:11434</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Request timeout for generation calls.</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;
}
