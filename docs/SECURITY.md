# Security Model

Local Agent Platform executes code and shell commands on local repositories, so its security boundary matters even when deployed only on a workstation or private server.

## Authentication

### MVC

MVC uses ASP.NET Core cookie authentication. Passwords are stored using the project's PBKDF2-SHA256 `PasswordHasher`; plaintext passwords are never stored.

### API

API keys are generated with high-entropy random material, displayed once, and stored only as SHA-256 hashes. `/api/*` endpoints require the API-key authentication scheme. The VS Code client stores its key through VS Code `SecretStorage`, never in extension settings or workspace files. It refuses to send keys over plain HTTP except to loopback hosts; deployments accessed across a network must provide HTTPS. API-key authentication is not a substitute for TLS or network access controls.

## Roles

Two roles are implemented:

- `Admin`
- `User`

The first account becomes `Admin`. Once an account exists, anonymous registration is closed and new accounts can only be created by an authenticated admin.

Role claims are written into the MVC authentication cookie. The API currently relies primarily on owner identity rather than role-gated administration endpoints.

## MFA

TOTP MFA is implemented without a cloud dependency. MFA secrets are protected at rest with ASP.NET Core Data Protection. Verification accepts the current 30-second window plus one adjacent window on either side to tolerate small clock drift.

Users must first generate a secret and then prove possession by entering a valid code before `MfaEnabled` is set.

## Password recovery

Each account receives a cryptographically random recovery code. Only its PBKDF2 hash is stored. Successful recovery:

1. Changes the password.
2. Disables MFA to recover from a lost authenticator.
3. Generates and stores a new recovery-code hash.
4. Displays the replacement recovery code exactly once.

There is intentionally no email-based reset because this is a local-first platform and no email provider is configured.

## User/workspace isolation

Ownership is rooted at `Project.OwnerUserId`. Repository, agent-session, memory, tool, telemetry, IDE, and SignalR paths enforce ownership in their MVC/API execution paths. Repository and session listings also hide records whose paths no longer pass the workspace-root policy.

`AgentSession.OwnerUserId` is persisted so background work retains the originating identity after the HTTP request is gone. `ToolExecutionService` independently checks repository ownership when a caller user ID is supplied and enforces the configured `Repositories:AllowedRoots` allowlist, preventing a controller-only authorization bypass. The allowlist is empty by default (fail-closed); Compose explicitly allows `/workspace`.

## Command permissions

`CommandPolicyEngine` provides the static command policy. Per-user `AlwaysDeny` rules can block terminal executables and apply to both console and agent calls. `AlwaysAllow` remains representable for database compatibility but is intentionally unavailable in the UI while `TerminalTool` is High risk; every terminal invocation requires its own approval, and a stored allow can never clear that independent gate. Dangerous-pattern and destructive-Git approvals are also one-time. If an approved invocation fails, retries return to the approval gate. Agent calls pass their owning user ID into the same `ToolExecutionService`, so agents do not get a weaker permission path. Terminal/build/test/Git child processes use argument vectors, bounded output capture, and a filtered environment to avoid inheriting platform credentials.

## Filesystem sandboxing

File tools, indexing, context reads, build/test targets, verification, and VS Code open-file requests are constrained to registered repository roots, and repository roots must be existing real directories under existing real configured allowlist roots. A configured allowlist root or repository root that is itself a symlink/reparse point is rejected, even if its target remains under the configured path. Allowlist roots are resolved to physical paths at startup; symlinked ancestors are canonicalized during containment checks, preventing them from redirecting repository access outside the configured root. Canonical absolute-path checks also prevent `..` traversal and prefix-confusion attacks. Existing symlink/reparse-point segments are resolved and rejected when their final target escapes the workspace. Indexing skips linked directories; strict verification stops rather than passing when links or inaccessible directories make coverage ambiguous. File mutations use expected SHA-256 hashes, size limits, and same-directory atomic replacement, but path checks cannot eliminate every time-of-check/time-of-use race.

This is defense in depth, not an OS sandbox. The Compose web process drops to a configurable non-root UID before starting; its trusted entrypoint retains only the capabilities needed to repair key-ring volume ownership and drop privileges. `no-new-privileges` is enabled. Compose publishes the web, PostgreSQL, and Ollama ports on loopback only by default. Tool subprocesses still share the web process UID and container namespace; sufficiently hostile code may inspect same-UID process state, including the web process environment. Do not treat filtered child environments as a hard secret boundary. For hostile/untrusted repositories, use a separate tool-runner container/VM with a narrow bind mount, no application credentials, resource limits, and a dedicated unprivileged identity.

The VS Code extension's local file picker operates only on the allowed repository paths returned by the server and present on the editor host. It skips linked entries and generated directories, bounds traversal to 50,000 entries/5,000 files/32 levels, refuses files over 10 MiB, and rechecks containment and file type before opening. Remote/container paths are mapped only through explicit `localAgentPlatform.repositoryPathMappings`; without a mapping, the returned path must exist on the editor host. The extension never fetches file contents from the server.

## Verification and subprocess boundaries

Build/test commands are executed only after fresh user approval. The security-pattern scan re-enumerates supported source/configuration files at verification time (so new/unindexed files are included), redacts detected credential excerpts before storage, and fails the verification path on unreadable directories, reparse/special files, more than 100,000 traversed entries, more than 5,000 scannable files, more than 64 MiB total input, files over 2 MiB, or more than 10,000 findings. It remains a small heuristic scanner, not a full SAST product; passing it must not be treated as proof that a repository has no vulnerabilities.

Child process environments are filtered to remove application secrets. Tool output is bounded and common credential formats are redacted, but arbitrary application-specific secrets may evade pattern-based redaction.

## Secret handling

- Passwords: PBKDF2 hash only.
- Recovery codes: PBKDF2 hash only.
- API keys: SHA-256 hash only.
- MFA secrets: ASP.NET Core Data Protection ciphertext.
- Terminal output: basic pattern redaction for common password/token/API-key/connection-string forms.
- CI/CD deployment secrets: expected through GitHub Environments/Secrets, not committed configuration.

## Remaining security work for high-assurance deployments

For internet-facing or enterprise use, add at minimum:

- Account lockout and per-account failed-attempt tracking. Login, registration, MFA confirmation, and recovery currently have a tighter per-remote-IP rate limit, which does not stop distributed attacks.
- Data Protection key rotation, filesystem permission, backup, and multi-instance operational strategy. Optional key-ring persistence is configured; Docker Compose uses a named volume.
- Fine-grained RBAC/permissions beyond the current Admin/User split.
- CSRF/security regression tests for all state-changing MVC actions.
- OS-level sandbox/container isolation for tool processes.
- Per-command resource limits beyond wall-clock timeout.
- Centralized secret manager integration for production.
- Security headers/CSP tuned to the final UI dependencies.
- Audit-log retention/immutability policy.
- Formal threat model and penetration testing.
