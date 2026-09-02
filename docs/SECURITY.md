# Security

## Authentication & authorization

Real ASP.NET Core Identity (`AccountController`, `PlatformDbContext :
IdentityDbContext<ApplicationUser>`). Cookie-based, local — no cloud
identity provider dependency. All API and MVC controllers except
`AccountController`/`HomeController` are `[Authorize]`.

## Command execution sandboxing

`CommandPolicyEngine` (`Services/Tools/CommandPolicyEngine.cs`) evaluates
every command before `TerminalTool` executes it:

- **Denylist**: regex patterns for genuinely destructive commands
  (`rm -rf /`, `mkfs`, fork bombs, `dd ... of=/dev/sd*`, `shutdown`,
  `reboot`). Matched commands are refused outright — never executed.
- **Allowlist**: a small set of read-only/safe prefixes (`git status`,
  `git diff`, `git log`, `dotnet build`, `dotnet test`, `ls`, `dir`) run
  without further approval.
- **Everything else**: returns `RequireApproval` and is not executed unless
  the caller explicitly resends with `approved: true`.

This is enforced in code (`CommandPolicyEngineTests.cs` asserts all three
branches), not just documented as a policy.

## File system sandboxing

`FileReadTool`, `FileWriteTool`, `ProjectStructureTool`, `SearchSymbolTool`,
and `DependencyAnalysisTool` all resolve paths via
`Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath))` and then
verify the result still starts with `_workspaceRoot` before touching disk.
A path like `../../etc/passwd` is rejected with `FileWriteToolException` /
`FileReadToolException` — asserted in `FileWriteToolTests.WriteAsync_PathTraversal_IsBlocked`.

There is currently **no filesystem access outside the configured
`Workspace:Root`** from any tool in this codebase.

## Safe file modification

Per the platform's §59 requirement, `SafeFileEditService` and
`FileWriteTool` implement hash-based conflict detection: before an edit is
applied, the file's current content is hashed; if the caller's
`expectedHash` doesn't match what's actually on disk, the write is refused
rather than silently overwritten. Every edit is snapshotted to
`FileSnapshot` before writing, so `POST /api/tools/file/rollback` can
genuinely restore prior content.

## Autonomy gating

`AutonomyService` enforces Low/Medium/High levels. At Low, the automated
repair step inside `run-verified` is skipped — not attempted and then
discarded, genuinely not called — so no file edit happens without the user
having raised their autonomy level.

## What's NOT implemented yet

- Rate limiting on API endpoints
- CSRF tokens are present on the Identity forms (`@Html.AntiForgeryToken()`)
  but not yet enforced globally across all POST API endpoints
- Secret redaction in logs — Serilog is configured but no explicit
  redaction middleware/enricher exists yet; avoid putting secrets in
  `AuditLog.Details` or tool inputs until this is added
- No dedicated audit trail for authentication events beyond what ASP.NET
  Core Identity logs by default
