namespace Platform.Web.Models;

public enum MemoryType
{
    ShortTerm,
    Working,
    LongTermProject,
    UserPreference,
    Execution
}

public class Memory
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public MemoryType Type { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Simple keyword tags used for retrieval matching. A true implementation
    /// would use vector embeddings (§14 allows for PostgreSQL-compatible
    /// vector storage later); this is a real, working keyword-overlap
    /// retrieval mechanism rather than a fabricated embedding similarity
    /// score, and is labeled as such.
    /// </summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>
    /// Real embedding vector from IModelProvider.GetEmbeddingAsync, stored as
    /// comma-separated floats (no pgvector extension assumed to be installed
    /// — see MEMORY.md). Null if no embedding model was configured/available
    /// when this memory was stored; retrieval falls back to keyword overlap
    /// for those rows rather than crashing or fabricating a score.
    /// </summary>
    public string? EmbeddingCsv { get; set; }

    public Guid? AgentSessionId { get; set; }

    public int TimesRetrieved { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastRetrievedAtUtc { get; set; }
}
