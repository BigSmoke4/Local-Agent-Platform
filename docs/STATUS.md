# Implementation Status

This file is the authoritative status ledger for the current repository. “Implemented” means concrete code exists. “Verified” means the relevant executable test has actually been run in an environment with the required runtime/dependency.

## Implemented

| Area | Status | Notes |
|---|---|---|
| Model runtime | Implemented | `OllamaModelProvider` uses real HTTP endpoints. |
| Branching task graphs | Implemented | Plans are validated DAGs with stable IDs and dependencies; execution is deterministic/sequential. |
| Agent command permissions | Implemented, constrained | `AgentSession.OwnerUserId` is passed into `ToolExecutionService`; persistent `AlwaysDeny` rules apply to agent calls. `AlwaysAllow` is intentionally unavailable while `TerminalTool` is High risk, and no persisted allow bypasses that gate. |
| Semantic/vector memory | Implemented | Ollama embeddings + cosine/lexical hybrid ranking with lexical fallback. |
| Repository Context Engine | Implemented | Current indexed repository context is selected independently from durable Memory. |
| Repository indexing | Implemented | Hash-based incremental scanner + C# Roslyn symbols + deletion tracking. Interrupted indexing jobs are not automatically resumed. |
| Unclaimed agent-session recovery | Implemented, partially verified | Startup re-enqueues persisted `Created` sessions and relies on atomic database claims; cross-worker claim/cancellation tests pass in CI. Sessions interrupted after claiming are intentionally not replayed because tool side effects are not exactly-once safe. |
| Cross-file graph | Implemented, bounded | Generates cross-file `References` relationships using indexed symbols and real file contents; capped for indexing safety. |
| Symbol pagination | Implemented | Server-side query, page, and page-size handling. |
| Workspace-root allowlist | Implemented, CI-tested | Registration, repository/session listings, MVC/API operations, tool execution, indexing, context, verification, IDE open, and session telemetry enforce configured roots; default is fail-closed and Compose allows `/workspace`. Unit/integration tests pass in CI; filesystem race resistance still depends on OS/container isolation. |
| Symlink-aware workspace containment | Implemented, CI-tested | Shared path containment rejects workspace escapes through symlink/reparse-point segments; configured allowlist roots are snapshotted as physical paths; a repository root itself may not be a link, and symlink ancestors are canonicalized for containment. Linux filesystem tests pass in CI. |
| Container privilege hardening | Image build and hardened runtime CI-tested | CI builds the image, starts it against PostgreSQL with all capabilities dropped except `CHOWN`, `SETUID`, and `SETGID`, and confirms `/health/live` succeeds while PID 1 runs as UID `1000`. Compose runtime itself has not been exercised. Tool subprocesses still share the web UID/namespace and are not a hostile-code sandbox. |
| IDE integration | Implemented, partially verified | Server-side VS Code CLI provider remains available. A standalone VS Code extension under `integrations/vscode` stores its API key in SecretStorage and supports repository/model discovery, session start/list, task inspection, one-time approval, cancellation, changed-file listing, and safe opening through the session repository ID plus configured server-to-local mapping. Its 23 Node tests pass, VSIX packaging succeeds, and its Extension Host smoke test passes in GitHub Actions. A live-platform end-to-end test has not run. |
| Roles | Implemented | `Admin` and `User`; first user is Admin. |
| MFA | Implemented | TOTP with Data Protection-protected secrets. |
| Password recovery | Implemented | One-time recovery code, hashed at rest and rotated after reset. |
| User isolation | Implemented | Projects/repositories/sessions/tool calls are scoped to the authenticated owner in the principal execution paths. |
| NVIDIA telemetry | Implemented | `nvidia-smi` utilization/VRAM/temp/power. |
| AMD telemetry | Implemented, best-effort | `rocm-smi --json`; field compatibility depends on ROCm version. |
| Security/concurrency regression tests | Added, CI-verified | Domain and PostgreSQL integration suites pass in GitHub Actions, covering terminal/Git/file mutation, symlink/workspace boundaries, DAG validation, repository traversal, secret redaction, regex scanning, verified test summaries, failed-task propagation, cross-worker claim/cancellation, corrupt persisted task metadata, and file deletion/reappearance. |
| Real Ollama HTTP test | Implemented, opt-in | Enabled with `LAP_RUN_LIVE_TESTS=1`; requires a live Ollama instance. |
| Load-test suite | Implemented | k6 API/health scenario with thresholds; requires k6 and a running instance. |
| CI | Implemented, verified in GitHub Actions | 23 VS Code Node tests, VS Code Extension Host smoke test, .NET Release build, Domain tests, PostgreSQL integration tests, temporary EF baseline generation/artifact publication, Docker image build, and a hardened PostgreSQL-backed runtime smoke test (live health plus UID `1000`) passed in [GitHub Actions](https://github.com/BigSmoke4/Local-Agent-Platform/actions/runs/37735619610). |
| CD | Implemented/configurable | GHCR publish; optional SSH production deploy and readiness gate. |
| Fresh-schema bootstrap | Implemented | Startup uses `EnsureCreatedAsync()` only when no EF migrations exist; otherwise `MigrateAsync()`. |

## Environment-dependent verification

- VS Code's `code` CLI must be installed/visible for the server-side open-file adapter. The standalone extension requires VS Code 1.90+ and a reachable Local Agent Platform API. Extension Host tests pass on GitHub Actions; a live-server end-to-end test has not run.
- `nvidia-smi` and `rocm-smi` must be installed/visible for NVIDIA/AMD telemetry respectively.
- `nomic-embed-text` (or the configured embedding model) must be pulled for semantic memory; lexical fallback works without it.
- Live Ollama tests require an actual Ollama instance and coding model.
- SSH CD requires a configured GitHub `production` environment and deployment secrets.
- k6 load tests require k6 and a running instance.

## Deliberate design limits, not fake features

- DAG nodes are not executed concurrently. The graph is general, but scheduling remains sequential until per-file/resource locking exists.
- Embeddings are stored as JSON vectors and ranked in application memory. This is intended for local-scale collections, not ANN-scale retrieval; `pgvector` is a natural scale-up path.
- The cross-file graph records bounded symbol references, not a compiler-grade interprocedural call graph across every language.
- No Cursor, JetBrains, or Antigravity-specific plugin is included. The authenticated REST API and VS Code extension are the supported IDE integration surfaces.
- Tool subprocesses share the web process UID/container namespace; Compose is defense-in-depth, not a hostile-code sandbox.

## Schema migration status

CI successfully generated and compiled the baseline migration, ran the build/tests against it, and published a temporary [`ef-migration-baseline` artifact](https://github.com/BigSmoke4/Local-Agent-Platform/actions/runs/37735619610#artifacts). The migration is not committed to the repository yet. For production schema evolution, review that artifact (or run `./scripts/create-baseline-migration.sh` with the .NET 8 SDK) and commit the migration/snapshot. Once committed, startup switches to `Database.MigrateAsync()` for fresh and existing databases.

The sandbox lacks the .NET SDK. Attempts to retrieve the generated artifact and job logs from GitHub's Actions storage endpoints failed with `EOF`, so the generated migration files have not been brought into this checkout. Fresh installs still work through `EnsureCreatedAsync()` until the baseline is reviewed and committed.

## Reproducible validation commands

```bash
npm ci --prefix integrations/vscode
npm --prefix integrations/vscode test
xvfb-run -a npm --prefix integrations/vscode run test:extension-host
npx --yes @vscode/vsce package --no-dependencies --out /tmp/local-agent-platform.vsix
dotnet restore src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj
dotnet build src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj -c Release
dotnet test tests/LocalAgentPlatform.Domain.Tests/LocalAgentPlatform.Domain.Tests.csproj -c Release
dotnet test tests/LocalAgentPlatform.Integration.Tests/LocalAgentPlatform.Integration.Tests.csproj -c Release
docker build -t local-agent-platform:ci .
```

With Ollama available:

```bash
ollama pull llama3.2:3b
ollama pull nomic-embed-text
LAP_OLLAMA_MODEL=llama3.2:3b ./scripts/run-live-tests.sh
```

Finally run k6 against the running app using `./scripts/run-load-test.sh`.
