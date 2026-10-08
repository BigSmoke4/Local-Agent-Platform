# Local Agent Platform

A local-first autonomous coding platform built with **ASP.NET Core 8, PostgreSQL, EF Core, Ollama, SignalR, Roslyn, and a modular-monolith architecture**. It can index repositories, build repository context, plan multi-step coding work, execute guarded local tools, verify changes, retain semantic memory, expose an authenticated API, and stream telemetry without requiring a cloud LLM.

> This repository intentionally distinguishes implemented features from environment-dependent integrations. See [`docs/STATUS.md`](docs/STATUS.md) for the current verification matrix and [`docs/SECURITY.md`](docs/SECURITY.md) for the security model.

## What is implemented

### Agent engine

- Persisted agent sessions with explicit lifecycle states.
- Model-driven JSON planning through `IModelProvider`.
- **General DAG task plans** with stable step IDs and `dependsOn` edges.
- Cycle/unknown-dependency validation before execution.
- Deterministic topological execution of dependency graphs; execution is sequential (not parallel) until resource/file locking exists.
- Iteration, retry, duration, and repair budgets.
- Human approval gates for risky tool calls; approvals are scoped to a single invocation and retries require a new approval.
- Build and test verification use real `dotnet` processes and require explicit approval because repository targets/tests execute code.
- A bounded, heuristic security-pattern scan covers a fresh workspace traversal; it fails closed on inaccessible, linked, or over-budget coverage. It is not a full SAST engine.
- Verification, reviewer, and bounded repair loop.
- Cancellation and SignalR state broadcasts.
- Startup recovery re-enqueues persisted sessions that remain unclaimed in `Created`; already-claimed sessions are not replayed because external tool side effects are not generally exactly-once safe.

### Repository intelligence and Context Engine

- Recursive filesystem scanning with ignore rules.
- SHA-256 incremental indexing.
- Real Roslyn C# symbol extraction.
- Soft-deleted file tracking.
- Symbol browser with server-side search and pagination.
- Cross-file symbol-reference graph generation.
- **Repository-wide Context Engine** that selects indexed files/symbols relevant to the current request and injects bounded current-code context into planning.
- Memory and repository context remain separate by design: Memory represents previous decisions/executions/preferences; Context represents the current repository state.

### Semantic memory

- Short-term, working, long-term, user-preference, and execution memory scopes.
- Ollama `/api/embed` integration through `IEmbeddingProvider`.
- Portable persisted embedding vectors on `MemoryEntry`.
- **Hybrid retrieval:** cosine semantic similarity blended with lexical relevance, recency, and importance.
- Automatic lexical fallback if the configured local embedding model is unavailable.
- Execution outcomes automatically become searchable memory.

### Tool execution and sandboxing

Eight real tools are registered through `ITool`:

- `FileReadTool`
- `DirectoryListTool`
- `FileWriteTool`
- `FileEditTool`
- `TerminalTool`
- `GitTool`
- `BuildTool`
- `TestTool`

Tool execution includes:

- Per-tool risk levels and timeouts.
- Command allow/deny/approval policy.
- Persistent per-user **Always Deny** rules are available for terminal executables and apply to agent-initiated calls. **Always Allow is intentionally unavailable for the current High-risk TerminalTool**; every terminal call requires its own approval, and legacy allow records cannot clear that independent gate.
- Audit rows for allowed, denied, pending, successful, and failed executions.
- A fail-closed configured workspace-root allowlist (`Repositories:AllowedRoots`; Compose allows `/workspace`).
- Path traversal checks plus **symlink/reparse-point-aware workspace escape prevention**.
- Secret redaction in process output, tool audit records, and security-finding excerpts.
- The web process in Docker Compose drops to a configurable non-root UID before starting; the trusted entrypoint retains only the capabilities needed to repair key-ring volume ownership and drop privileges. `no-new-privileges` is enabled; tool processes still share the web UID/namespace, so this is defense in depth, not a hostile-code sandbox.

### Authentication, authorization, and user isolation

- PBKDF2-SHA256 password hashing.
- Cookie authentication for MVC.
- SHA-256-hashed API keys for `/api/*`.
- `Admin` and `User` roles.
- First account becomes `Admin`; additional accounts require an authenticated admin.
- TOTP MFA using standard authenticator apps.
- Offline one-time recovery-code password reset; recovery codes are stored hashed and rotated after use.
- MFA secrets are protected with ASP.NET Core Data Protection.
- **Per-user project/repository/session isolation** in MVC, API, and tool execution paths.
- User-scoped command permissions and tool audit visibility.

### IDE integration

