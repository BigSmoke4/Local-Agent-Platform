using LocalAgentPlatform.Modules.Models.Infrastructure.Ollama;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

public sealed class LiveOllamaModelProviderTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task RealHttp_ListModels_And_Generate_WhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("LAP_RUN_LIVE_TESTS") != "1") return;
        var baseUrl = Environment.GetEnvironmentVariable("LAP_OLLAMA_URL") ?? "http://localhost:11434";
        var model = Environment.GetEnvironmentVariable("LAP_OLLAMA_MODEL") ?? "llama3.2:3b";
        var provider = new OllamaModelProvider(new HttpClient(), Options.Create(new OllamaOptions { BaseUrl = baseUrl, RequestTimeoutSeconds = 120 }), NullLogger<OllamaModelProvider>.Instance);
        var models = await provider.ListModelsAsync();
        Assert.Contains(models, m => m.Id == model);
        var result = await provider.GenerateAsync(new(model, "Reply with exactly: ok", Temperature: 0, MaxOutputTokens: 8));
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.True(result.Duration > TimeSpan.Zero);
    }
}
