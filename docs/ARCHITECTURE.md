# Architecture

## Shape

This is a modular monolith (spec Section 4): one ASP.NET Core MVC host
(`LocalAgentPlatform.Web`) referencing a set of independently-project-filed modules
under `src/Modules/`, each split into `Domain` / `Application` / `Infrastructure` /
`Presentation` where that module has reached that far. `Presentation` folders are
largely still empty placeholders — this codebase puts controllers/views in the Web
host itself rather than per-module Razor Class Libraries, a pragmatic shortcut
documented here rather than hidden.

```
User → Web (MVC controllers + Razor views + /api/* JSON controllers)
          → Application services (per module)
              → Domain (pure logic, no I/O — the only layer with real unit tests)
              → Infrastructure (real I/O: EF Core, Process, HTTP, file system)
          → Shared.Data (PlatformDbContext, PostgreSQL)
          → Shared.Kernel (cross-module abstractions: IModelProvider, ITool,
                            IHardwareTelemetryProvider, IAgentEventBroadcaster,
                            IBackgroundTaskQueue)
```

## Module map (what's real vs. placeholder)

| Module | Real | Notes |
|---|---|---|
| Models | Yes | Ollama-backed `IModelProvider`, registry, hardware-aware recommendation |
| RepositoryAnalysis | Yes | Real scanning/hashing/Roslyn symbol extraction |
| Tools | Yes | 8 real tools + `CommandPolicyEngine` |
| Agent | Yes | Real plan → execute → verify → repair loop |
| Verification | Yes | Real build/test/security pipeline + advisory reviewer |
| Memory | Yes | Real retrieval-based memory (keyword-overlap ranking) |
| IdeIntegration | Interface only | See docs/STATUS.md — no concrete adapter |
| Projects | Placeholder | Entities live in Shared.Data; no separate domain logic yet |
| Execution | Placeholder | Reserved for a future distinct agent-runtime state machine |

## Why a linear task chain, not a general DAG

The Agent Engine's planner produces an ordered list of steps, each pointing at the
previous one via `ParentId`. The master spec's own worked example (Section 9) is
also linear. Building a general branching/parallel task graph — with the scheduling,
partial-failure, and merge semantics that implies — is a meaningfully larger effort
than this slice covers, and is called out explicitly rather than silently narrowed.

## Why Postgres for everything, including "memory"

The spec explicitly permits keyword-based retrieval today with a note that
Postgres-compatible vector storage can be added later (Section 14). Rather than pull
in a vector database or an embeddings API (which would also violate the "no
mandatory cloud AI" constraint unless a local embeddings model were added), memory
retrieval uses a deterministic keyword-overlap ranker. It's the honest, correctly-
scoped version of that requirement, not a permanent design decision — the schema
doesn't preclude adding a `vector` column via `pgvector` later.

## Background work and real-time updates

Long-running work (repository indexing, agent sessions) runs on a single
`System.Threading.Channels`-backed queue drained by one `BackgroundService`
(`QueuedHostedService`). This is intentionally simple — one worker, FIFO, in-memory —
and does not survive a process restart (an in-flight job is simply gone; see
docs/STATUS.md). Real-time UI updates go through a single SignalR hub
(`AgentTelemetryHub`) with two event types (`AgentSessionUpdated`,
`AgentTaskUpdated`) plus a periodic hardware-telemetry broadcast. There's no
per-event granularity below "something about this session changed" — the client
reacts by reloading the page rather than patching a specific DOM node.

## Where the model is and isn't used

Per Section 68 ("use deterministic software whenever possible"), the only two places
that call `IModelProvider` are: (1) `AgentPlanningService`, for turning a natural-
language request into a structured plan, and (2) `ReviewerService`, for an advisory
verdict on verification results. Everything else — file I/O, terminal execution,
git, build, test, hashing, symbol extraction, security scanning, memory ranking — is
deterministic code with no model call in the loop.

## Authentication layering

Cookie auth (MVC UI) and API-key auth (`/api/*`) are two separate ASP.NET Core
authentication schemes registered side by side, each with its own controllers
opting into the right one — `[Authorize(AuthenticationSchemes = "ApiKey")]` on API
controllers, plain `[Authorize]` (cookie, the default scheme) everywhere else, with
`[AllowAnonymous]` only on `AccountController`. Both schemes ultimately check the
same Postgres tables (`AppUser`, `ApiKey`) — there's a single source of truth for
"who is this," just two different credentials for reaching it (a browser session vs.
a bearer-style header for programmatic/IDE-adjacent access).

## Testing layers

Two separate test projects reflect a real distinction, not an arbitrary split:
`tests/LocalAgentPlatform.Domain.Tests` covers every project named `*.Domain` — pure
logic, zero I/O, runs anywhere in milliseconds. `tests/LocalAgentPlatform.Integration.Tests`
exercises real Application/Infrastructure code (EF Core against an actual Postgres
instance, real temp-directory filesystem scans, real Roslyn parsing) and is
deliberately allowed to fail if its real dependency (Postgres) isn't reachable —
that's what makes it a genuine integration test rather than a disguised unit test.
CI (`.github/workflows/ci.yml`) provides a real `postgres:16` service container for
this; running it locally requires `docker compose up -d postgres` first.

## Deployment

`Dockerfile` builds and runs the Web app on the .NET 8 **SDK** image (not the
smaller ASP.NET runtime image) because this platform's own `BuildTool`/`TestTool`
shell out to a real `dotnet` CLI against whatever repository is mounted into
`/workspace` — the app needs the SDK present at runtime, not just to compile itself.
`docker-compose.yml` wires Postgres, Ollama, and the web app together for a
single-command local (not production-hardened) deployment; EF Core migrations run
automatically on startup via `Database.MigrateAsync()`.
