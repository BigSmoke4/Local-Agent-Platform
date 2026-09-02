# Architecture

This document describes the system as it actually exists in this repository —
not the aspirational end-state. Where something described in the original
spec isn't built, that's stated explicitly rather than implied.

## Current shape: single well-structured project, not a module split

The spec calls for a modular monolith with ~13 separate module projects
(`/Modules/Agent`, `/Modules/Models`, etc., each with Domain/Application/
Infrastructure/Presentation). What exists today is **one ASP.NET Core MVC
project** (`src/Web`) with folders organized by *concern* rather than by
*module project*:

```
src/Web/
  Controllers/                 Thin MVC + API controllers
  Models/                      EF Core entities + view models
  Data/                        PlatformDbContext, design-time factory
  Services/
    IModelProvider.cs          Model runtime abstraction
    OllamaModelProvider.cs     Concrete local adapter
    Tools/                     CalculatorTool, FileReadTool, FileWriteTool,
                                TerminalTool, GitTool, BuildTool, TestTool,
                                DiffTool, ProjectStructureTool,
                                SearchSymbolTool, DependencyAnalysisTool,
                                CommandPolicyEngine, SafeFileEditService
    Verification/               VerificationEngine, ReviewerService
    CodeIntelligence/           RoslynSyntaxIndexer, RepositoryIndexService,
                                BuildDiagnosticParser
    Memory/                     MemoryService
    Routing/                    ModelRouter
    Autonomy/                   AutonomyService
    Telemetry/                  HardwareTelemetryProvider
  Hubs/                        AgentHub (SignalR)
  BackgroundServices/          HardwareTelemetryBackgroundService
  Views/, wwwroot/             Razor + centralized CSS/JS
```

**Why not split into module projects now**: with this few modules and this
little cross-module logic, separate projects would mean empty
Domain/Application/Infrastructure/Presentation folders in most of them —
scaffolding for its own sake. The `Services/<Concern>/` folders above are
real namespace boundaries already (`Platform.Web.Services.Tools`,
`Platform.Web.Services.Verification`, etc.) and are structured so that
splitting each into its own `.csproj` later is a mechanical move — extract
folder, add project file, fix references — not a redesign.

## Request flow (as implemented)

```
Browser / curl
  |
  v
ASP.NET Core MVC / API controller (thin — orchestration only)
  |
  +--> IModelProvider (OllamaModelProvider) --> local Ollama HTTP API
  |
  +--> Tools (Services/Tools/*) --> real process execution / file I/O / git
  |
  +--> PlatformDbContext (EF Core) --> PostgreSQL
  |
  +--> IAgentEventBroadcaster --> AgentHub (SignalR) --> connected browsers
```

There is no separate "Agent Orchestrator" service layer distinct from the
controllers yet — `AgentController` and `AgentVerificationController`
currently *are* the orchestration layer. Extracting that into a dedicated
`AgentOrchestrator` application service is a reasonable next refactor once
there's a second caller of the same logic (e.g. an IDE adapter) that would
otherwise duplicate it.

## Data flow for the verification/repair loop

```
POST /api/agent/run-verified
  |
  v
VerificationEngine.RunAsync
  |  BuildTool.RunAsync --> real `dotnet build`, parses real output
  |  (if build fails) --> BuildDiagnosticParser finds real failing file
  |                    --> AutonomyService gate (Low = skip repair)
  |                    --> SafeFileEditService.ApplyAsync
  |                         (FileReadTool + model call + FileWriteTool
  |                          + DiffTool + FileSnapshot persisted)
  |                    --> retry, up to MaxIterations = 3
  |  TestTool.RunAsync --> real `dotnet test`, parses real summary line
  v
ReviewerService.ReviewAsync --> second real model call, structured verdict
  |
  v
AgentSession persisted with final State; AgentTaskNode rows record each stage
```

Every arrow above is a real call — no stage is simulated or hardcoded to
succeed.

## What's genuinely NOT built

See the "What is intentionally NOT implemented yet" section of `README.md`
for the current, authoritative list — it's kept in sync with the code as
new pieces land rather than duplicated here (two lists drift; one doesn't).
