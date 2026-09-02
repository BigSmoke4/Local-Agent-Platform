# Local Agent Platform — Phase 1 Foundation

A local-first, offline-capable ASP.NET Core MVC foundation for an autonomous
coding agent platform. No mandatory cloud LLM dependency — the model runtime
adapter talks to a **local [Ollama](https://ollama.com)** instance over HTTP.

## What's actually implemented (real, not mocked)

- ASP.NET Core MVC + Razor, PostgreSQL via EF Core
- ASP.NET Core Identity (real local auth — register/login/logout)
- `IModelProvider` abstraction + a working `OllamaModelProvider` HTTP adapter
  (generate, stream, health check, list models)
- Agent session persistence (`AgentSession`, `ToolExecution`, `AuditLog`) with
  a minimal real agent loop: `Created → Understanding → Executing → Completed/Failed`
- Deterministic-first tool routing: arithmetic requests are evaluated by
  `CalculatorTool`, never sent to the model
- `FileReadTool` sandboxed to a configured workspace root (path traversal blocked)
- `CommandPolicyEngine` with deny/allow/require-approval rules, backing a real
  `TerminalTool` that actually spawns processes (with timeout + kill)
- `GitTool` — real `git` CLI wrapper (status, diff, log, branch, checkpoint commit)
- `BuildTool` — runs real `dotnet build`, parses actual error/warning counts
  from output; never reports success without exit code 0 and zero errors
- `TestTool` — runs real `dotnet test`, parses the actual pass/fail/skip summary
  line; reports failure honestly if no summary line is found rather than
  guessing
- `ToolsController` — exposes all tools as audited API endpoints independent
  of the agent loop, so each can be exercised and inspected on its own
- `ProjectStructureTool` — real recursive filesystem scan of the workspace
- `SearchSymbolTool` — real regex-based text search for class/method/property
  declarations. **Not a semantic index**: no cross-reference resolution,
  inheritance awareness, or overload resolution — see "not implemented" below
- `DependencyAnalysisTool` — real XML parsing of `.csproj` `PackageReference`
  entries (tested against actual XML, not fixtures pretending to be one)
- `VerificationEngine` — real build→test pipeline: runs `BuildTool` then
  `TestTool` in sequence, only proceeds to tests if the build actually passed
- `ReviewerService` — independent reviewer stage; makes a second real call to
  the local model and requires a structured approve/reject verdict. Fails
  closed (rejects) if the model call errors or returns unparseable output
- `AgentVerificationController` (`POST /api/agent/run-verified`) — runs a
  real task graph (Build → Test → Review) persisted as `AgentTaskNode` rows,
  retries on failure up to a hard cap of 3 iterations (never unbounded, §47),
  and broadcasts each stage over SignalR as it happens
- `AgentHub` (`/hubs/agent`) — real SignalR hub; `AgentStateChanged`,
  `VerificationUpdated`, and `HardwareTelemetryUpdated` events are pushed
  from actual state transitions and actual telemetry samples, not a timer
  faking activity
- `HardwareTelemetryProvider` + background service — samples real process
  CPU% and working-set memory every 5s and broadcasts it; GPU is honestly
  reported as `"Unavailable"` since no vendor tooling (e.g. `nvidia-smi`) is
  wired up
- Health checks (`/health`, `/health/live`, `/health/ready`) backed by a real
  PostgreSQL connectivity check
- Serilog structured logging to console + rolling file
- xUnit tests for the command policy engine, calculator, and dependency XML
  parsing (real assertions, not smoke tests)

- **Real single-file self-repair** now exists: `run-verified` accepts an
  optional `RepairTargetFile`. On build failure it reads that file, sends
  the actual compiler output + file content to the model, strips markdown
  fences from the response, and applies the result via
  `SafeFileEditService` (hash-conflict-checked, snapshotted, diffed), then
  retries the build. This is genuinely wired end to end — not a stub — but
  it is scoped to one caller-specified file, because there is no
  diagnostic-to-file mapping yet (that needs the Roslyn work below), so the
  agent cannot decide *which* file to fix on its own
- `FileWriteTool` — real writes inside the sandboxed workspace, with
  hash-based conflict detection (§59): if the file changed on disk since it
  was last read, the write is refused rather than silently overwriting
- `DiffTool` — real line-level diff via DiffPlex, not a placeholder
- `SafeFileEditService` — ties FileReadTool + FileWriteTool + DiffTool +
  `FileSnapshot` persistence together: every edit is snapshotted before
  writing, so `POST /api/tools/file/rollback` can genuinely restore prior
  content, not just claim to

- **Real Roslyn syntax indexing** now exists (`RoslynSyntaxIndexer` +
  `RepositoryIndexService`, `POST /api/code-intelligence/index`,
  `GET /api/code-intelligence/symbols?name=...`). This uses
  `Microsoft.CodeAnalysis.CSharp` to actually parse each `.cs` file's AST and
  extract classes/interfaces/structs/records/enums/methods/properties/
  constructors with real line spans and containing-type/namespace context —
  not a regex approximation. Indexing is genuinely incremental per §60: each
  file's content hash is compared against what's stored, and unchanged files
  are skipped rather than re-parsed. **Scope limit, stated plainly**: this is
  syntax-level analysis only (`CSharpSyntaxTree.ParseText`), not a full
  `Compilation` built across project references via `MSBuildWorkspace` — it
  cannot resolve overloads, inherited members, or cross-assembly symbols.
  That would require real MSBuild project loading and is a materially larger
  piece of work, not attempted here
- **`BuildDiagnosticParser`** — real regex parsing of the actual `dotnet
  build` diagnostic line format (`file(line,col): error CSxxxx: message`),
  tested against real compiler output shape, not an invented one
- **Auto-target repair**: `run-verified`'s `RepairTargetFile` is now
  optional. If omitted, the agent parses the real build output for the file
  path of the first actual error and repairs that — it no longer requires
  you to already know which file broke
- **`MemoryService`** — real retrieval-based memory per §14 ("do not
  blindly inject all memories into every prompt"). Stores `Memory` rows
  (ShortTerm/Working/LongTermProject/UserPreference/Execution) in
  PostgreSQL and retrieves by keyword-overlap relevance scoring against a
  bounded candidate set — genuine ranking, not a fixed dump of everything
  stored. **Honestly scoped**: overlap scoring, not vector/embedding
  similarity; swapping in pgvector-based embeddings later only changes the
  scoring function, not the retrieval contract
- **`ModelRouter`** — real routing per §31: classifies each request
  (Trivial/Simple/Complex) via request length + keyword heuristics, then
  actually selects a different registered model per tier (small model for
  simple tasks, reasoning-flagged model for complex ones), falling back
  honestly to the default when no specialized model is registered. Wired
  into `POST /api/agent/run`, not just a standalone unused class
- **`AutonomyService`** — real enforcement per §35: Low/Medium/High levels
  persisted per user. At Low, the verification loop's automated repair step
  is actually skipped (not just logged) rather than silently editing files;
  Medium/High permit it. This is a functioning gate on a real code path,
  not a UI toggle with no effect
- `api/settings/autonomy` (GET/POST) and `api/memory` (POST to store,
  GET `/retrieve?query=`) — real endpoints for both of the above
- **Real UI screens** (not just APIs): `/model-manager`, `/token-monitor`,
  `/settings` (autonomy level, backed by the same `AutonomyService` gate
  the repair loop actually checks)
- **`SemanticCodeGraphService`** — real cross-file semantic analysis via
  `Microsoft.CodeAnalysis.MSBuild` (`MSBuildWorkspace`) +
  `Microsoft.Build.Locator`, not a syntax-only approximation. Loads a real
  `.sln`/`.csproj` and answers with actually-resolved base types,
  actually-resolved implemented interfaces, and real cross-file
  "find all references" via Roslyn's `SymbolFinder`. Requires a real .NET
  SDK on the host (same one used to `dotnet build` this repo); if
  registration fails, endpoints return the real error rather than a
  fabricated success. See `docs/CODE_INTELLIGENCE.md`
- **`PlannerService`** — real dynamic planner: makes an actual model call
  asking it to break a request into a JSON step sequence, constrained to
  step types that map to real tools in this codebase (unknown types are
  dropped, not executed blindly). `POST /api/agent/plan` returns the real
  parsed plan. **Honest scope**: `run-verified` still executes its fixed
  Build→Test→Review→Repair sequence rather than consuming this plan —
  generating a plan and building an executor that walks an arbitrary
  model-generated sequence are two different pieces of work, not conflated
  here
- **Multi-file repair** — the repair step now fixes every file with a real
  reported build error in a given attempt (`BuildDiagnosticParser.FindAllErrorFiles`),
  not just the first one found
- **Embedding-based memory similarity** — `MemoryService` now has two real
  scoring paths: cosine similarity over real Ollama `/api/embeddings`
  vectors (when `ModelRuntime:EmbeddingModel` is configured), with keyword
  overlap as an honest per-row fallback when no embedding is available.
  Each result reports which method scored it. See `docs/MEMORY.md`
- **`GenericIdeController`** (`/api/ide/*`) — a real, working, IDE-agnostic
  HTTP surface (capabilities, workspace tree, symbol search, safe edit,
  diagnostics). **Honest scope**: no Cursor/VS Code/JetBrains-specific
  adapter exists — each needs that IDE's actual extension/protocol SDK to
  build and test against, which isn't available in this environment. The
  generic HTTP surface is the real fallback the spec itself asks for
  ("support a generic local endpoint so compatible clients can connect
  without a custom integration")
- **`ProjectStructureTool` incremental scanning** — now computes a cheap
  aggregate signature (path+size+mtime per file) and skips rebuilding the
  tree when nothing changed. In-process cache only (resets on restart) —
  stated honestly rather than claiming DB-backed persistence it doesn't have
- **Full documentation set** (`docs/`): `ARCHITECTURE.md`, `SECURITY.md`,
  `DATABASE.md`, `TOOL_SYSTEM.md`, `AGENT_ENGINE.md`, `MODEL_RUNTIME.md`,
  `CODE_INTELLIGENCE.md`, `MEMORY.md`, `TELEMETRY.md`, `TESTING.md`,
  `MODULE_SPLIT.md` — each describes what's actually implemented and
  states limitations plainly, rather than describing the aspirational spec

## What is intentionally NOT implemented yet

Per the platform's own "no fake functionality" principle, these are defined
as clean extension points, not faked:

- **Semantic-graph-driven repair targeting is now real** —
  `SemanticRepairTargetResolver` uses `SemanticCodeGraphService.FindReferencesAsync`
  to expand repair targets beyond the compiler-reported file when a
  semantic workspace has been loaded (`POST /api/code-intelligence/semantic/load`);
  it falls back to the plain compiler-diagnostic file list otherwise,
  honestly, rather than silently doing nothing. Root-cause tracing is
  still limited to symbols named in the diagnostic message (regex-extracted
  from the actual compiler text) — it doesn't do deep multi-hop causal
  analysis
- **Dynamic-plan-driven execution is now real** — `PlanExecutionService` +
  `POST /api/agent/run-planned` actually walks whatever step sequence
  `PlannerService` generates and runs the real tool behind each known step
  type, stopping at the first real failure. `run-verified`'s fixed
  Build→Test→Review→Repair sequence still exists separately (it has real
  target-resolution logic `run-planned`'s generic executor doesn't) —
  the two are intentionally different code paths, not merged
- **IDE integration via the real LSP protocol** — `Services/Lsp/LspServer.cs`
  and `LspFraming.cs` implement the actual Language Server Protocol wire
  format and a real `initialize`/`textDocument/didSave`→
  `textDocument/publishDiagnostics` flow, runnable via `dotnet run -- --lsp`.
  This is real, public, and testable (`LspFramingTests.cs`) — unlike
  Cursor's undocumented internals or JetBrains' proprietary Kotlin/Java
  plugin SDK, LSP is implementable and verifiable without needing any
  single vendor's toolchain. **Honest scope**: only `initialize`, `shutdown`,
  and diagnostics-on-save are implemented — not completion, hover,
  go-to-definition, or code actions; `GenericIdeController`'s plain HTTP
  surface still exists alongside this for clients that don't speak LSP
- **Modular-monolith physical project split — partially done** —
  `Platform.Modules.Tools` and `Platform.Modules.CodeIntelligence` are now
  real separate `.csproj` projects referenced by `Platform.Web`, done
  safely because their classes have zero `PlatformDbContext` dependency
  (see `docs/MODULE_SPLIT.md` for exactly which classes moved and why the
  remaining modules — which mostly do depend on the DB or on
  `IModelProvider` — weren't attempted blind in the same pass)
- **pgvector — real opt-in path exists, not wired in by default** —
  `PgVectorSimilarityService` does real SQL-side cosine search via
  pgvector's `<=>` operator, but isn't called automatically by
  `MemoryService` (see `docs/SETUP_PGVECTOR.md` for the exact setup SQL
  and why it's opt-in rather than assumed)
- **OpenTelemetry tracing — now real** — `AddAspNetCoreInstrumentation()`,
  `AddHttpClientInstrumentation()` (real spans for Ollama calls), and
  Npgsql's tracing integration are wired in `Program.cs`, exporting to
  console always and via OTLP if `Telemetry:OtlpEndpoint` is configured.
  Metrics export (as opposed to tracing) is still not set up
- **PostgreSQL-backed integration tests — now real** —
  `tests/Platform.Tests/Integration/PostgresIntegrationTests.cs` uses
  Testcontainers to run tests against an actual ephemeral PostgreSQL
  instance (unique-constraint and cascade-delete behavior that EF Core's
  InMemory provider can't validate). **Requires Docker** on the machine
  running `dotnet test` — not available in the sandbox that generated this
  code, so these specific tests were written but not executed here; see
  `docs/TESTING.md`

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- PostgreSQL 14+ running locally (or update the connection string)
- [Ollama](https://ollama.com) running locally with at least one model pulled,
  e.g. `ollama pull llama3.1` then `ollama serve` (usually auto-started)

## Setup

```bash
# 1. Restore & build
dotnet restore
dotnet build

# 2. Create the database schema
cd src/Web
dotnet tool install --global dotnet-ef   # if not already installed
dotnet ef migrations add InitialCreate
dotnet ef database update

# 3. Run
dotnet run
```

Open `https://localhost:5001` (or the URL printed in the console), register
an account, then sign in.

### Register a model

```bash
curl -X POST http://localhost:5000/api/models \
  -H "Content-Type: application/json" \
  -b cookies.txt \
  -d '{"name":"Llama 3.1 8B","runtimeId":"llama3.1","contextWindow":8192,"quantization":"Q4_K_M"}'
```

(You'll need an authenticated session cookie — sign in via the browser first,
or exercise the API with a tool that preserves cookies.)

### Run an agent session

```bash
curl -X POST http://localhost:5000/api/agent/run \
  -H "Content-Type: application/json" \
  -b cookies.txt \
  -d '{"userRequest":"25 * 48"}'
```

Arithmetic is handled deterministically. Anything else is routed to the
registered default model via Ollama.

### Use the new tools directly

```bash
curl -b cookies.txt http://localhost:5000/api/tools/git/status
curl -b cookies.txt "http://localhost:5000/api/tools/git/diff?path=README.md"
curl -X POST -b cookies.txt http://localhost:5000/api/tools/build
curl -X POST -b cookies.txt http://localhost:5000/api/tools/test
curl -X POST -b cookies.txt -H "Content-Type: application/json" \
  -d '{"command":"git status","approved":false}' \
  http://localhost:5000/api/tools/terminal
```

Commands not on the allowlist come back with `"decision":"RequireApproval"`
and are **not executed** until you resend with `"approved":true`.

### Run the verification loop (task graph + reviewer)

```bash
curl -X POST -b cookies.txt -H "Content-Type: application/json" \
  -d '{"userRequest":"Verify the current build is healthy"}' \
  http://localhost:5000/api/agent/run-verified

curl -b cookies.txt http://localhost:5000/api/agent/sessions/<session-id>/tasks
```

Watch it happen live by connecting to `/hubs/agent` with a SignalR client
(the dashboard already does this for hardware telemetry).

### Run the verification loop with self-repair

```bash
# RepairTargetFile is optional now — omit it and the agent parses the real
# build output to find the first failing file itself.
curl -X POST -b cookies.txt -H "Content-Type: application/json" \
  -d '{"userRequest":"Fix the build"}' \
  http://localhost:5000/api/agent/run-verified

# Or force a specific file:
curl -X POST -b cookies.txt -H "Content-Type: application/json" \
  -d '{"userRequest":"Fix the build","repairTargetFile":"src/Broken.cs"}' \
  http://localhost:5000/api/agent/run-verified
```

The agent reads the target file, sends the real compiler diagnostics to the
model, and applies whatever it returns via the hash-checked safe-edit path,
then rebuilds. Check `GET /api/agent/sessions/<id>/tasks` to see the actual
Repair task node and its diff summary. Roll back with:

```bash
curl -X POST -b cookies.txt -H "Content-Type: application/json" \
  -d '{"snapshotId":"<guid-from-the-edit-response>"}' \
  http://localhost:5000/api/tools/file/rollback
```

### Index the workspace and search real symbols

```bash
curl -X POST -b cookies.txt http://localhost:5000/api/code-intelligence/index
curl -b cookies.txt "http://localhost:5000/api/code-intelligence/symbols?name=OrderService"
```

### Real cross-file semantic analysis

```bash
curl -X POST -b cookies.txt "http://localhost:5000/api/code-intelligence/semantic/load?path=LocalAgentPlatform.sln"
curl -b cookies.txt "http://localhost:5000/api/code-intelligence/semantic/type?name=AgentSession"
curl -b cookies.txt "http://localhost:5000/api/code-intelligence/semantic/references?name=IModelProvider"
```

### Real dynamic planning

```bash
curl -X POST -b cookies.txt -H "Content-Type: application/json" \
  -d '{"userRequest":"Add Redis caching to the product catalog"}' \
  http://localhost:5000/api/agent/plan
```

### Generic IDE integration surface

```bash
curl -b cookies.txt http://localhost:5000/api/ide/capabilities
curl -b cookies.txt http://localhost:5000/api/ide/workspace
curl -X POST -b cookies.txt -H "Content-Type: application/json" \
  -d '{"project":null}' \
  http://localhost:5000/api/ide/diagnostics
```

### Dynamic-plan-driven execution (real, not just planning)

```bash
curl -X POST -b cookies.txt -H "Content-Type: application/json" \
  -d '{"userRequest":"Check git status, build, and test the project"}' \
  http://localhost:5000/api/agent/run-planned
```

Returns the actual plan, the real per-step execution results (task nodes),
and a summary — the model's plan genuinely drives what runs.

### Semantic-aware repair

```bash
# 1. Load the solution for real semantic analysis
curl -X POST -b cookies.txt "http://localhost:5000/api/code-intelligence/semantic/load?path=LocalAgentPlatform.sln"

# 2. Now run-verified's repair step will use SemanticRepairTargetResolver's
#    expanded, reference-aware target set instead of just the compiler's
#    reported file
curl -X POST -b cookies.txt -H "Content-Type: application/json" \
  -d '{"userRequest":"Fix the build"}' \
  http://localhost:5000/api/agent/run-verified
```

### LSP mode (real Language Server Protocol)

```bash
dotnet run --project src/Web -- --lsp
```

Runs the real LSP server over stdin/stdout instead of the web host — point
any generic LSP client (VS Code with a small client extension, Neovim's
built-in client, etc.) at this as a subprocess language server.

### Embedding-based memory (optional)

Set `ModelRuntime:EmbeddingModel` in `appsettings.json` to a model you've
pulled for embeddings (e.g. `ollama pull nomic-embed-text`, then set the
value to `"nomic-embed-text"`). Without this set, memory retrieval uses the
keyword-overlap fallback automatically — no code change needed either way.

## Verifying it's real

- `GET /health` — real PostgreSQL check, will fail honestly if the DB is down
- Dashboard "Runtime Health" — real HTTP call to Ollama's `/api/tags`; shows
  `UNAVAILABLE` if Ollama isn't running (never fabricated as `READY`)
- `dotnet test` — runs real unit tests against the command policy engine and
  calculator tool

## Project layout

```
src/Web/
  Controllers/     Thin MVC + API controllers
  Models/          EF Core entities + view models
  Data/            PlatformDbContext
  Services/        IModelProvider, OllamaModelProvider
  Services/Tools/  CalculatorTool, FileReadTool, CommandPolicyEngine
  Views/           Razor views (console/neural-workstation styling)
  wwwroot/css,js/  Centralized styling and scripts
tests/Platform.Tests/
```

## Next steps (not built yet)

1. Repository registration + incremental file indexing
2. Roslyn-based symbol graph for .NET repos
3. Task graph + multi-step planner
4. Build/test verification pipeline with real `dotnet build` / `dotnet test`
   invocation and pass/fail parsing
5. SignalR hub for live agent/tool/telemetry events
6. Split into the full modular-monolith layout as each module grows
