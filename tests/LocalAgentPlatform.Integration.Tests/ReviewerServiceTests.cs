using LocalAgentPlatform.Modules.Verification.Application.Services;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Models;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

public sealed class ReviewerServiceTests
{
    [Theory]
    [InlineData("{\"verdict\":\"Maybe\",\"reason\":\"not a supported verdict\"}")]
    [InlineData("{\"reason\":\"missing verdict\"}")]
    [InlineData("not JSON")]
    public async Task Unrecognized_or_unparseable_model_output_is_unavailable_not_approved(string response)
    {
        var service = new ReviewerService(new FixedResponseModelProvider(response));

        var result = await service.ReviewAsync("model", "request", new VerificationRun());

        Assert.Equal("Unavailable", result.Verdict);
    }

    [Fact]
    public async Task Parses_valid_verdict_even_when_reason_contains_braces()
    {
        var service = new ReviewerService(
            new FixedResponseModelProvider("prefix {\"verdict\":\"Rejected\",\"reason\":\"unexpected } in output\"} suffix"));

        var result = await service.ReviewAsync("model", "request", new VerificationRun());

        Assert.Equal("Rejected", result.Verdict);
        Assert.Equal("unexpected } in output", result.Reason);
    }

    [Fact]
    public async Task Propagates_caller_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new ReviewerService(new FixedResponseModelProvider("{}"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ReviewAsync("model", "request", new VerificationRun(), cts.Token));
    }

    private sealed class FixedResponseModelProvider : IModelProvider
    {
        private readonly string _response;
        public FixedResponseModelProvider(string response) => _response = response;
        public string ProviderId => "fixed-review-test";
        public Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModelDescriptor>>(Array.Empty<ModelDescriptor>());
        public Task<ModelLoadResult> LoadModelAsync(string modelId, CancellationToken ct = default) =>
            Task.FromResult(new ModelLoadResult(true, null, TimeSpan.Zero));
        public Task UnloadModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ModelGenerationResult> GenerateAsync(ModelGenerationRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ModelGenerationResult(_response, 1, 1, TimeSpan.Zero, null, request.ModelId, false));
        }
        public async IAsyncEnumerable<ModelStreamChunk> GenerateStreamAsync(
            ModelGenerationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ModelStreamChunk(_response, true, 1);
        }
        public Task<int> CountTokensAsync(string modelId, string text, CancellationToken ct = default) =>
            Task.FromResult(text.Length);
        public Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct = default) =>
            Task.FromResult(new ModelProviderHealth(true, "fixed test provider"));
    }
}
