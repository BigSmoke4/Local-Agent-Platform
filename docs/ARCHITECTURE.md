# Architecture

## Shape

Local Agent Platform is a modular monolith: one ASP.NET Core host (`LocalAgentPlatform.Web`) composes application services from the modules under `src/Modules/`. The modules have separate project files and Domain/Application/Infrastructure boundaries where needed. MVC controllers and Razor views live in the host rather than in per-module presentation projects.

```text
Browser / IDE client
  → Web host (MVC, authenticated JSON API, SignalR)
    → Application services
      → Domain policies and parsers
      → Infrastructure adapters (PostgreSQL/EF Core, processes, filesystem, HTTP)
    → Shared.Data (PlatformDbContext)
    → Shared.Kernel (tool/model/telemetry/filesystem/background-work abstractions)
```

## Module map

| Module | Current implementation |
|---|---|
| Models | Ollama chat and embedding adapters, model registry, hardware-aware recommendations, telemetry providers. |
| RepositoryAnalysis | Workspace-root-constrained scanning, SHA-256 incremental indexing, C# Roslyn symbol extraction, bounded cross-file references, paginated symbol views, repository context selection. |
| Tools | File read/list/write/edit, terminal, Git, build, and test tools; shared risk/approval policy, owner/workspace checks, output limits, and audit rows. |
| Agent | Model-backed planning, persisted task DAGs, deterministic topological execution, budgets, retries, approvals, verification, bounded repair, cancellation, and summaries. |
| Verification | Real build/test result consumption, fresh bounded security-pattern scans, and advisory model review. Incomplete security coverage fails closed. |
| Memory | Owner-scoped durable memories, local embedding integration, hybrid lexical/cosine retrieval, lexical fallback, and execution-outcome records. |
| IdeIntegration | Server-side VS Code CLI provider exposed through authenticated API endpoints. A separate VS Code extension client lives under `integrations/vscode`; it uses the platform's session/model/repository APIs. No Cursor, JetBrains, or Antigravity plugin is included. |
| Projects | Ownership and repository association currently use shared entities; a richer standalone Projects domain/application module is not present. |
| Execution | Reserved marker module; agent lifecycle and tool execution currently live in Agent and Tools rather than a separate execution runtime. |

## Agent plans and task execution

The planner parses structured model output into steps with stable IDs and `dependsOn` edges. The orchestrator validates IDs, duplicate/missing dependencies, self-edges, and cycles, then executes a deterministic topological order. It records task states and broadcasts changes. Execution is deliberately sequential: there is no parallel scheduler or per-file/resource lock manager, so independent DAG branches do not currently run concurrently. Tool approval and retry rules are enforced for each invocation, and high-risk tools require fresh approval.

Session claims use conditional database updates as well as the in-process registry so duplicate queue deliveries on separate instances cannot both claim a `Created` or `AwaitingApproval` session. Pending-session cancellation uses an owner-scoped conditional transition; active runs receive a cancellation token. Cancellation/failure state updates are limited to runs that actually won the claim.

## Repository access and filesystem boundaries

`IWorkspaceRootPolicy` is fail-closed when its configured root list is empty. It accepts only existing real directory roots under existing real allowlist roots; a configured allowlist root or repository root that is itself a link/reparse point is rejected. Allowlist roots are resolved to physical paths at startup, and existing path segments are canonicalized for containment so symlinked ancestors cannot redirect a repository outside the allowlist. Shared path checks reject workspace escapes. Repository traversal skips linked directories for indexing and fails closed for security verification if traversal could not provide complete supported-file coverage. File mutations require an expected content hash and use bounded, same-directory atomic replacement.

These are application-level controls, not a complete OS sandbox. Tool subprocesses can execute repository-controlled code after approval; hostile repositories still require container/VM isolation and appropriately narrow mounts.

## Memory and context

Memory and repository context are separate on purpose. Memory contains previous decisions, preferences, and execution outcomes; the Context Engine selects bounded excerpts from the current indexed repository. Memory uses local embeddings when available and combines cosine similarity with lexical/recency/importance ranking; it falls back to lexical retrieval when embedding inference is unavailable. Vectors are stored as JSON and ranked in application memory, not through a database ANN index, so this is intended for local-scale collections.

## Background work and real-time updates

Long-running indexing and agent work is dispatched through a `System.Threading.Channels` FIFO queue drained by one `BackgroundService`. The queue is in-memory and single-worker, not a durable broker. At startup, persisted agent sessions still in `Created` are re-enqueued; conditional database claims prevent duplicate execution if multiple instances recover the same row. Sessions interrupted after claim are not replayed because tools can have non-idempotent side effects; they need a separate lease/reconciliation design. Indexing jobs are also not resumed after interruption. SignalR emits session/task state changes and hardware telemetry; the current UI refreshes session details rather than applying task-specific DOM patches.

## Model use

The local `IModelProvider` is used for planning and advisory review. Tool execution, budgets, path validation, file hashing, build/test process results, output parsing, and security-pattern matching are deterministic code. A reviewer verdict never overrides an actual build/test/security gate.

## Authentication and authorization

Cookie authentication is used for the MVC UI and API-key authentication for `/api/*`. API keys are stored as hashes. Owner scoping is applied to projects, repositories, sessions, memory, telemetry, IDE endpoints, and tool calls. The first account becomes Admin; later registration requires an authenticated Admin. MFA uses TOTP with Data Protection-protected secrets, and recovery codes are one-time and stored as hashes.

## Testing and verification

`LocalAgentPlatform.Domain.Tests` covers pure policies/parsers. `LocalAgentPlatform.Integration.Tests` exercises real EF Core/PostgreSQL, filesystem, process, and Roslyn behavior; the PostgreSQL fixture requires a real database. The opt-in `scripts/run-live-platform-e2e.js` drives an actual authenticated API session through a running PostgreSQL/Ollama-backed platform, but its live path has not yet been executed; CI covers only the harness's safety checks and the pinned disposable test fixture. The current GitHub Actions PR run passed the .NET Release build, Domain tests, PostgreSQL integration tests, Docker image build, and a hardened Docker runtime smoke test against PostgreSQL that checks `/health/live` and UID `1000`. This authoring sandbox did not have the .NET SDK or Docker, so those results were obtained on the CI runner rather than locally.

Security verification scans the repository afresh rather than trusting the incremental index. It rejects out-of-root/non-regular paths and enforces traversal, file-count, size, byte, and finding limits. A scan exception or incomplete coverage does not produce a passing verification run. The regex rules are a narrow heuristic, not a general SAST engine.

## Database bootstrap and deployment

The web host applies the committed EF Core baseline migration at startup. CI checks for pending model changes and applies the baseline to a fresh PostgreSQL database before the integration suite; the Docker runtime smoke test also exercises startup with a clean PostgreSQL service. `EnsureCreatedAsync()` remains a Development-only fallback if a migration assembly is missing, while non-Development startup fails closed. Existing databases previously initialized with `EnsureCreatedAsync()` lack an EF migration-history row and require a deliberate, backed-up baseline-adoption cutover before `MigrateAsync()` can be used safely. `Dockerfile` uses the .NET SDK image at runtime because BuildTool/TestTool invoke `dotnet` against mounted repositories. A trusted entrypoint repairs key-ring volume ownership and then drops the web process to a configurable non-root UID; Compose restricts entrypoint capabilities and sets `no-new-privileges`, but tool processes still share the web process identity and namespace. Compose remains local-development hardening, not a production or hostile-code sandbox.
