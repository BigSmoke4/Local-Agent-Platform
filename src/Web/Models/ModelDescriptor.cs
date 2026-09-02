using System.ComponentModel.DataAnnotations;

namespace Platform.Web.Models;

public class ModelDescriptor
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Version { get; set; } = "unknown";

    public long? ParameterCount { get; set; }

    [MaxLength(50)]
    public string? Quantization { get; set; }

    public int ContextWindow { get; set; } = 4096;

    [MaxLength(50)]
    public string ModelFormat { get; set; } = "gguf";

    public long? FileSizeBytes { get; set; }

    public long? EstimatedRamBytes { get; set; }

    public long? EstimatedVramBytes { get; set; }

    public bool CodingCapability { get; set; }

    public bool ReasoningCapability { get; set; }

    public bool ToolCallingCapability { get; set; }

    public bool StreamingCapability { get; set; } = true;

    public bool IsDefault { get; set; }

    /// <summary>
    /// Identifier used by the runtime adapter to load this model (e.g. Ollama tag).
    /// </summary>
    [Required, MaxLength(200)]
    public string RuntimeId { get; set; } = string.Empty;

    public DateTime RegisteredAtUtc { get; set; } = DateTime.UtcNow;
}
