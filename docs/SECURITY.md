# Security

This document describes the security measures actually implemented in this codebase —
not an aspirational checklist. Where the spec (Section 38) asks for something not yet
built, that's stated plainly below and in `docs/STATUS.md`.

## Authentication

- **MVC UI**: cookie-based auth (`Microsoft.AspNetCore.Authentication.Cookies`). A
  global `AuthorizeFilter` requires an authenticated user on every controller except
  `AccountController` (Login/Register). Passwords are hashed with PBKDF2-SHA256
  (100,000 iterations, random 16-byte salt) in `PasswordHasher.cs` — no external
  Identity package, but a real, standard KDF, not a toy hash.
- **Registration is gated**: `AccountController.Register` only allows creating a new
  account when no users exist yet, or when the caller is already authenticated. This
  is a simple first-run bootstrap, not a full invite/role system.
- **JSON API (`/api/*`)**: a separate `ApiKey` authentication scheme
  (`ApiKeyAuthenticationHandler`), validated against a **SHA-256 hash** stored in
  Postgres — the raw key is shown to the user exactly once at creation
  (`/ApiKeys`) and never stored or logged in plaintext. Keys can be revoked
  (soft-deleted via `RevokedAtUtc`) without deleting the audit history of when they
  were used.
- **SignalR hub**: `[Authorize]` on `AgentTelemetryHub` — the browser's existing
  auth cookie is sent automatically on the hub handshake.
- **Not covered by auth**: `/health*` endpoints and `/swagger*` (Swagger UI/JSON) are
  separate ASP.NET Core middleware, not MVC controllers, so the global MVC
  `AuthorizeFilter` doesn't apply to them. This is standard practice for health
  checks and API documentation, but is worth knowing if you're reasoning about what's
  actually gated.
- **What this does NOT cover**: there is no role/permission system (every
  authenticated user can do everything), no password reset flow, no email
  verification, no lockout-after-N-failed-attempts, and no MFA. This is
  single/small-team local-tool-grade authentication, not enterprise IAM.

## Filesystem sandboxing

Every file-touching tool (`FileReadTool`, `FileWriteTool`, `FileEditTool`,
`DirectoryListTool`) validates the requested path against the repository's workspace
root via `CommandPolicyEngine.IsWithinWorkspace` before touching disk. A path that
resolves (after `Path.GetFullPath`) outside the workspace root is refused — this
catches `../` traversal and absolute-path escapes. There is currently no symlink-
specific check; a symlink inside the workspace pointing outside it would not be
caught by a pure path-string comparison. This is a known gap, not a hidden one.

## Terminal command policy

`TerminalTool` never runs a command that hasn't passed `CommandPolicyEngine.Evaluate`:
- A fixed denylist of executables with no legitimate agent use (`mkfs`, `dd`,
  `shutdown`, `passwd`, etc.) is refused outright — **and this can never be
  overridden**, including by a persistent Always-Allow rule (see below).
- A fixed set of dangerous substring patterns (fork bombs, `rm -rf /`, piping a
  remote script into a shell, credential-file paths) forces human approval regardless
  of the base executable.
- Everything else not on the allowlist also requires approval — the default posture
  is "ask," not "allow."
- `ToolExecutionService` re-checks the same policy before invoking the tool, and
  `TerminalTool` itself re-validates defensively — there is no path that reaches
  process execution without a policy check.

**Persistent "Always Allow / Always Deny" scoping is now implemented**
(`CommandPermissionRule` + `CommandPermissionService`), scoped per authenticated user
and per base executable name. From the Tools console, when a command needs approval,
a person can choose "Approve & Run (once)" or persist a rule via "Always Allow this
executable" / "Always Deny this executable." Rules are listed and revocable from the
same page. The static denylist/dangerous-pattern check always runs first and always
wins — a persisted Always-Allow rule can make an ordinary "needs approval" command
skip future approval prompts, but it can never unlock something the denylist refuses.
Agent-run (not human-run) tool calls do not currently look up persisted rules — see
`docs/STATUS.md` for why.

## Secret redaction

`TerminalTool` runs a small set of regex substitutions over stdout/stderr before
returning it (API keys, `password=`, `secret=`, `Authorization: Bearer`, and
Postgres-style connection strings with an inline password). This is best-effort
pattern matching, not a guarantee — a secret in an unusual format will not be caught.

## Static/security scanning

`RegexSecurityPatternScanner` (Phase 6) scans tracked `.cs`/`.json`/`.config` files
for five patterns: hardcoded credential-like literals, connection strings with an
inline password, weak hash algorithms (MD5/SHA1), string-concatenated SQL, and
disabled TLS certificate validation. This is intentionally narrow — it is not a
substitute for a real SAST tool, has no suppression/allowlist mechanism, and only
scans file types the repository indexer already tracks.

## API surface

- Every JSON API controller (`/api/*`) uses DTOs, never raw EF entities (Section 45).
- A fixed-window rate limiter (60 requests/minute per the shared "api" policy) applies
  to all `/api/*` controllers.
- Every `/api/*` controller requires a valid API key (see Authentication above).

## Audit trail

Every tool invocation — allowed, denied, or pending approval — is written to
`ToolExecution` in Postgres with the tool name, arguments, decision, reason, and
result. Nothing is logged silently or skipped.

## What is explicitly NOT implemented

- Role/permission granularity beyond "authenticated or not" (see Authentication above)
- Password reset, email verification, lockout-after-N-attempts, MFA
- Symlink-aware path sandboxing
- A general SAST/dependency-vulnerability scanner
- Secrets-manager integration (API keys/connection strings currently come from
  `appsettings`/user-secrets/environment variables only, per standard ASP.NET Core
  configuration — there's no Vault/KeyVault adapter)
- Persistent command permission lookup for agent-initiated (not human-initiated)
  tool calls — see `docs/STATUS.md`
