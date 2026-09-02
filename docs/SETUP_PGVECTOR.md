# pgvector Setup (Optional)

`PgVectorSimilarityService` exists and is registered, but is **not wired
into `MemoryService`'s default retrieval path** — it's a separate,
independently-usable service you opt into, because it requires
infrastructure this repo can't assume or verify:

1. The `vector` extension actually installed on your PostgreSQL server.
   Managed Postgres (RDS, Cloud SQL, Supabase, etc.) mostly support this;
   a local install needs the extension built/installed for your PG version.

2. Run this once against your database (not part of `dotnet ef` migrations,
   deliberately — see below):

```sql
CREATE EXTENSION IF NOT EXISTS vector;

-- Dimension must match your embedding model's actual output size.
-- nomic-embed-text (Ollama) = 768. Check your model's docs if using a
-- different one — using the wrong dimension here will make every insert
-- fail with a real Postgres error, not a silent wrong answer.
CREATE TABLE memory_embeddings (
    memory_id UUID PRIMARY KEY REFERENCES "Memories"("Id") ON DELETE CASCADE,
    embedding VECTOR(768) NOT NULL
);

CREATE INDEX ON memory_embeddings USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);
```

3. Why this isn't an EF Core migration: the vector dimension is a property
   of whichever embedding model you choose (768 for `nomic-embed-text`,
   1536 for OpenAI's `text-embedding-ada-002`, etc.), which isn't known
   until you configure `ModelRuntime:EmbeddingModel`. A migration authored
   against a fixed dimension would be wrong for anyone using a different
   embedding model.

## Using it

`PgVectorSimilarityService.UpsertEmbeddingAsync` / `.SearchAsync` are ready
to call once the table above exists. They're not automatically called by
`MemoryService.StoreAsync`/`RetrieveAsync` — wiring that in is a real next
step (check `UpsertEmbeddingAsync`'s return value; if `false`, the table
isn't set up and `MemoryService`'s existing app-level cosine/keyword path
should be used instead, exactly like the fallback logic already inside
`PgVectorSimilarityService` itself).

## Why not wired in by default

If `MemoryService` called this unconditionally and the table didn't exist,
every memory operation would either throw or silently fall back — and
distinguishing "pgvector isn't set up" from "pgvector is set up but broken"
matters for a system this document has tried consistently not to paper
over. Keeping it as an explicit opt-in, with the honest fallback behavior
demonstrated in `PgVectorSimilarityService`'s own error handling, is safer
than guessing.
