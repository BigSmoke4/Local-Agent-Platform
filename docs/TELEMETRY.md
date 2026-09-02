# Telemetry

## Token telemetry

`AgentSession.InputTokens/OutputTokens/CachedTokens` are populated directly
from Ollama's real response fields (see MODEL_RUNTIME.md) — never
estimated. `CachedTokens` is currently always 0 because Ollama's
`/api/generate` response doesn't expose a cached-token count in the
version this was built against; the field exists on the entity for when a
runtime that reports it is used.

## Hardware telemetry

`HardwareTelemetryProvider` samples real values via .NET APIs:

- **CPU%**: computed from `Process.TotalProcessorTime` delta over wall-clock
  time delta, divided by `Environment.ProcessorCount` — a real, if
  approximate, per-process CPU utilization figure
- **Process memory**: `Process.WorkingSet64` — real
- **Total available memory**: `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`
  — real, when the runtime reports it
- **GPU**: honestly reported as `"Unavailable"`. No `nvidia-smi`/vendor
  tooling integration exists. This was a deliberate choice per §7 ("If a
  metric is unavailable... display Unavailable instead of fake data") —
  adding real GPU telemetry means shelling out to `nvidia-smi` (NVIDIA),
  `rocm-smi` (AMD), or platform-specific APIs, parsing genuinely different
  output formats per vendor, and handling the "no GPU present" case
  distinctly from "GPU present but tooling missing"

`HardwareTelemetryBackgroundService` samples every 5 seconds and broadcasts
over SignalR (`HardwareTelemetryUpdated` event) — a real `BackgroundService`
running off the request pipeline, not a client-side timer faking activity.

## SignalR events

`AgentHub` (`/hubs/agent`) is server-to-client only. Events are fired from
real state transitions in `AgentVerificationController` and the hardware
background service:

- `AgentStateChanged` — fired whenever `AgentSession.State` actually changes
- `VerificationUpdated` — fired after each real Build/Test/Review/Repair
  stage completes, carrying the actual outcome
- `HardwareTelemetryUpdated` — fired every 5s with a real sample
- `TokenUsageUpdated` — defined on `IAgentEventBroadcaster` but not yet
  called from any controller (token counts are currently only visible via
  the `AgentSession` API response, not pushed live)
- `ToolInvoked`/`ToolCompleted` — defined but not yet called from
  `ToolsController`; only the verification loop's internal tool calls are
  currently broadcast

## Not implemented

- Per-tool latency histograms
- Context window utilization tracking (no context-budget accounting exists
  yet — see README's context engine gap)

## OpenTelemetry (real, added)

`Program.cs` wires real `OpenTelemetry.Extensions.Hosting` tracing:
`AddAspNetCoreInstrumentation()` (real spans for every HTTP request),
`AddHttpClientInstrumentation()` (real spans for outbound calls to
Ollama), and Npgsql's own OpenTelemetry integration (real spans for
PostgreSQL queries). Always exports to console (`AddConsoleExporter()`, so
tracing is visible with zero external setup); additionally exports via
OTLP if `Telemetry:OtlpEndpoint` is configured, for a real collector
(Jaeger, Tempo, etc.) — genuine distributed tracing export, not structured
logs relabeled as traces. Metrics export (as opposed to tracing) is not
wired — only the tracing signal is set up here.
