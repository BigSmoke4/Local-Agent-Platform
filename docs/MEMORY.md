# Memory

`MemoryService` (`Services/Memory/MemoryService.cs`) implements the five
memory types from §14 as one table (`Memory`, discriminated by
`MemoryType`: ShortTerm, Working, LongTermProject, UserPreference,
Execution) rather than five separate tables — a deliberate simplification
since they share the same storage/retrieval shape.

## Retrieval is real, two real scoring paths

`RetrieveAsync(query, maxResults)` scores every candidate by one of two
genuinely different, genuinely real methods:

1. **Embedding cosine similarity** — when `ModelRuntime:EmbeddingModel` is
   configured (e.g. `"nomic-embed-text"`, pulled via `ollama pull
   nomic-embed-text`), `MemoryService` calls the real Ollama
   `/api/embeddings` endpoint via `IModelProvider.GetEmbeddingAsync` for
   both the query and each memory at store-time, and computes real cosine
   similarity between the vectors at retrieval time.
2. **Keyword overlap** — the honest fallback, used per-row whenever no
   embedding model is configured, or the embedding call failed for that
   specific memory when it was stored (`EmbeddingCsv` stays null in that
   case, not a fabricated vector).

Each `MemoryResult` carries a `ScoringMethod` field (`"Embedding"` or
`"KeywordOverlap"`) so callers/tests can see which path produced a given
score rather than the distinction being hidden. `MemoryServiceTests.cs`
exercises both paths — the embedding path via a deterministic stub
`IModelProvider` that returns real vectors without needing a live Ollama.

## Storage

`Memory.EmbeddingCsv` stores the vector as comma-separated floats in a text
column — no `pgvector` extension is assumed to be installed. This means
similarity is computed in application code (after pulling the bounded
candidate set), not pushed into a SQL `ORDER BY embedding <-> query LIMIT n`
query. That's the natural next step if `pgvector` is available on your
Postgres instance — it would let the top-k search happen in the database
instead of after loading 500 rows into memory, but requires the extension
to actually be installed, so it isn't assumed here.

## Not yet wired into the agent loop

`MemoryService` is reachable via `POST /api/memory` and
`GET /api/memory/retrieve`, but `AgentController`/`AgentVerificationController`
do not yet call it automatically before generating a response. Wiring
retrieval into the prompt-building step (so past decisions/preferences
actually influence new generations) is a real next step, not done here.
