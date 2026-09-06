# Security Model

Local Agent Platform executes code and shell commands on local repositories, so its security boundary matters even when deployed only on a workstation or private server.

## Authentication

### MVC

MVC uses ASP.NET Core cookie authentication. Passwords are stored using the project's PBKDF2-SHA256 `PasswordHasher`; plaintext passwords are never stored.

### API

API keys are generated with high-entropy random material, displayed once, and stored only as SHA-256 hashes. `/api/*` endpoints require the API-key authentication scheme.

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

Ownership is rooted at `Project.OwnerUserId`. Repository, agent-session, memory, and tool paths now enforce that ownership in the principal MVC/API execution paths.

`AgentSession.OwnerUserId` is persisted so background work retains the originating identity after the HTTP request is gone. `ToolExecutionService` independently checks repository ownership when a caller user ID is supplied, preventing a controller-only authorization bypass.

## Command permissions

`CommandPolicyEngine` provides the static command policy. Per-user persisted executable decisions can be `AlwaysAllow` or `AlwaysDeny`.

The static hard deny path wins over persisted allow rules. Agent calls now pass their owning user ID into the same `ToolExecutionService`, so agents do not get a weaker permission path.

## Filesystem sandboxing

File tools are constrained to the registered repository root. Containment checking uses canonical absolute paths and prevents simple `..` traversal and prefix-confusion attacks. Existing symlink/reparse-point segments are resolved and rejected when their final target escapes the workspace.

This is defense in depth, not an OS sandbox. For hostile/untrusted repositories, run the platform inside a locked-down container/VM with a narrow bind mount and non-root user.

## Secret handling

- Passwords: PBKDF2 hash only.
- Recovery codes: PBKDF2 hash only.
- API keys: SHA-256 hash only.
- MFA secrets: ASP.NET Core Data Protection ciphertext.
- Terminal output: basic pattern redaction for common password/token/API-key/connection-string forms.
- CI/CD deployment secrets: expected through GitHub Environments/Secrets, not committed configuration.

## Remaining security work for high-assurance deployments

For internet-facing or enterprise use, add at minimum:

- Account lockout/rate limiting specifically on login and recovery endpoints.
- Data Protection key-ring persistence and rotation strategy for multi-instance deployments.
- Fine-grained RBAC/permissions beyond the current Admin/User split.
- CSRF/security regression tests for all state-changing MVC actions.
- OS-level sandbox/container isolation for tool processes.
- Per-command resource limits beyond wall-clock timeout.
- Centralized secret manager integration for production.
- Security headers/CSP tuned to the final UI dependencies.
- Audit-log retention/immutability policy.
- Formal threat model and penetration testing.