- An authenticated REST API supports compatible editor and automation clients.
- A standalone **VS Code extension** is available under [`integrations/vscode`](integrations/vscode): it discovers allowed repositories/models, opens local checkouts/files (including configured container-to-host path mappings), lists changed paths from completed file-write/edit tasks, starts sessions, polls session/task status every 15 seconds while its view is visible, and supports one-time approval and cancellation.
- The extension keeps the API key in VS Code SecretStorage and allows plain HTTP only for loopback hosts; remote API URLs must use HTTPS.
- A separate **VS Code CLI adapter** exposes `/api/ide/status` and `/api/ide/open` when the `code` executable is available to the web process.
- `IIdeIntegrationProvider` remains the abstraction for additional server-side adapters. See the extension README for setup and verification limits.

### Telemetry

- CPU/RAM telemetry from Linux `/proc` where available.
- Process working-set and disk usage telemetry.
- **NVIDIA telemetry through `nvidia-smi`**: GPU utilization, VRAM used/total, temperature, and power draw.
- **AMD ROCm telemetry through `rocm-smi`** where compatible output is available.
- Missing vendor tooling returns `null`/Unavailable rather than fabricated measurements.
- SignalR broadcasts hardware snapshots and agent state updates.

### API, observability, CI/CD

- Authenticated API-key-protected `/api/*` endpoints.
- Swagger/OpenAPI UI.
- Fixed-window API rate limiting.
- PostgreSQL and Ollama health checks.
- Serilog structured logging.
- OpenTelemetry ASP.NET Core/HTTP tracing.
- Dockerfile + Docker Compose.
- GitHub Actions CI for VS Code client tests, .NET restore/build/unit/PostgreSQL integration tests, and Docker build.
- **GitHub Actions CD** publishes images to GHCR and can deploy over SSH to a protected `production` environment when deployment variables/secrets are configured.

## Repository layout

```text
src/
  LocalAgentPlatform.Web/             MVC/API host, auth, SignalR, UI, VS Code CLI adapter
  Modules/
    Agent/                             planner + orchestrator
    Memory/                            semantic/lexical memory
    Models/                            Ollama model + embedding providers, telemetry
    RepositoryAnalysis/               indexing, symbols, context engine
    Tools/                             policy + tool execution
    Verification/                      build/test/security/reviewer pipeline
    IdeIntegration/                    IDE abstraction
    Projects/                          project module boundary
  Shared/
    Data/                              EF Core entities and PlatformDbContext
    Kernel/                            provider/tool/telemetry abstractions

integrations/
  vscode/                             VS Code extension + Node client/filesystem tests

tests/
  LocalAgentPlatform.Domain.Tests/
  LocalAgentPlatform.Integration.Tests/
  load/                               k6 scenarios

scripts/
  run-live-tests.sh
  run-load-test.sh
  create-baseline-migration.sh
```

## Prerequisites

For direct development:

- .NET 8 SDK
- PostgreSQL 16+
- Ollama
- Git
- Optional: Node.js for the VS Code API-client tests (`cd integrations/vscode && npm test`)
- Optional: VS Code `code` CLI for the server-side open-file adapter
- Optional GPU telemetry: `nvidia-smi` or `rocm-smi`
- Optional load tests: k6

Docker Compose can supply PostgreSQL and Ollama. Published service ports bind to loopback by default; they are not exposed to the LAN. A trusted entrypoint uses only the capabilities needed to repair key-ring volume ownership and drop privileges; the web process then runs as a configurable non-root UID (default `1000`), with all other capabilities dropped and `no-new-privileges` enabled. Make sure the host `workspace/` directory and mounted repositories are writable by that UID. On Linux, if your account uses a different UID/GID, export `LOCAL_AGENT_UID=$(id -u)` and `LOCAL_AGENT_GID=$(id -g)` before building the Compose service.

## Quick start with Docker Compose

```bash
docker compose up -d postgres ollama

docker compose exec ollama ollama pull llama3.2:3b
docker compose exec ollama ollama pull nomic-embed-text

docker compose up -d --build web
```

Open:

```text
http://localhost:8080
```

On a brand-new database the startup code bootstraps the EF schema with `EnsureCreatedAsync()` if the assembly contains no migrations. For a conventional long-term EF migration history, generate and commit a baseline migration using:

```bash
./scripts/create-baseline-migration.sh
```

After a migration exists, startup automatically uses `Database.MigrateAsync()`.

## Run directly

Start dependencies:

```bash
docker compose up -d postgres ollama
ollama pull llama3.2:3b
ollama pull nomic-embed-text
```

Set configuration if needed:

```bash
export ConnectionStrings__PlatformDb='Host=localhost;Port=5432;Database=local_agent_platform;Username=postgres;Password=postgres'
export Ollama__BaseUrl='http://localhost:11434'
export Ollama__EmbeddingModel='nomic-embed-text'
```

Then:

```bash
dotnet restore src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj
dotnet build src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj
dotnet run --project src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj
```

## First account and security setup

The first registered account becomes `Admin`. Registration closes to anonymous users after that; an admin can create additional users.

Each new account receives a random recovery code displayed exactly once. Save it securely. It is hashed in the database and rotated when used.

After login, open the Account Security page and choose **Set up MFA**. Add the displayed Base32 secret to any standard TOTP authenticator and confirm with the current six-digit code.

