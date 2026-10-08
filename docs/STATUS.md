# Implementation Status

This file is the authoritative status ledger for the current repository. "Implemented" means concrete code exists. "Verified" means the relevant executable test has actually been run in an environment with the required runtime/dependency.

## Implemented

| Area | Status | Notes |
|---|---|---|
| Model runtime | Implemented | `OllamaModelProvider` uses real HTTP endpoints. |
| Branching task graphs | Implemented | Plans are validated DAGs with stable IDs and dependencies; execution is deterministic/sequential. |
| Agent command permissions | Implemented, constrained | `AgentSession.OwnerUserId` is passed into `ToolExecutionService`; persistent `AlwaysDeny` rules apply to agent calls. `AlwaysAllow` is intentionally unavailable while `TerminalTool` is High risk, and no persisted allow bypasses that gate. |
| Semantic/vector memory | Implemented | Ollama embeddings + cosine/lexical hybrid ranking with lexical fallback. |
| Repository Context Engine | Implemented | Current indexed repository context is selected independently from durable Memory. |
| Repository indexing | Implemented | Hash-based incremental scanner + C# Roslyn symbols + deletion tracking. Interrupted indexing jobs are not automatically resumed. |
| Unclaimed agent-session recovery | Implemented, unverified | Startup re-enqueues persisted `Created` sessions and relies on atomic database claims; sessions interrupted after claiming are intentionally not replayed because tool side effects are not exactly-once safe. |
| Cross-file graph | Implemented, bounded | Generates cross-file `References` relationships using indexed symbols and real file contents; capped for indexing safety. |
| Symbol pagination | Implemented | Server-side query, page, and page-size handling. |
| Workspace-root allowlist | Implemented, unverified | Registration, repository/session listings, MVC/API operations, tool execution, indexing, context, verification, IDE open, and session telemetry enforce configured roots; default is fail-closed and Compose allows `/workspace`. Filesystem race resistance still depends on OS/container isolation. |
| Symlink-aware workspace containment | Implemented, unverified | Shared path containment rejects workspace escapes through symlink/reparse-point segments; configured allowlist roots are snapshotted as physical paths; a repository root itself may not be a link, and symlink ancestors are canonicalized for containment; traversal does not follow reparse-point directories. |
| Container privilege hardening | Implemented, unverified | A trusted entrypoint repairs key-ring volume ownership, then the web process drops to configurable non-root UID/GID; Compose drops all other capabilities, enables `no-new-privileges`, and binds published ports to loopback. Tool subprocesses still share the web UID/namespace and are not a hostile-code sandbox. |
| IDE integration | Implemented, partially verified | Server-side VS Code CLI provider remains available. A standalone VS Code extension under `integrations/vscode` stores its API key in SecretStorage and supports allowed-repository discovery, opening available local checkouts/files, session start/list, task inspection, one-time approval, cancellation, and a changed-files group derived only from completed `FileWriteTool`/`FileEditTool` tasks. Changed files open only through the session's server repository ID, configured local path mapping, and the existing containment-checked file resolver. Its 23 Node API-client, command-flow, manifest-wiring, path-mapping, changed-file, and local-file containment tests pass. A VSIX package was built successfully with `@vscode/vsce`, and the Extension Host smoke test passes in GitHub Actions. The local attempt could not download VS Code because `update.code.visualstudio.com` reset the TLS connection. Live-platform end-to-end tests have not run. |
| Roles | Implemented | `Admin` and `User`; first user is Admin. |
| MFA | Implemented | TOTP with Data Protection-protected secrets. |
| Password recovery | Implemented | One-time recovery code, hashed at rest and rotated after reset. |
| User isolation | Implemented | Projects/repositories/sessions/tool calls scoped to the authenticated owner in the principal execution paths. |
| NVIDIA telemetry | Implemented | `nvidia-smi` utilization/VRAM/temp/power. |
| AMD telemetry | Implemented, best-effort | `rocm-smi --json`; field compatibility depends on ROCm version. |
| Security/concurrency regression tests | Added, unverified | Terminal, Git, file mutation/symlink/root, DAG validation, repository traversal, secret redaction, regex scanner, verified test-summary, failed-task propagation, cross-worker agent claim/cancellation, null/blank path normalization, corrupt persisted task-argument JSON, and repository file deletion/reappearance cases; the .NET SDK was unavailable in this environment, so none are claimed as passing. |
| Real Ollama HTTP test | Implemented, opt-in | Enabled with `LAP_RUN_LIVE_TESTS=1`. |
| Load-test suite | Implemented | k6 API/health scenario with thresholds. |
| CI | Implemented | VS Code API-client Node tests + .NET build/unit/Postgres integration tests + Docker build. |
| CD | Implemented/configurable | GHCR publish; optional SSH production deploy and readiness gate. |
| Fresh-schema bootstrap | Implemented | Startup uses `EnsureCreatedAsync()` only when no EF migrations exist; otherwise `MigrateAsync()`. |

## Environment-dependent verification

The following code requires host capabilities and therefore cannot be truthfully claimed as universally available:

- `nvidia-smi` must be installed/visible for NVIDIA telemetry.
- `rocm-smi` must be installed/visible for AMD telemetry.
- VS Code's `code` CLI must be installed/visible for the server-side open-file adapter. The standalone extension requires VS Code 1.90+ and a reachable Local Agent Platform API; its VSIX packages successfully and all 23 Node API-client/command-flow/manifest/path-mapping/changed-file/local-file tests pass. The Extension Host smoke test is in CI; the local attempt failed before launch because `update.code.visualstudio.com` reset the TLS connection. No live-server test has run.
- `nomic-embed-text` (or the configured embedding model) must be pulled for semantic memory; lexical fallback works without it.
- Live Ollama tests require an actual Ollama instance and coding model.
- SSH CD requires a configured GitHub `production` environment and deployment secrets.
- k6 load tests require k6 and a running instance.

## Deliberate design limits, not fake features

- DAG nodes are not executed concurrently. The graph is general, but scheduling is sequential until per-file/resource locking exists.
- Embeddings are stored as JSON vectors and ranked in application memory. This is correct for small/local memory stores but not an ANN solution; `pgvector` is the natural scale-up path.
- The cross-file graph currently records bounded symbol references. It is not a full compiler-grade interprocedural call graph across every language.
- The VS Code extension supports explicit server-root-to-local-root path mappings for repository/file browsing and highlights paths from completed file-write/edit tasks; it does not include Cursor/JetBrains/Antigravity plugins. It has not yet been packaged or exercised through VS Code's Extension Host.
- AMD telemetry parser is best-effort because ROCm JSON field names vary by version.

## Schema migration status

The repository can now boot a completely fresh database without a committed migration because startup detects an empty migration assembly and calls `EnsureCreatedAsync()`.

For production schema evolution, run `./scripts/create-baseline-migration.sh` with the .NET 8 SDK, review the generated baseline, and commit it to source control. CI also generates and publishes a temporary `ef-migration-baseline` artifact when no migration exists. After a migration is committed, startup switches to `Database.MigrateAsync()`.

This authoring environment did not contain the .NET SDK, so a conventional EF-generated migration/snapshot could not be produced or compiled here. Do not hide that distinction: fresh installation works, but long-term migration history should still be generated and checked into source control.

## Validation to run after extraction

```bash
npm ci --prefix integrations/vscode
npm --prefix integrations/vscode test
xvfb-run -a npm --prefix integrations/vscode run test:extension-host
npx --yes @vscode/vsce package --no-dependencies --out /tmp/local-agent-platform.vsix
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
