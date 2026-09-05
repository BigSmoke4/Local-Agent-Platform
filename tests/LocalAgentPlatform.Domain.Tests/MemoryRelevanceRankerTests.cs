using LocalAgentPlatform.Modules.Memory.Domain;
using Xunit;

namespace LocalAgentPlatform.Domain.Tests;

public class MemoryRelevanceRankerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ranks_keyword_overlapping_memory_above_unrelated_memory()
    {
        var candidates = new List<ScorableMemory>
        {
            new(Guid.NewGuid(), "Auth bug", "Fixed the authentication token refresh bug", null, 0.5, Now, Now),
            new(Guid.NewGuid(), "Unrelated note", "The project uses tabs not spaces", null, 0.5, Now, Now),
        };

        var ranked = MemoryRelevanceRanker.Rank("fix authentication bug", candidates, Now);

        Assert.NotEmpty(ranked);
        Assert.Equal(candidates[0].Id, ranked[0].Id);
    }

    [Fact]
    public void High_importance_note_surfaces_even_without_keyword_overlap()
    {
        var candidates = new List<ScorableMemory>
        {
            new(Guid.NewGuid(), "Pinned architecture decision", "We use the repository pattern everywhere", null, 0.9, Now, Now),
        };

        var ranked = MemoryRelevanceRanker.Rank("completely different unrelated query text", candidates, Now);

        Assert.Single(ranked);
    }

    [Fact]
    public void Low_importance_note_with_no_overlap_is_excluded()
    {
        var candidates = new List<ScorableMemory>
        {
            new(Guid.NewGuid(), "Random note", "Something about lunch plans", null, 0.3, Now, Now),
        };

        var ranked = MemoryRelevanceRanker.Rank("fix the database connection pooling", candidates, Now);

        Assert.Empty(ranked);
    }

    [Fact]
    public void Empty_query_returns_no_results()
    {
        var candidates = new List<ScorableMemory> { new(Guid.NewGuid(), "T", "C", null, 0.9, Now, Now) };
        var ranked = MemoryRelevanceRanker.Rank("", candidates, Now);
        Assert.Empty(ranked);
    }

    [Fact]
    public void More_recently_accessed_memory_scores_higher_when_overlap_is_equal()
    {
        var older = new ScorableMemory(Guid.NewGuid(), "Cache note", "Redis caching was added to the service", null, 0.5, Now.AddDays(-60), Now.AddDays(-60));
        var recent = new ScorableMemory(Guid.NewGuid(), "Cache note", "Redis caching was added to the service", null, 0.5, Now.AddDays(-60), Now.AddDays(-1));

        var ranked = MemoryRelevanceRanker.Rank("redis caching service", new List<ScorableMemory> { older, recent }, Now);

        Assert.Equal(recent.Id, ranked[0].Id);
    }
}