## Register a repository

Repository access is restricted to explicitly configured workspace roots. The default configuration is fail-closed (`Repositories:AllowedRoots` is empty), so direct runs must set an allowlisted root before registering repositories. For example:

```bash
export Repositories__AllowedRoots__0="$HOME/dev"
```

The Docker Compose configuration allowlists `/workspace`. Mount repositories under the Compose `./workspace:/workspace` volume and register a path beneath it, for example:

```text
/workspace/my-repository
```

Repository/project queries are scoped to the authenticated owner.

## Models and embeddings

Default configuration:

```json
{
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "RequestTimeoutSeconds": 300,
    "EmbeddingModel": "nomic-embed-text"
  }
}
```

The coding model is selected by the user/session. Semantic memory uses `Ollama__EmbeddingModel`. If the embedding model is absent or Ollama is down, memory retrieval continues with lexical ranking.

## Tests

Run normal tests:

```bash
dotnet test tests/LocalAgentPlatform.Domain.Tests/LocalAgentPlatform.Domain.Tests.csproj -c Release
dotnet test tests/LocalAgentPlatform.Integration.Tests/LocalAgentPlatform.Integration.Tests.csproj -c Release
```

Integration tests require PostgreSQL. They also exercise real child processes for supported tool tests.

Run opt-in **live Ollama HTTP tests**:

```bash
export LAP_OLLAMA_MODEL='llama3.2:3b'
./scripts/run-live-tests.sh
```

`LAP_RUN_LIVE_TESTS=1` prevents accidental model-dependent CI failures while still keeping a real local-runtime test suite in the repository.

## Load/performance testing

Install k6 and create an API key from the UI, then:

```bash
export API_KEY='lap_...'
export BASE_URL='http://localhost:8080'
export VUS=20
export DURATION=30s
./scripts/run-load-test.sh
```

Default thresholds:

- HTTP failure rate `< 1%`
- p95 HTTP request duration `< 750 ms`

Tune the scenario before treating those values as production SLOs.

## API and IDE integration

Swagger:

```text
/swagger
```

API clients send:

```text
X-Api-Key: lap_...
```

The standalone extension uses these authenticated API routes:

```text
GET  /api/repositories
GET  /api/models
GET  /api/agent/sessions
GET  /api/agent/sessions/{id}/tasks
POST /api/agent/sessions
POST /api/agent/sessions/{id}/approve
POST /api/agent/sessions/{id}/cancel
```

Session creation takes `repositoryId`, `modelId`, and `userRequest`. Repository discovery is owner-scoped and excludes paths outside the configured workspace roots. See [`integrations/vscode/README.md`](integrations/vscode/README.md) for extension setup.

The separate server-side CLI adapter exposes:

```text
GET  /api/ide/status
POST /api/ide/open
```

The open-file request accepts a canonical file path under an allowed repository, plus an optional line and column. This adapter requires the `code` executable to be available to the web process.

## CI/CD configuration

`.github/workflows/ci.yml` builds and tests the project against a PostgreSQL service container.

`.github/workflows/cd.yml`:

1. Builds the Docker image.
2. Publishes `latest` and SHA-tagged images to GHCR.
3. Optionally deploys the `web` service over SSH.
4. Gates deployment with `/health/ready`.

For SSH deployment configure a GitHub `production` environment with:

```text
Variable: ENABLE_SSH_DEPLOY=true
Variable: DEPLOY_PATH=/path/to/deployment

Secrets:
DEPLOY_HOST
DEPLOY_USER
DEPLOY_SSH_KEY
GHCR_PAT
```

Production secrets remain outside the repository and should be supplied through GitHub Environments, container secrets, environment variables, or your organization’s secret manager.

## Important operational notes

- DAG execution is dependency-aware but intentionally sequential. Parallel execution can corrupt shared working trees unless resource/file locking is introduced first.
- Cross-file relationship generation is bounded to avoid unbounded indexing cost on very large repositories.
- The semantic memory vector is currently stored as JSON for provider/database portability rather than requiring `pgvector`. For very large memory stores, migrate to `pgvector` or another ANN index.
- NVIDIA and ROCm telemetry depend on vendor CLIs being installed and visible to the application process.
- The VS Code extension has 23 passing Node API-client/command-flow/manifest/filesystem-safety tests and packages successfully as a VSIX. The VS Code Extension Host smoke test passes in GitHub Actions; local VS Code download was blocked by a TLS failure. Live-platform end-to-end testing remains outstanding.
- CI can generate and publish a temporary EF baseline migration artifact when none is committed; review and check that baseline into source control before treating schema evolution as production-ready.

## Documentation

- [`docs/STATUS.md`](docs/STATUS.md) — implementation and verification status
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — architecture
- [`docs/SECURITY.md`](docs/SECURITY.md) — authentication, authorization, sandboxing, remaining security considerations

## License

This project is licensed under the MIT License. See [`LICENSE`](LICENSE).
