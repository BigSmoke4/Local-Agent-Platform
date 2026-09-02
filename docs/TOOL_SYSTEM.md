# Tool System

All tools live under `src/Web/Services/Tools/`. None of them are behind a
shared `ITool` interface yet (the spec's §10 `ITool` abstraction with
Name/Description/InputSchema/OutputSchema/Permission/RiskLevel/Timeout is
not built) — each tool is currently a concrete class with its own method
signature, called directly by controllers. That's a real gap: adding
`ITool` and a registry would be needed before an LLM-driven planner could
dynamically choose tools by reflection rather than the controller code
calling them by name.

## What each tool actually does

| Tool | Real behavior |
|---|---|
| `CalculatorTool` | `DataTable.Compute` — real arithmetic evaluation, deterministic |
| `FileReadTool` | Reads a file inside `Workspace:Root`; blocks path traversal |
| `FileWriteTool` | Writes inside `Workspace:Root`; hash-conflict-checked |
| `TerminalTool` | Spawns a real process via `Process.Start`, policy-gated, 5-minute timeout, tree-kill on timeout |
| `GitTool` | Wraps real `git` CLI calls (status/diff/log/branch/checkpoint commit) through TerminalTool |
| `BuildTool` | Runs real `dotnet build`, regex-parses the actual `X Error(s)` / `Y Warning(s)` summary |
| `TestTool` | Runs real `dotnet test`, regex-parses the actual `Failed: X, Passed: Y, Skipped: Z` summary line |
| `DiffTool` | Real line diff via DiffPlex |
| `ProjectStructureTool` | Real recursive directory scan, ignores `.git`/`bin`/`obj`/etc. |
| `SearchSymbolTool` | Real regex-based text search over `.cs` files for declaration-shaped lines. **Not semantic** — see CODE_INTELLIGENCE.md |
| `DependencyAnalysisTool` | Real XML parsing of `.csproj` `PackageReference` elements |
| `CommandPolicyEngine` | Deny/allow/require-approval decision logic used by `TerminalTool` |
| `SafeFileEditService` | Orchestrates FileRead+FileWrite+Diff+FileSnapshot for a single safe edit, with rollback |

## Missing from the spec's tool list

- `PackageManagerTool`, `DatabaseTool`, `DockerTool`, `BrowserTool` — none built
- No formal `ITool` interface/registry (see above)
- No per-tool declared risk level or timeout configuration beyond
  `TerminalTool`'s hardcoded 5-minute timeout
