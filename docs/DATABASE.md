# Database

PostgreSQL via EF Core (`Npgsql.EntityFrameworkCore.PostgreSQL`). No
migrations are checked into this repository — see README.md for why
(no verified SDK/DB access in the environment that generated this code) and
run `dotnet ef migrations add InitialCreate` yourself.

## Tables (as of this build)

| Entity | Purpose |
|---|---|
| `AspNetUsers` + Identity tables | ASP.NET Core Identity (auth) |
| `Models` (`ModelDescriptor`) | Registered local models, one flagged `IsDefault` |
| `AgentSessions` | One row per agent run; tracks `State`, token counts, result |
| `ToolExecutions` | One row per tool invocation from the plain agent loop |
| `AgentTaskNodes` | Task graph nodes for the verification loop (Build/Test/Review/Repair) |
| `AuditLogs` | Append-only action log, written by nearly every controller |
| `FileSnapshots` | Pre-edit file content, for rollback |
| `CodeSymbols` | Roslyn-indexed symbol rows, incrementally refreshed by content hash |
| `Memories` | Retrieval-based memory rows, typed by `MemoryType` |
| `AutonomySettings` | One row per user, current `AutonomyLevel` |

## Indexing choices

- `ModelDescriptor.RuntimeId` — unique index (one registration per runtime model id)
- `AgentSession.State`, `AgentSession.CreatedAtUtc` — dashboard queries filter/sort on these
- `CodeSymbol.SymbolName`, `CodeSymbol.FilePath` — symbol search and per-file lookups
- `AutonomySetting.UserId` — unique (one setting row per user)

## Known scale limitation

`MemoryService.RetrieveAsync` pulls the 500 most recent `Memory` rows into
memory and scores them there, rather than pushing the relevance scoring
into SQL or a vector index. This is fine at the scale a single local
developer's memory table would reach, but is explicitly not the design for
a large multi-user memory table — see README's "not implemented" list for
the pgvector-based follow-up.
