# Testing

## What exists

`tests/Platform.Tests` — xUnit, real assertions against real behavior
(not smoke tests that just check "doesn't throw"):

| Test file | Asserts |
|---|---|
| `CommandPolicyEngineTests` | Deny/Allow/RequireApproval branches all fire correctly for real command strings |
| `CalculatorToolTests` | Real arithmetic results |
| `DependencyAnalysisToolTests` | Real `.csproj` XML parsing produces correct package list |
| `DiffToolTests` | Real added/removed line detection via DiffPlex |
| `FileWriteToolTests` | Conflict detection actually throws on stale hash; path traversal actually blocked; new-file and update-with-correct-hash paths actually succeed |
| `BuildDiagnosticParserTests` | Real `dotnet build` diagnostic line format is parsed correctly, including distinguishing error vs. warning |
| `RoslynSyntaxIndexerTests` | Real AST parsing finds real classes/methods/properties/interfaces with correct line numbers |
| `ModelRouterTests` | Classification heuristic produces the expected tier for arithmetic/simple/complex inputs |
| `MemoryServiceTests` | Retrieval actually ranks relevant memories above irrelevant ones; zero-overlap queries return empty; retrieval count increments; embedding-based cosine scoring path exercised via a deterministic stub |
| `PlannerServiceTests` | Real JSON plan parsing, markdown-fence stripping, unknown-step-type filtering, honest failure on unparseable output |
| `SemanticRepairTargetResolverTests` | Honest fallback to compiler file list when no semantic workspace is loaded |
| `LspFramingTests` | Real Content-Length wire framing, including multi-byte UTF-8 correctness and message round-tripping |

Run with:

```bash
dotnet test
```

## Real PostgreSQL integration tests (Testcontainers)

`tests/Platform.Tests/Integration/PostgresIntegrationTests.cs` runs against
a real, ephemeral PostgreSQL container via Testcontainers — not the
InMemory provider. This actually catches things InMemory can't:

- Unique constraint violations (`ModelDescriptor.RuntimeId`,
  `AutonomySetting.UserId`) — InMemory doesn't enforce unique indexes at all
- Cascade-delete behavior (`AgentSession` → `ToolExecution`) — real FK
  behavior, not assumed
- Runs real `Database.MigrateAsync()` against a real database, which is
  the actual thing that needs to work in production

**Requires Docker** on the machine running `dotnet test`. This was not run
in the sandbox that generated this code (no Docker access there — see the
top-level README). If Docker isn't available, `InitializeAsync` throws a
real, visible error rather than silently skipping and reporting a false
green — you'll know immediately if these didn't actually run.

## What's still NOT covered

- **`TerminalTool`/`GitTool`/`BuildTool`/`TestTool` integration tests** —
  these spawn real processes; testing them meaningfully needs a controlled
  workspace fixture with a real tiny `.csproj` to build/test against, which
  isn't set up here
- **End-to-end tests** (user request → agent → repository → modification →
  build → test → verification → result) as a single automated flow
- **Adversarial tests** for path traversal via the API layer (unit-level
  path traversal is tested; an end-to-end HTTP-level test isn't)
- **SignalR hub tests**
- **Load/performance tests**

Building real PostgreSQL-backed integration tests (e.g. via Testcontainers)
is the natural next step for this file — noted here rather than silently
left out.
