# Implementation Status

This file is the authoritative status ledger for the current repository. “Implemented” means concrete code exists. “Verified” means the relevant executable test has actually been run in an environment with the required runtime/dependency.

## Verification for the current hardening patchset (2026-10-08)

- Passed locally: live-E2E harness syntax check and 8 Node safety tests; shell syntax checks; JSON/project-XML parsing; `git diff --check`.
- Not run locally: .NET build/unit/integration tests, PostgreSQL checks, Docker build/runtime smoke, or current-branch GitHub Actions. The environment has no .NET SDK, PostgreSQL client/server, or Docker. No live Ollama/platform session or deployment was performed.
- GitHub Actions run [37767552706](https://github.com/BigSmoke4/Local-Agent-Platform/actions/runs/37767552706) passed the prior PR revision; it predates the current Production-mode smoke, Data Protection permission checks, hardened tool-level approval checks, and immutable-SHA CD changes. It is not verification of this patchset.

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
| Container privilege hardening | Prior image/runtime CI-tested; current Production smoke pending | Previous CI confirmed `/health/live` with PID 1 at UID `1000` and only `CHOWN`, `SETUID`, and `SETGID` capabilities. The current smoke additionally exercises Production configuration, exact Host filtering, disabled Swagger, and `0700`/`0600` key-ring permissions, but has not run yet. Compose runtime itself has not been exercised. Tool subprocesses still share the web UID/namespace and are not a hostile-code sandbox. |
| Production startup policy | Implemented, current CI pending | Production requires exact non-loopback Host filtering, an existing writable persistent Data Protection directory, safe allowlisted workspaces, a trusted reverse-proxy IP, and an out-of-band first-admin token. Persistent key-ring permissions are normalized by the container entrypoint. Deployment persistence, encryption-at-rest, backup, and restore remain operator gates. |
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
| CI | Prior PR revision verified; current hardening pending | [Run 37767552706](https://github.com/BigSmoke4/Local-Agent-Platform/actions/runs/37767552706) passed the Node E2E-harness safety tests, disposable .NET fixture build/test, .NET Release build, Domain/PostgreSQL integration tests, EF snapshot validation/fresh-PostgreSQL migration, VS Code tests, the opt-in EnsureCreated-adoption safety fixture, Docker build, and hardened PostgreSQL-backed runtime smoke. It predates the current Production-mode smoke, Data Protection permission checks, tool-level approval checks, and immutable-SHA CD changes. It did not run live Ollama or the opt-in platform E2E session.
| CD | Configurable; immutable-SHA deployment change pending workflow validation | Publishes `latest` plus a full-SHA tag; optional SSH deployment is restricted to `main` and passes the full-SHA image via `LAP_DEPLOY_IMAGE`. The external production Compose file must use it and remain under a protected environment; no remote deployment has been exercised. |
| Fresh-schema bootstrap | Implemented, CI-tested | Startup applies committed EF migrations with `MigrateAsync()`. CI verifies the model snapshot and applies the baseline to clean PostgreSQL; only Development can fall back to `EnsureCreatedAsync()` if migrations are absent. |
| Existing `EnsureCreatedAsync()` adoption | Experimental; disposable-fixture CI-verified only | The opt-in helper backs up to a mode-0600 custom-format dump, restores it in a scratch database, requires an exact match with the generated `InitialCreate` schema, checks for competing sessions, and transactionally writes only EF migration-history rows. CI verifies both refusal of a deliberately mismatched schema (backup/data retained) and adoption of the exact disposable fixture followed by `dotnet ef database update`. No real or production database has been used; this remains an operator-reviewed experiment, not a supported production procedure. |

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

Databases previously initialized with `EnsureCreatedAsync()` do not have an `__EFMigrationsHistory` row. Do not deploy this baseline directly onto such a database: EF will try to create tables that already exist. An opt-in draft at `scripts/adopt-ensurecreated-database.sh` is exercised only against disposable PostgreSQL in CI, including its expected fail-closed mismatch path and a positive baseline-adoption path. That does not validate it against real installations, credentials, extensions, or operational conditions. Do not run it on a valuable/production database; treat any eventual cutover as a deliberate, backed-up, operator-reviewed maintenance procedure.

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
