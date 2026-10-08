using LocalAgentPlatform.Modules.Memory.Application.Services;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Models;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

[Collection("Postgres")]
public sealed class MemoryRetrievalServiceTests
{
    private readonly PostgresFixture _fixture;

    public MemoryRetrievalServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Retrieval_is_owner_scoped_and_respects_the_output_budget()
    {
        var owner = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        await using var db = _fixture.CreateContext();
        db.MemoryEntries.AddRange(
            new MemoryEntry
            {
                OwnerUserId = owner,
                Scope = "LongTerm",
                Title = "needle: owned note",
                Content = new string('x', 4_000)
            },
            new MemoryEntry
            {
                OwnerUserId = otherOwner,
                Scope = "LongTerm",
                Title = "needle: another user's note",
                Content = "must not cross the owner boundary"
            });
        await db.SaveChangesAsync();

        var service = new MemoryRetrievalService(db, new UnavailableEmbeddingProvider());
        var results = await service.RetrieveRelevantAsync(owner, repositoryId: null, query: "needle", maxEntries: 10, maxTotalChars: 160);

        var result = Assert.Single(results);
        Assert.Equal("needle: owned note", result.Title);
        Assert.True(result.Content.Length + result.Title.Length + result.Scope.Length + 8 <= 160);
        Assert.DoesNotContain(results, item => item.Title.Contains("another user's", StringComparison.Ordinal));
    }

    private sealed class UnavailableEmbeddingProvider : IEmbeddingProvider
    {
        public string ProviderId => "test-unavailable";
        public string DefaultEmbeddingModelId => "none";
        public Task<float[]> EmbedAsync(string text, string? modelId = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Embedding runtime is unavailable in this test.");
    }
}
