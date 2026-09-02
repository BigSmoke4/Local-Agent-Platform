using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Platform.Web.Data;
using Platform.Web.Models;
using Platform.Web.Services.Memory;
using Xunit;

namespace Platform.Tests;

public class MemoryServiceTests
{
    private static PlatformDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PlatformDbContext(options);
    }

    // No ModelRuntime:EmbeddingModel configured -> MemoryService uses the
    // keyword-overlap fallback path for every row, which is what these
    // tests exercise (real relevance ranking without needing a live Ollama).
    private static MemoryService CreateService(PlatformDbContext db)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build();
        return new MemoryService(db, new StubModelProvider(), config);
    }

    [Fact]
    public async Task RetrieveAsync_ReturnsRelevantMemoryOverIrrelevant()
    {
        var db = CreateInMemoryDb();
        var service = CreateService(db);

        await service.StoreAsync(MemoryType.LongTermProject, "The authentication service uses JWT tokens with refresh rotation.", "auth jwt security", null);
        await service.StoreAsync(MemoryType.LongTermProject, "The checkout page uses Stripe for payment processing.", "checkout payments stripe", null);

        var results = await service.RetrieveAsync("How does authentication work with JWT?");

        Assert.NotEmpty(results);
        Assert.Contains("authentication", results[0].Memory.Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(results[0].RelevanceScore > 0);
        Assert.Equal("KeywordOverlap", results[0].ScoringMethod);
    }

    [Fact]
    public async Task RetrieveAsync_NoOverlap_ReturnsEmpty()
    {
        var db = CreateInMemoryDb();
        var service = CreateService(db);

        await service.StoreAsync(MemoryType.LongTermProject, "The checkout page uses Stripe.", "checkout stripe", null);

        var results = await service.RetrieveAsync("xyzzy unrelated quantum kittens");

        Assert.Empty(results);
    }

    [Fact]
    public async Task RetrieveAsync_IncrementsRetrievalCount()
    {
        var db = CreateInMemoryDb();
        var service = CreateService(db);

        var stored = await service.StoreAsync(MemoryType.UserPreference, "User prefers concise responses.", "preference concise", null);
        Assert.Equal(0, stored.TimesRetrieved);

        await service.RetrieveAsync("concise preference responses");

        var updated = await db.Memories.FindAsync(stored.Id);
        Assert.Equal(1, updated!.TimesRetrieved);
    }

    [Fact]
    public async Task RetrieveAsync_WithEmbeddingModelConfigured_UsesCosineSimilarity()
    {
        var db = CreateInMemoryDb();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ModelRuntime:EmbeddingModel"] = "fake-embed-model" }).Build();
        var service = new MemoryService(db, new DeterministicEmbeddingModelProvider(), config);

        await service.StoreAsync(MemoryType.LongTermProject, "The auth service validates JWT tokens.", "auth jwt", null);
        await service.StoreAsync(MemoryType.LongTermProject, "The checkout flow uses Stripe payment.", "checkout stripe payment", null);

        var results = await service.RetrieveAsync("How does JWT auth security work?");

        Assert.NotEmpty(results);
        Assert.Equal("Embedding", results[0].ScoringMethod);
        Assert.Contains("JWT", results[0].Memory.Content);
    }
}
