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
| IDE integration | Implemented, partially verified | Server-side VS Code CLI provider remains available. A standalone VS Code extension under `integrations/vscode` stores its API key in SecretStorage and supports repository/model discovery, session start/list, task inspection, one-time approval, cancellation, changed-file listing, and safe opening through the session repository ID plus configured server-to-local mapping. Its 23 Node tests pass, VSIX packaging succeeds, and its Extension Host smoke test passes in GitHub Actions. The extension has not been exercised against a live platform. |
| Live platform/API end-to-end harness | Approval-boundary guarded; not live-verified | `scripts/run-live-platform-e2e.js` calls a real authenticated platform and configured Ollama model only when an operator runs it against the pinned disposable fixture. It never calls the approval endpoint; on a system BuildTool/TestTool task in `AwaitingApproval`, it stops with exit code 2 and preserves the pending session and marker for a human. Regression tests enforce the no-approval boundary. No live app/model run has occurred. |
| Roles | Implemented | `Admin` and `User`; first user is Admin. |
| MFA | Implemented | TOTP with Data Protection-protected secrets. |
| Password recovery | Implemented | One-time recovery code, hashed at rest and rotated after reset. |
| User isolation | Implemented | Projects/repositories/sessions/tool calls are scoped to the authenticated owner in the principal execution paths. |
| NVIDIA telemetry | Implemented | `nvidia-smi` utilization/VRAM/temp/power. |
| AMD telemetry | Implemented, best-effort | `rocm-smi --json`; field compatibility depends on ROCm version. |
| Security/concurrency regression tests | Added, CI-verified | Domain and PostgreSQL integration suites pass in GitHub Actions, covering terminal/Git/file mutation, symlink/workspace boundaries, DAG validation, repository traversal, secret redaction, regex scanning, verified test summaries, failed-task propagation, cross-worker claim/cancellation, corrupt persisted task metadata, and file deletion/reappearance. |
| Real Ollama HTTP test | Implemented, opt-in | Performs actual model-list and generation HTTP requests. It is explicitly skipped—not passed—unless `LAP_RUN_LIVE_TESTS=1`; a live Ollama endpoint and pulled model are required. No live model call was made in the latest CI run. |
| Load-test suite | Implemented | k6 API/health scenario with thresholds; requires k6 and a running instance. |
| CI | Implemented, verified in GitHub Actions | [Run 37743153211](https://github.com/BigSmoke4/Local-Agent-Platform/actions/runs/37743153211) passed the Node E2E-harness safety tests, disposable .NET fixture build/test, .NET Release build, Domain/PostgreSQL integration tests, EF snapshot validation/fresh-PostgreSQL migration, VS Code tests, Docker build, and hardened PostgreSQL-backed runtime smoke. This still does not run a live Ollama model or the opt-in platform E2E session. |
| CD | Implemented/configurable | GHCR publish; optional SSH production deploy and readiness gate. |
| Fresh-schema bootstrap | Implemented, CI-tested | Startup applies committed EF migrations with `MigrateAsync()`. CI verifies the model snapshot and applies the baseline to clean PostgreSQL; only Development can fall back to `EnsureCreatedAsync()` if migrations are absent. |

## Environment-dependent verification

- VS Code's `code` CLI must be installed/visible for the server-side open-file adapter. The standalone extension requires VS Code 1.90+ and a reachable Local Agent Platform API. Extension Host tests pass on GitHub Actions; a live-server end-to-end test has not run.
- `nvidia-smi` and `rocm-smi` must be installed/visible for NVIDIA/AMD telemetry respectively.
- `nomic-embed-text` (or the configured embedding model) must be pulled for semantic memory; lexical fallback works without it.
- Live Ollama tests require an actual Ollama instance and coding model.
- The live platform/API E2E harness requires a reachable running app backed by PostgreSQL and Ollama, an owner-scoped API key, a registered model/repository, Node.js 18+, Git, and an exact clean copy of `tests/fixtures/live-platform-e2e/`. It never approves tasks; if it observes the system BuildTool/TestTool gate in `AwaitingApproval`, it exits with code 2 and leaves the human-approval decision to the platform UI. It has not been run against a live platform.
- SSH CD requires a configured GitHub `production` environment and deployment secrets.
- k6 load tests require k6 and a running instance.

## Deliberate design limits, not fake features

- DAG nodes are not executed concurrently. The graph is general, but scheduling remains sequential until per-file/resource locking exists.
- Embeddings are stored as JSON vectors and ranked in application memory. This is intended for local-scale collections, not ANN-scale retrieval; `pgvector` is a natural scale-up path.
- The cross-file graph records bounded symbol references, not a compiler-grade interprocedural call graph across every language.
- No Cursor, JetBrains, or Antigravity-specific plugin is included. The authenticated REST API and VS Code extension are the supported IDE integration surfaces.
- Tool subprocesses share the web process UID/container namespace; Compose is defense-in-depth, not a hostile-code sandbox.

## Schema migration status

The generated EF Core baseline is committed in `src/Shared/Data/Migrations/` (migration, designer, and model snapshot). CI verifies that the snapshot matches the current model and applies the migration to a clean PostgreSQL database; the Docker runtime smoke test also starts the application against a fresh PostgreSQL service. New installations use `Database.MigrateAsync()` at startup. Non-Development environments fail closed rather than silently creating an unmanaged schema when migrations are missing.

Databases previously initialized with `EnsureCreatedAsync()` do not have an `__EFMigrationsHistory` row. Do not deploy this baseline directly onto such a database: EF will try to create tables that already exist. Back up and verify the schema, then perform an explicit baseline-adoption cutover before applying future migrations. No automatic adoption procedure is included yet; pre-migration installations still require a deliberate cutover.

## Reproducible validation commands

```bash
npm ci --prefix integrations/vscode
npm --prefix integrations/vscode test
node --test tests/live-platform-e2e.test.js
xvfb-run -a npm --prefix integrations/vscode run test:extension-host
npx --yes @vscode/vsce package --no-dependencies --out /tmp/local-agent-platform.vsix
dotnet restore src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj
dotnet build src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj -c Release
dotnet test tests/fixtures/live-platform-e2e/Disposable.csproj -c Release
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
