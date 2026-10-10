# Production deployment gate

This guide describes the configuration and operational controls required before exposing the platform outside a trusted, single-host development environment. It is deliberately conservative: CI passing is not a production launch approval, and no production deployment or live model run has been performed from this checkout.

## Configuration that must be explicit

Run the web host with `ASPNETCORE_ENVIRONMENT=Production` and supply secrets through the platform's secret manager or protected environment variables. `appsettings.json` contains no database credentials.

Required settings:

| Setting | Requirement |
|---|---|
| `ConnectionStrings__PlatformDb` | Secret-managed PostgreSQL connection to a dedicated application database/role. Do not use the `postgres` superuser or the local Compose password. Require TLS verification when the database connection crosses a host/network boundary. |
| `AllowedHosts` | Exact, non-loopback DNS hostnames separated with semicolons. Wildcards and a loopback-only list are rejected at startup. |
| `DataProtection__KeyRingPath` | Absolute, persistent, writable location on a protected volume. Preserve and back up this key ring: losing it invalidates cookies and prevents decrypting MFA secrets. Restrict access to the web UID. |
| `Repositories__AllowedRoots__0` (and any additional indexed roots) | Existing, safe, non-filesystem-root directories. Production startup fails if no valid root is configured. Mount only repositories the service is allowed to access. |
| `ForwardedHeaders__Enabled` | Set `true` when TLS terminates at a reverse proxy. The application currently listens on HTTP inside its container. |
| `ForwardedHeaders__KnownProxies__0` | Exact IP address of the trusted proxy; add one entry per trusted proxy. CIDR-wide or arbitrary-client trust is not enabled. The proxy must overwrite incoming `X-Forwarded-For` and `X-Forwarded-Proto` headers. |
| `Ollama__BaseUrl` | Reachable, operator-controlled model endpoint. Keep it on a private trusted network; use TLS if it crosses a host/network boundary. |

First-admin bootstrap in Production additionally requires `Security__BootstrapAdminToken`, generated out of band with at least 32 random bytes (for example, `openssl rand -hex 32`). It is checked only while the user table is empty. Remove the secret after creating the first administrator. Do not put it in source control, images, command-line arguments, or logs.

Swagger is disabled outside Development. The authentication cookie is `Secure`, `HttpOnly`, `SameSite=Lax`, and uses the `__Host-` prefix in Production. Console trace export is Development-only; production trace export, retention, alerting, and log redaction/retention must be configured by the operator.

## Deployment and database procedure

1. Build and scan the image from a reviewed commit; deploy an immutable commit/digest tag rather than `latest`.
2. Provision a dedicated database and least-privilege migration-capable application role. Store its connection string outside the image/repository.
3. Take an encrypted, off-host PostgreSQL backup and verify restoration to an isolated database before each schema-changing release. The application applies EF migrations at startup under a PostgreSQL advisory lock, which serializes replicas but does not make a migration backward-compatible or replace a restore plan.
4. Provision the persistent Data Protection volume and workspace mount with least-privilege ownership and a tested recovery procedure.
5. Put a TLS-terminating reverse proxy in front of the web container. Bind the container port to a private interface, set exact `AllowedHosts`, and configure the proxy's exact IP in `ForwardedHeaders:KnownProxies`.
6. Configure resource limits, process supervision, disk monitoring, database backup/restore monitoring, secret rotation, patch cadence, and an operator for the initial admin bootstrap.
7. Verify `/health/live` and `/health/ready`, login/MFA, API-key revocation, a harmless read-only repository operation, backups, restore, and rollback in staging before promoting the immutable image.

The repository's `docker-compose.yml` is local development only: it deliberately sets Development mode, loopback port bindings, and weak local defaults. Do not use that Compose file as a production deployment manifest. The optional SSH CD workflow deploys only from `main` and passes a full commit-SHA image reference as `LAP_DEPLOY_IMAGE`; the externally managed production Compose file must use `image: ${LAP_DEPLOY_IMAGE:?LAP_DEPLOY_IMAGE is required}` for its `web` service and must not rebuild or deploy `latest`. The workflow requires an independently secured deployment directory and production Compose/configuration; its passing CI checks do not validate a remote host.

## Security boundaries and release blockers

- BuildTool, TestTool, TerminalTool, and other child processes still share the web process UID and container namespace. The Docker hardening in this repository is defense in depth, **not a hostile-code sandbox**. Until tools run in a separate unprivileged container/VM with a narrow workspace mount, no credentials, network restrictions, and CPU/memory/PID limits, operate only on trusted repositories and users.
- The in-memory background queue is not a durable message broker. Session state/claims are persisted and startup recovers unclaimed `Created` sessions, but this is not exactly-once processing or a substitute for deployment/load testing under worker failure.
- CI validates an opt-in EnsureCreated-adoption procedure only against a disposable PostgreSQL fixture. It is not a supported production cutover; do not run it against a valuable database. Use a separately rehearsed, operator-approved migration plan.
- CI does not run a live Ollama model or the platform E2E harness, and no production-like endpoint has been exercised here. The E2E harness deliberately never approves high-risk tasks.
- As of 2026-10-08, .NET 8 is in its maintenance phase and reaches end of support on 2026-11-10. A production release extending beyond that date is blocked on a tested migration to a supported runtime (currently .NET 10 LTS) and compatible dependency/toolchain updates. Check the [official .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) when scheduling the upgrade.
- No third-party vulnerability scan, SBOM/signature verification, disaster-recovery exercise, independent penetration test, or live production-like soak test is represented as complete by this repository's current CI.

## Promotion rule

Treat a release as production-approved only after every deployment-specific item above has a named owner and verified evidence, the runtime support upgrade is complete, the tool sandbox decision is resolved, and a staging restore/rollback/live-model rehearsal has passed. A green code-CI run by itself is insufficient.
