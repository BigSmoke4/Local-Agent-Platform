namespace LocalAgentPlatform.Web.Models;

public class DashboardViewModel
{
    public bool ModelProviderHealthy { get; set; }
    public string? ModelProviderDetail { get; set; }
    public IReadOnlyList<ModelRowViewModel> AvailableModels { get; set; } = Array.Empty<ModelRowViewModel>();

    public double? CpuUtilizationPercent { get; set; }
    public long? RamUsedBytes { get; set; }
    public long? RamTotalBytes { get; set; }
    public double? GpuUtilizationPercent { get; set; }
    public long? GpuVramUsedBytes { get; set; }
    public long? GpuVramTotalBytes { get; set; }

    public int ActiveAgentSessionCount { get; set; }
    public int TotalProjectCount { get; set; }
    public int TotalRepositoryCount { get; set; }
}

public class ModelRowViewModel
{
    public string Id { get; set; } = default!;
    public string? Quantization { get; set; }
    public long? ParameterCount { get; set; }
    public long? FileSizeBytes { get; set; }
}
