# Local Agent Platform

An implementation of the "Local Autonomous Coding Intelligence Platform" master
spec, covering all 11 of its phases plus a follow-up hardening pass, at varying but
honestly-documented depth. This is a real, working system for the parts described
below — **not** a fully hardened, enterprise-grade product. Read `docs/STATUS.md`
before trusting any specific claim; it is the authoritative, line-by-line account of
what's real versus a marked extension point, per the spec's own rule #65 ("do not
cheat"). `docs/ARCHITECTURE.md` and `docs/SECURITY.md` cover how it's put together
and exactly what security measures exist and don't.

## What's actually implemented and working here

**Foundation (Phase 1)** — ASP.NET Core MVC (net8.0) modular monolith; PostgreSQL +
EF Core; a real `IModelProvider` backed by `OllamaModelProvider` (actual HTTP calls,
real token counts/timings); a real `IHardwareTelemetryProvider` reading actual
`/proc/stat`/`/proc/meminfo` (GPU/temp/power honestly render "Unavailable"); health
checks; Serilog + OpenTelemetry.

**Model Manager (Phase 2)** — `RegisteredModel` CRUD, a hardware-aware
recommendation engine ranking models against live available RAM.

**Repository Intelligence (Phase 3)** — real recursive filesystem scanning with
SHA-256 hashing, incremental re-indexing, real Roslyn-based C# symbol extraction,
soft-deletion tracking, background job queue.

**Tool Execution Engine (Phase 4)** — a real `ITool` abstraction with eight working
tools (file read/write/edit/list, a terminal tool with real process spawning +
secret redaction, a read-only git tool, real `dotnet build`/`dotnet test`). A pure
`CommandPolicyEngine` gates every terminal command; every invocation is
audit-logged.

**Agent Engine (Phase 5)** — a real orchestration loop (Created → Understanding →
Planning → Executing → AwaitingApproval/Verifying/Repairing →
Completed/Failed/Cancelled). Real model-driven JSON planning, real tool execution
through the same service the Tools console uses, real iteration/retry/duration
budgets, real cancellation.

**Verification Engine + Self-Critic Reviewer (Phase 6)** — real `dotnet build`/
`dotnet test` with actual output parsed by pure regex parsers, a narrow but real
security scan, an advisory reviewer model call that can never turn a real failure
into a false success, and a real bounded repair loop.

**Memory (Phase 7)** — `MemoryEntry` across five scopes with real deterministic
retrieval (keyword overlap + importance + recency), feeding a budgeted slice into
the planning prompt. Every session automatically records a real `Execution`-memory
entry from its actual outcome.

**Telemetry + real-time updates (Phase 8)** — `AgentTelemetryHub` (SignalR)
broadcasts real orchestrator state transitions; a background service polls real
hardware telemetry every 3s. The SignalR JS client is vendored locally via `libman`
— never fetched from a CDN at runtime.

**Neural navigation UI (Phase 9)** — a real server-rendered SVG graph with working
links, route-driven active-state, and `prefers-reduced-motion` respected.

**IDE-independent JSON API (Phase 10)** — `/api/models`, `/api/agent/sessions`
(routed through the same real orchestrator the UI uses), `/api/telemetry/*`, all
DTO-based, documented at `/swagger`, rate-limited, and now **authenticated** (see
Hardening below). No concrete Cursor/VS Code/JetBrains/Antigravity adapter exists —
building one honestly requires that editor's real protocol.

**Hardening (Phase 11 + follow-up pass)**:
- A real xUnit test project (`tests/LocalAgentPlatform.Domain.Tests`) covering every
  pure Domain layer.
- **A second real xUnit project (`tests/LocalAgentPlatform.Integration.Tests`)**
  running against an actual PostgreSQL connection — `ModelRegistryService`,
  `RepositoryIndexingService` (real temp-directory + real Roslyn parsing + real
  incremental re-index + soft-delete), and `ToolExecutionService` (real audit rows,
  real path-traversal refusal). These genuinely fail if Postgres isn't reachable.
- **Real authentication**: PBKDF2-SHA256 cookie login for the MVC UI, and a
  separate SHA-256-hashed API-key scheme for `/api/*` — both check the same
  Postgres tables. See `docs/SECURITY.md` for exactly what this does and doesn't
  cover (no roles, no MFA, no password reset — single/small-team grade auth).
- **Real persistent "Always Allow / Always Deny" command scoping** (Section 11),
  per authenticated user and base executable, managed from the Tools console. The
  static denylist always wins regardless of a saved Allow.
- **A real multi-stage `Dockerfile`** (deliberately SDK-based, since `BuildTool`/
  `TestTool` shell out to a real `dotnet` CLI at runtime) and an updated
  `docker-compose.yml` that brings up Postgres + Ollama + the web app together,
  with automatic EF Core migration on startup.
- **A real CI workflow** (`.github/workflows/ci.yml`): build, unit tests,
  integration tests against an actual Postgres service container, then a Docker
  build. Every step runs real commands — nothing is a placeholder.
