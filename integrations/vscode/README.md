# Local Agent Platform for VS Code

A small VS Code extension for starting and monitoring real Local Agent Platform sessions. It calls the platform's authenticated HTTP API; it does not embed a model runtime or execute code in the editor process.

## Features

- Stores the API key in VS Code `SecretStorage` (not settings or workspace files).
- Lists only repositories returned by the server's authenticated, workspace-root-filtered `GET /api/repositories` endpoint; can open an available local checkout in a new VS Code window, but refuses paths unavailable on the editor host.
- Opens a selected file from that local checkout. Traversal is capped at 50,000 entries, 5,000 files, and 32 levels; it skips generated and linked directories, then revalidates containment and file type before opening. Files over 10 MiB are refused to protect the editor host.
- Lists registered models and starts a session through `POST /api/agent/sessions`.
- Shows recent sessions and their task nodes, with session summaries and task output in an Output channel.
- Shows each task's persisted tool-argument JSON and includes it in the one-time approval confirmation; cancellation also requires confirmation.
- Adds a **Changed files** group from completed `FileWriteTool`/`FileEditTool` task paths. Selecting a file uses the session's repository ID, configured server-to-local path mapping, and the same canonical containment/file-type/size checks as repository browsing; stale, missing, or unsafe local files are not opened.
- Refreshes the session tree every 15 seconds and provides a manual refresh command.
- Sends credentials over HTTPS for remote endpoints. Plain HTTP is accepted only for `localhost`, `127.0.0.1`, and `::1`.

## Requirements and setup

- VS Code 1.90 or newer.
- A running Local Agent Platform web service, at a base URL reachable from the VS Code host.
- A platform API key created in the web UI. The authenticated account must own the repository, and the server must allow its path.
- At least one repository and registered model for starting a session.

For local development, open this directory as a VS Code extension development host (for example, use **Run Extension** in the VS Code Extension Development Host launch configuration). Configure `localAgentPlatform.baseUrl` if the server is not at `http://localhost:8080`, then run **Local Agent Platform: Configure API Key** from the Command Palette. Use the **Local Agent Platform** view in Explorer to start and follow sessions.

When the API server reports container/remote paths that differ from paths visible to the VS Code extension host, configure `localAgentPlatform.repositoryPathMappings`. For example, if Compose reports `/workspace/my-repo` and the same checkout is `/home/me/Local-Agent-Platform/workspace/my-repo` on the editor host, set:

```json
"localAgentPlatform.repositoryPathMappings": [
  {
    "serverRoot": "/workspace",
    "localRoot": "/home/me/Local-Agent-Platform/workspace"
  }
]
```

Use absolute paths for both roots. The longest matching server root wins. Mapping is only used for opening local repositories/files; session operations still use server-side repository IDs. The mapped root must exist locally, and the extension still rejects linked repository roots, linked files, traversal outside the root, and oversized files.

The API key is sent in the `X-Api-Key` header. Do not disable TLS verification or use an insecure remote HTTP endpoint. Protect the platform API key as a bearer secret and revoke it from the web UI if exposed.

## Package the extension

Create a VSIX from this directory with:

```bash
npx --yes @vscode/vsce package --no-dependencies
```

The package includes the extension runtime, README, and MIT license; tests and development configuration are excluded. VSIX packaging and the Extension Host smoke test are verified in CI, but the extension has not been tested against a live platform.

## Tests

The API-client, command-flow, manifest-wiring, changed-file, and local-file containment tests run with Node's built-in test runner:

```bash
npm test
```

They exercise HTTP/TLS URL validation, authentication headers, JSON requests, error handling, completed-only changed-file extraction, mapped file opening, manifest command wiring, and local filesystem containment. To launch VS Code itself and smoke-test extension activation and command registration, install the development dependencies and run:

```bash
npm ci
# Linux CI/headless Linux:
xvfb-run -a npm run test:extension-host
# Windows/macOS with a desktop session:
npm run test:extension-host
```

The Extension Host test downloads VS Code if it is not already cached. These tests still do not replace an end-to-end test against a running platform. The repository's opt-in [`scripts/run-live-platform-e2e.js`](../../scripts/run-live-platform-e2e.js) exercises the real authenticated platform API, Ollama-backed agent session, and tool/verification pipeline using a pinned disposable workspace; it does not launch VS Code, so live extension UI behavior remains unverified.
