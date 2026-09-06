# Implementation Status

This file is the authoritative status ledger for the current repository. "Implemented" means concrete code exists. "Verified" means the relevant executable test has actually been run in an environment with the required runtime/dependency.

## Implemented

| Area | Status | Notes |
|---|---|---|
| Model runtime | Implemented | `OllamaModelProvider` uses real HTTP endpoints. |
| Branching task graphs | Implemented | Plans are validated DAGs with stable IDs and dependencies; execution is deterministic/sequential. |
| Agent command permissions | Implemented | `AgentSession.OwnerUserId` is passed into `ToolExecutionService`, so persisted per-user rules apply to agent calls. |
| Semantic/vector memory | Implemented | Ollama embeddings + cosine/lexical hybrid ranking with lexical fallback. |
| Repository Context Engine | Implemented | Current indexed repository context is selected independently from durable Memory. |
| Repository indexing | Implemented | Hash-based incremental scanner + C# Roslyn symbols + deletion tracking. |
| Cross-file graph | Implemented, bounded | Generates cross-file `References` relationships using indexed symbols and real file contents; capped for indexing safety. |
| Symbol pagination | Implemented | Server-side query, page, and page-size handling. |
| Symlink-aware sandboxing | Implemented | Path containment rejects workspace escapes through symlink/reparse-point segments. |
| Concrete IDE adapter | Implemented | VS Code CLI provider + authenticated API endpoints. Generic REST/OpenAPI remains available. |
| Roles | Implemented | `Admin` and `User`; first user is Admin. |
| MFA | Implemented | TOTP with Data Protection-protected secrets. |
| Password recovery | Implemented | One-time recovery code, hashed at rest and rotated after reset. |
| User isolation | Implemented | Projects/repositories/sessions/tool calls scoped to the authenticated owner in the principal execution paths. |
| NVIDIA telemetry | Implemented | `nvidia-smi` utilization/VRAM/temp/power. |
| AMD telemetry | Implemented, best-effort | `rocm-smi --json`; field compatibility depends on ROCm version. |
| Real process tests | Implemented | Terminal and Git process integration tests. |
| Real Ollama HTTP test | Implemented, opt-in | Enabled with `LAP_RUN_LIVE_TESTS=1`. |
| Load-test suite | Implemented | k6 API/health scenario with thresholds. |
| CI | Implemented | Build + unit + Postgres integration tests + Docker build. |
| CD | Implemented/configurable | GHCR publish; optional SSH production deploy and readiness gate. |
| Fresh-schema bootstrap | Implemented | Startup uses `EnsureCreatedAsync()` only when no EF migrations exist; otherwise `MigrateAsync()`. |

## Environment-dependent verification

The following code requires host capabilities and therefore cannot be truthfully claimed as universally available:

- `nvidia-smi` must be installed/visible for NVIDIA telemetry.
- `rocm-smi` must be installed/visible for AMD telemetry.
- VS Code's `code` CLI must be installed/visible for the concrete IDE adapter.
- `nomic-embed-text` (or the configured embedding model) must be pulled for semantic memory; lexical fallback works without it.
- Live Ollama tests require an actual Ollama instance and coding model.
- SSH CD requires a configured GitHub `production` environment and deployment secrets.
- k6 load tests require k6 and a running instance.

## Deliberate design limits, not fake features

- DAG nodes are not executed concurrently. The graph is general, but scheduling is sequential until per-file/resource locking exists.
- Embeddings are stored as JSON vectors and ranked in application memory. This is correct for small/local memory stores but not an ANN solution; `pgvector` is the natural scale-up path.
- The cross-file graph currently records bounded symbol references. It is not a full compiler-grade interprocedural call graph across every language.
- The VS Code adapter is CLI-based. There is no custom VS Code extension UI, Cursor plugin, JetBrains plugin, or Antigravity plugin in this repository.
- AMD telemetry parser is best-effort because ROCm JSON field names vary by version.

## Schema migration status

The repository can now boot a completely fresh database without a committed migration because startup detects an empty migration assembly and calls `EnsureCreatedAsync()`.

For production schema evolution, generate and commit a baseline migration from a machine with the .NET 8 SDK:

```bash
./scripts/create-baseline-migration.sh
```

After any migration exists, startup switches to `Database.MigrateAsync()`.

This authoring environment did not contain the .NET SDK, so a conventional EF-generated migration/snapshot could not be produced or compiled here. Do not hide that distinction: fresh installation works, but long-term migration history should still be generated and checked into source control.

## Validation to run after extraction

```bash
dotnet restore src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj
dotnet build src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj -c Release
dotnet test tests/LocalAgentPlatform.Domain.Tests/LocalAgentPlatform.Domain.Tests.csproj -c Release
dotnet test tests/LocalAgentPlatform.Integration.Tests/LocalAgentPlatform.Integration.Tests.csproj -c Release
```

Then, with Ollama available:

```bash
ollama pull llama3.2:3b
ollama pull nomic-embed-text
LAP_OLLAMA_MODEL=llama3.2:3b ./scripts/run-live-tests.sh
```

Finally run k6 against the running app using `./scripts/run-load-test.sh`.