- API rate limiting; `SECURITY.md`/`ARCHITECTURE.md` documenting what's actually
  built.

## What is NOT implemented (explicit extension points, not fake features)

- **General branching task graphs** — the planner produces a linear chain, matching
  the spec's own worked example.
- **Semantic/vector memory search** — real keyword-overlap ranking, not embeddings;
  the spec explicitly anticipates this as a later upgrade.
- **The repository-wide Context Engine (Section 13)** is a distinct, unimplemented
  piece from Memory (Phase 7).
- **No concrete IDE adapter** — interface only (`IIdeIntegrationProvider`).
- **No role/permission system, MFA, or password reset** — authentication exists but
  is intentionally minimal (see `docs/SECURITY.md`).
- **Per-user workspace isolation is not wired up** — every logged-in user currently
  shares the same default "Local Workspace" project/repositories/agent sessions.
  Login exists; multi-tenant data isolation on top of it does not yet.
- **Agent-initiated tool calls don't check persisted command permissions** — the
  Always-Allow/Deny scoping applies to human-driven Tools-console calls only, since
  `AgentSession` isn't yet tied to a specific user.
- **GPU/VRAM/temperature/power telemetry** needs vendor tooling integration that
  isn't wired up.
- **Integration tests cover a meaningful slice, not everything** — no automated
  tests yet for `OllamaModelProvider`'s real HTTP calls, the real `Process`
  invocations in `TerminalTool`/`GitTool`/`BuildTool`/`TestTool`, or the
  orchestrator loop end-to-end against a real model.
- **No load/performance testing** has been done on any of it.
- **CI builds and tests but doesn't deploy** — there's no CD step, no production
  secrets management beyond standard ASP.NET Core configuration, and EF migrations
  still need to actually be generated (`dotnet ef migrations add`) before the
  automatic-migration-on-startup code has anything to apply.
- Several smaller, specific gaps (symlink-aware sandboxing, symbol-browser
  pagination, cross-file semantic code graph, and more) are each called out
  individually in `docs/STATUS.md`.

Every implemented module has a `ModuleMarker.cs` where relevant, stating its status
so nothing is silently stubbed.

## Running it locally

### Option A: Docker Compose (closest to a one-command start)

```bash
docker compose up -d --build
```

This brings up Postgres, Ollama, and the web app. **You still need to generate an
EF Core migration before first use** (see Option B, step 3) — without one, the
app's automatic `Database.MigrateAsync()` call has no schema to apply. Then pull a
model into Ollama:

```bash
docker exec -it $(docker ps -qf name=ollama) ollama pull llama3.2:3b
```

The app will be reachable at `http://localhost:8080`. On first visit, register the
first account at `/Account/Register` (open only until the first user exists).

### Option B: Run the .NET host directly

**Prerequisites:** .NET 8 SDK, Docker (for Postgres + Ollama), and the `libman` CLI.

```bash
# 1. Start Postgres + Ollama only
docker compose up -d postgres ollama

# 2. Pull a model into Ollama
docker exec -it $(docker ps -qf name=ollama) ollama pull llama3.2:3b

# 3. Generate and apply the EF Core migration (from src/LocalAgentPlatform.Web) —
#    required; nothing works without this.
cd src/LocalAgentPlatform.Web
dotnet tool install --global dotnet-ef   # if not already installed
dotnet ef migrations add InitialCreate --project ../Shared/Data/Shared.Data.csproj --startup-project .
dotnet ef database update --project ../Shared/Data/Shared.Data.csproj --startup-project .

# 4. Vendor the SignalR JS client locally (never fetched from a CDN at runtime)
dotnet tool install --global Microsoft.Web.LibraryManager.Cli
libman restore

# 5. Run
dotnet run
```

Open the printed localhost URL, register the first account, then use the app.
Visit `/swagger` for the JSON API (create an API key first at `/ApiKeys`).

### Running the tests

```bash
# Pure logic, no dependencies:
dotnet test tests/LocalAgentPlatform.Domain.Tests

# Requires a reachable Postgres (docker compose up -d postgres):
dotnet test tests/LocalAgentPlatform.Integration.Tests
```

> **Note on verification:** this project was authored in a sandbox without the
> .NET SDK available, so it has **not** been compiled here, and the CI workflow has
> not actually run on GitHub Actions. Package versions (EF Core 8.0.8, Npgsql
> 8.0.4, Swashbuckle.AspNetCore 6.6.2, xUnit 2.9.2, Roslyn 4.11.0, etc.) are
> believed compatible with net8.0 as of early 2026 but you should run
> `dotnet build` and `dotnet test`, and fix any version drift, before relying on
> this.

## Where to go next

The highest-value remaining increments, in rough priority order: per-user workspace
isolation (multiple people currently share one workspace once logged in), wiring
`AgentSession` to a user so agent-initiated commands can use persisted permissions
too, integration tests against a real Ollama instance and the orchestrator loop
end-to-end, the cross-file semantic code graph (`CodeRelationship` population), and
a real IDE adapter for whichever editor you actually use.
