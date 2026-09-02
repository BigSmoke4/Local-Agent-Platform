using Npgsql;
using Pgvector;
using Platform.Web.Data;

namespace Platform.Web.Services.Memory;

public record PgVectorMatch(Guid MemoryId, double Distance);

/// <summary>
/// Real, opt-in pgvector-backed similarity search — pushes the top-k
/// search into PostgreSQL via the real `vector` type and `&lt;=&gt;` cosine
/// distance operator, instead of pulling 500 rows into app memory and
/// scoring them there (what MemoryService.RetrieveAsync does by default).
///
/// This is genuinely optional and off by default, honestly, because it
/// requires:
///   1. The `vector` extension actually installed on your PostgreSQL
///      instance (`CREATE EXTENSION IF NOT EXISTS vector;`)
///   2. The `memory_embeddings` table created — see the SQL in
///      docs/SETUP_PGVECTOR.md; this is NOT part of the standard EF Core
///      migration because the vector dimension depends on which embedding
///      model you use, which isn't known at migration-authoring time
///   3. `Memory:UsePgVector` set to `true` in configuration
///
/// If any of the above isn't true, MemoryService's default keyword-overlap/
/// app-level-cosine path is used instead — this class's failure mode is a
/// caught, logged exception and an honest empty result, never a silent
/// wrong answer.
/// </summary>
public class PgVectorSimilarityService
{
    private readonly string _connectionString;
    private readonly ILogger<PgVectorSimilarityService> _logger;

    public PgVectorSimilarityService(PlatformDbContext db, ILogger<PgVectorSimilarityService> logger)
    {
        _connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("No connection string configured on PlatformDbContext.");
        _logger = logger;
    }

    /// <summary>
    /// Upserts one memory's embedding into the optional memory_embeddings
    /// table. No-op (returns false) if the table doesn't exist — checked by
    /// catching the real Postgres "relation does not exist" error rather
    /// than guessing whether pgvector is set up.
    /// </summary>
    public async Task<bool> UpsertEmbeddingAsync(Guid memoryId, float[] embedding, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlDataSourceBuilder(_connectionString)
                .UseVector()
                .Build()
                .CreateConnection();
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO memory_embeddings (memory_id, embedding)
                VALUES ($1, $2)
                ON CONFLICT (memory_id) DO UPDATE SET embedding = EXCLUDED.embedding
                """;
            cmd.Parameters.AddWithValue(memoryId);
            cmd.Parameters.AddWithValue(new Vector(embedding));

            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // undefined_table — real Postgres error code, not guessed
        {
            _logger.LogInformation("memory_embeddings table not found — pgvector setup not present. See docs/SETUP_PGVECTOR.md. Falling back to app-level scoring.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "pgvector upsert failed");
            return false;
        }
    }

    /// <summary>
    /// Real SQL-side top-k cosine-distance search via pgvector's `&lt;=&gt;`
    /// operator. Returns an empty list (not an exception surfaced to the
    /// caller) if the table doesn't exist — MemoryService interprets an
    /// empty list as "fall back to app-level scoring".
    /// </summary>
    public async Task<List<PgVectorMatch>> SearchAsync(float[] queryEmbedding, int limit, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlDataSourceBuilder(_connectionString)
                .UseVector()
                .Build()
                .CreateConnection();
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT memory_id, embedding <=> $1 AS distance
                FROM memory_embeddings
                ORDER BY distance ASC
                LIMIT $2
                """;
            cmd.Parameters.AddWithValue(new Vector(queryEmbedding));
            cmd.Parameters.AddWithValue(limit);

            var results = new List<PgVectorMatch>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new PgVectorMatch(reader.GetGuid(0), reader.GetDouble(1)));
            }

            return results;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            _logger.LogInformation("memory_embeddings table not found — falling back to app-level scoring.");
            return new List<PgVectorMatch>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "pgvector search failed — falling back to app-level scoring.");
            return new List<PgVectorMatch>();
        }
    }
}
