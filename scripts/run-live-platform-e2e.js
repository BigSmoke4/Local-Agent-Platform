#!/usr/bin/env node
'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const FIXTURE_HASHES = Object.freeze({
  '.gitignore': '6c5082c7e6de3e350189c67a1aacf757485ecc26e0a3cf96683528565f3e5f12',
  '.lap-live-e2e-disposable': '57425226bc78b402fbb9f6edd6384d740262b57134aec0c8a2cd3b552a60bce5',
  'Disposable.csproj': '7124b1375fccf83ee18a440ae3322055d1e8ca7305e314117b55294b72c4ace5',
  'DisposableTests.cs': 'b8e2097a5cbdbd25dc96ae89f1622f49c484e59a7be490f0171a312431626eeb'
});
const TERMINAL_STATES = new Set(['Completed', 'Failed', 'Cancelled']);
const VERIFICATION_DESCRIPTIONS = Object.freeze({
  BuildTool: 'Run an approved workspace build for verification. Build targets can execute repository code.',
  TestTool: 'Run approved workspace tests for verification. Tests execute repository code.'
});
const ALLOW_BUILD_AND_TEST_VALUE = 'I_APPROVE_BUILD_AND_TEST';
const ALLOW_DISPOSABLE_REPOSITORY_VALUE = 'I_CONFIRM_THIS_REPOSITORY_IS_DISPOSABLE';

let interrupted = false;

function fail(message) {
  throw new Error(message);
}

function requiredEnvironment(env, name) {
  const value = String(env[name] || '').trim();
  if (!value) fail(`Required environment variable ${name} is not set.`);
  return value;
}

function parseBaseUrl(value) {
  let parsed;
  try {
    parsed = new URL(value);
  } catch {
    fail('LAP_E2E_BASE_URL must be a valid HTTP(S) URL.');
  }

  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password ||
      parsed.search || parsed.hash || parsed.pathname !== '/') {
    fail('LAP_E2E_BASE_URL must be an origin-only HTTP(S) URL with no credentials, path, query, or fragment.');
  }
  const localHosts = new Set(['localhost', '127.0.0.1', '[::1]']);
  if (parsed.protocol === 'http:' && !localHosts.has(parsed.hostname.toLowerCase())) {
    fail('Plain HTTP is allowed only for localhost, 127.0.0.1, or ::1; use HTTPS for remote platforms.');
  }
  return parsed.origin;
}

function parsePositiveInteger(value, name, fallback, minimum, maximum) {
  if (value === undefined || value === '') return fallback;
  if (!/^\d+$/.test(String(value))) fail(`${name} must be an integer between ${minimum} and ${maximum}.`);
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < minimum || parsed > maximum)
    fail(`${name} must be an integer between ${minimum} and ${maximum}.`);
  return parsed;
}

function readConfig(env = process.env) {
  const nodeMajorVersion = Number(process.versions.node.split('.')[0]);
  if (!Number.isInteger(nodeMajorVersion) || nodeMajorVersion < 18) fail('Node.js 18 or newer is required for the live platform E2E runner.');
  if (env.LAP_E2E_CONFIRM_DISPOSABLE_REPOSITORY !== ALLOW_DISPOSABLE_REPOSITORY_VALUE) {
    fail(`Set LAP_E2E_CONFIRM_DISPOSABLE_REPOSITORY=${ALLOW_DISPOSABLE_REPOSITORY_VALUE} only after registering the dedicated disposable fixture repository.`);
  }
  if (env.LAP_E2E_ALLOW_BUILD_AND_TEST !== ALLOW_BUILD_AND_TEST_VALUE) {
    fail(`Set LAP_E2E_ALLOW_BUILD_AND_TEST=${ALLOW_BUILD_AND_TEST_VALUE} to explicitly authorize this test's exact, system-generated BuildTool and TestTool verification tasks. No other command or tool is auto-approved.`);
  }

  const baseUrl = parseBaseUrl(requiredEnvironment(env, 'LAP_E2E_BASE_URL'));
  const apiKey = requiredEnvironment(env, 'LAP_E2E_API_KEY');
  if (!apiKey.startsWith('lap_')) fail('LAP_E2E_API_KEY does not have the expected lap_ prefix.');

  const repositoryId = requiredEnvironment(env, 'LAP_E2E_REPOSITORY_ID');
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(repositoryId)) {
    fail('LAP_E2E_REPOSITORY_ID must be a UUID returned by GET /api/repositories.');
  }

  const expectedServerRepositoryPath = requiredEnvironment(env, 'LAP_E2E_SERVER_REPOSITORY_PATH');
  if (!path.isAbsolute(expectedServerRepositoryPath)) {
    fail('LAP_E2E_SERVER_REPOSITORY_PATH must be the absolute path reported by GET /api/repositories.');
  }

  const requestedLocalPath = requiredEnvironment(env, 'LAP_E2E_LOCAL_REPOSITORY_PATH');
  if (!path.isAbsolute(requestedLocalPath)) fail('LAP_E2E_LOCAL_REPOSITORY_PATH must be an absolute path on this machine.');
  const requestedRepositoryPath = path.resolve(requestedLocalPath);
  let requestedRepositoryInfo;
  try {
    requestedRepositoryInfo = fs.lstatSync(requestedRepositoryPath);
  } catch {
    fail('LAP_E2E_LOCAL_REPOSITORY_PATH does not exist or cannot be resolved.');
  }
  if (requestedRepositoryInfo.isSymbolicLink() || !requestedRepositoryInfo.isDirectory()) {
    fail('LAP_E2E_LOCAL_REPOSITORY_PATH must be a real directory, not a symlink.');
  }
  let localRepositoryPath;
  try {
    localRepositoryPath = fs.realpathSync(requestedRepositoryPath);
  } catch {
    fail('LAP_E2E_LOCAL_REPOSITORY_PATH does not exist or cannot be resolved.');
  }

  const modelId = requiredEnvironment(env, 'LAP_E2E_MODEL_ID');
  const timeoutSeconds = parsePositiveInteger(env.LAP_E2E_TIMEOUT_SECONDS, 'LAP_E2E_TIMEOUT_SECONDS', 840, 60, 900);
  const pollIntervalMs = parsePositiveInteger(env.LAP_E2E_POLL_INTERVAL_MS, 'LAP_E2E_POLL_INTERVAL_MS', 2500, 2100, 10000);
  const keepOutputFile = env.LAP_E2E_KEEP_FILE === '1';

  return {
    baseUrl,
    apiKey,
    repositoryId,
    expectedServerRepositoryPath,
    localRepositoryPath,
    modelId,
    timeoutMs: timeoutSeconds * 1000,
    pollIntervalMs,
    keepOutputFile
  };
}

function runGit(repositoryPath, args, { allowFailure = false, raw = false } = {}) {
  const result = spawnSync('git', ['-C', repositoryPath, ...args], {
    encoding: 'utf8',
    maxBuffer: 2 * 1024 * 1024,
    windowsHide: true
  });
  if (result.error) fail(`Could not execute git (${result.error.message}).`);
  if (!allowFailure && result.status !== 0) {
    const detail = (result.stderr || result.stdout || '').trim();
    fail(`git ${args[0]} failed${detail ? `: ${detail}` : '.'}`);
  }
  return { status: result.status, stdout: raw ? result.stdout : result.stdout.trim(), stderr: result.stderr.trim() };
}

function sha256NormalizedText(filePath) {
  const text = fs.readFileSync(filePath, 'utf8').replace(/\r\n/g, '\n');
  return crypto.createHash('sha256').update(text, 'utf8').digest('hex');
}

function verifyFixture(repositoryPath) {
  const gitRoot = runGit(repositoryPath, ['rev-parse', '--show-toplevel']).stdout;
  let resolvedGitRoot;
  try {
    resolvedGitRoot = fs.realpathSync(gitRoot);
  } catch {
    fail('The selected local path is not the root of a Git worktree.');
  }
  if (resolvedGitRoot !== repositoryPath) {
    fail('The selected local path must be the root of the disposable Git worktree, not a parent or subdirectory.');
  }

  const trackedFiles = runGit(repositoryPath, ['ls-files', '--cached', '-z'], { raw: true }).stdout
    .split('\0').filter(Boolean).sort();
  const expectedFiles = Object.keys(FIXTURE_HASHES).sort();
  if (JSON.stringify(trackedFiles) !== JSON.stringify(expectedFiles)) {
    fail(`The disposable worktree must track only the four checked-in fixture files (${expectedFiles.join(', ')}).`);
  }

  for (const [name, expectedHash] of Object.entries(FIXTURE_HASHES)) {
    const filePath = path.join(repositoryPath, name);
    let info;
    try {
      info = fs.lstatSync(filePath);
    } catch {
      fail(`Disposable fixture file is missing: ${name}`);
    }
    if (info.isSymbolicLink() || !info.isFile()) fail(`Disposable fixture file must be a regular file: ${name}`);
    const actualHash = sha256NormalizedText(filePath);
    if (actualHash !== expectedHash) fail(`Disposable fixture file differs from the reviewed test fixture: ${name}`);
  }

  const initialStatus = gitStatusRecords(repositoryPath);
  if (initialStatus.length !== 0) fail('The disposable fixture Git worktree must be completely clean before the test starts.');
  return true;
}

function gitStatusRecords(repositoryPath) {
  return runGit(repositoryPath, ['status', '--porcelain=v1', '--untracked-files=all', '--ignored=matching', '-z'], { raw: true })
    .stdout.split('\0').filter(Boolean);
}

function assertWorkspaceState(repositoryPath, targetName, { expectTarget = false, allowBuildArtifacts = false } = {}) {
  const records = gitStatusRecords(repositoryPath);
  const visibleChanges = records.filter(record => !record.startsWith('!! ')).sort();
  const expectedChanges = expectTarget ? [`?? ${targetName}`] : [];
  if (JSON.stringify(visibleChanges) !== JSON.stringify(expectedChanges)) {
    fail(`Disposable workspace changed unexpectedly. Expected ${expectedChanges.join(', ') || 'no visible changes'}; got ${visibleChanges.join(', ') || 'none'}. Changes were preserved for inspection.`);
  }

  const ignoredPaths = records.filter(record => record.startsWith('!! ')).map(record => record.slice(3)).sort();
  const allowedIgnoredPaths = allowBuildArtifacts ? ['bin/', 'obj/'] : [];
  if (JSON.stringify(ignoredPaths) !== JSON.stringify(allowedIgnoredPaths)) {
    fail(`Disposable workspace contains unexpected ignored paths: ${ignoredPaths.join(', ') || '(none)'}. Changes were preserved for inspection.`);
  }
}

function assertTargetIsNotIgnored(repositoryPath, targetName) {
  const result = runGit(repositoryPath, ['check-ignore', '--quiet', '--', targetName], { allowFailure: true });
  if (result.status === 0) fail(`The generated target ${targetName} is ignored by Git; refusing to run an unobservable file-mutation test.`);
  if (result.status !== 1) fail('Could not determine whether the generated target file is ignored by Git.');
}

function assertTargetContent(repositoryPath, targetName, marker) {
  const filePath = path.join(repositoryPath, targetName);
  let info;
  try {
    info = fs.lstatSync(filePath);
  } catch {
    fail(`The live agent did not create the expected file ${targetName}.`);
  }
  if (info.isSymbolicLink() || !info.isFile()) fail(`The generated target ${targetName} is not a regular file.`);
  const content = fs.readFileSync(filePath, 'utf8');
  if (content !== marker && content !== `${marker}\n` && content !== `${marker}\r\n`) {
    fail(`The generated target ${targetName} does not contain the exact one-line live-test marker.`);
  }
}

function parseTaskArguments(task) {
  if (task.argumentsJson === null || task.argumentsJson === undefined || task.argumentsJson === '') return {};
  let parsed;
  try {
    parsed = JSON.parse(task.argumentsJson);
  } catch {
    fail(`Task ${task.id || '(unknown)'} has invalid persisted arguments; refusing to continue.`);
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed) ||
      Object.values(parsed).some(value => typeof value !== 'string')) {
    fail(`Task ${task.id || '(unknown)'} does not contain a string-valued tool-argument object.`);
  }
  return parsed;
}

function hasExactKeys(value, expectedKeys) {
  const keys = Object.keys(value).sort();
  return JSON.stringify(keys) === JSON.stringify([...expectedKeys].sort());
}

function validateTask(task, { targetName, marker }) {
  if (!task || typeof task !== 'object') fail('The live platform returned a malformed task record.');
  const type = String(task.type || '');
  const toolName = String(task.toolName || '');
  const args = parseTaskArguments(task);

  if (type === 'Reasoning') {
    if (toolName) fail(`Unexpected tool attached to a reasoning task (${toolName}); refusing to continue.`);
    return { kind: 'reasoning', task, args };
  }

  if (type === 'Verification') {
    const knownTool = Object.keys(VERIFICATION_DESCRIPTIONS).find(name => name.toLowerCase() === toolName.toLowerCase());
    if (!knownTool || !hasExactKeys(args, []) ||
        task.description !== VERIFICATION_DESCRIPTIONS[knownTool]) {
      fail(`Refusing an unexpected verification task (${toolName || 'unnamed'}).`);
    }
    return { kind: 'verification', toolName: knownTool, task, args };
  }

  if (type !== 'ToolCall') fail(`Refusing an unsupported live-session task type: ${type || '(empty)'}.`);

  const normalizedTool = toolName.toLowerCase();
  if (normalizedTool === 'filereadtool') {
    if (!hasExactKeys(args, ['path']) || ![...Object.keys(FIXTURE_HASHES), targetName].includes(args.path)) {
      fail('The live model requested a file read outside the disposable fixture or generated test file.');
    }
    return { kind: 'read', toolName: 'FileReadTool', task, args };
  }

  if (normalizedTool === 'directorylisttool') {
    if (!hasExactKeys(args, []) && !hasExactKeys(args, ['path'])) fail('The live model requested an unexpected directory-list operation.');
    if (args.path !== undefined && args.path !== '' && args.path !== '.') {
      fail('The live model requested a directory listing outside the disposable repository root.');
    }
    return { kind: 'list', toolName: 'DirectoryListTool', task, args };
  }

  if (normalizedTool === 'filewritetool') {
    if (!hasExactKeys(args, ['path', 'content', 'expectedHash']) || args.path !== targetName ||
        (args.content !== marker && args.content !== `${marker}\n` && args.content !== `${marker}\r\n`) ||
        String(args.expectedHash).toUpperCase() !== 'NEW') {
      fail('The live model requested a file write other than the unique, exact marker file; the disposable workspace was preserved.');
    }
    return { kind: 'write', toolName: 'FileWriteTool', task, args };
  }

  fail(`Refusing to run or approve the live model's ${toolName || '(unnamed)'} tool call. Only fixture reads/listing and one exact marker-file write are permitted.`);
}

function validateAllTasks(tasks, context) {
  if (!Array.isArray(tasks)) fail('The live platform returned a malformed task list.');
  return tasks.map(task => validateTask(task, context));
}

function assertSuccessfulSession(session, validatedTasks, repositoryPath, targetName, marker) {
  if (session.state !== 'Completed') fail(`Live agent session finished in ${session.state || '(empty state)'}, not Completed: ${session.failureReason || 'no failure reason was returned.'}`);

  const writes = validatedTasks.filter(entry => entry.kind === 'write');
  if (writes.length !== 1 || writes[0].task.status !== 'Completed') {
    fail(`Expected exactly one completed FileWriteTool task; found ${writes.length}.`);
  }
  const buildTasks = validatedTasks.filter(entry => entry.kind === 'verification' && entry.toolName === 'BuildTool');
  const testTasks = validatedTasks.filter(entry => entry.kind === 'verification' && entry.toolName === 'TestTool');
  if (buildTasks.length !== 1 || buildTasks[0].task.status !== 'Completed' || buildTasks[0].task.error) {
    fail('The session did not record one successful, completed system BuildTool verification task.');
  }
  if (!/Build succeeded\./i.test(String(buildTasks[0].task.output || ''))) {
    fail('BuildTool did not return the real “Build succeeded.” output required by this test.');
  }
  if (testTasks.length !== 1 || testTasks[0].task.status !== 'Completed' || testTasks[0].task.error) {
    fail('The session did not record one successful, completed system TestTool verification task.');
  }
  if (!/Failed:\s*0,\s*Passed:\s*[1-9]\d*,\s*Skipped:\s*\d+,\s*Total:\s*[1-9]\d*/.test(String(testTasks[0].task.output || ''))) {
    fail('TestTool output did not include a real, non-empty, zero-failure test summary.');
  }
  if (validatedTasks.some(entry => entry.task.status !== 'Completed')) {
    fail('The completed session contains a task that did not complete successfully.');
  }

  assertTargetContent(repositoryPath, targetName, marker);
  assertWorkspaceState(repositoryPath, targetName, { expectTarget: true, allowBuildArtifacts: true });
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function createApiClient(baseUrl, apiKey) {
  return async function request(route, { method = 'GET', body, authenticated = true, expectedStatuses = [200] } = {}) {
    const url = new URL(route, baseUrl);
    if (url.origin !== baseUrl) fail('Refusing to send credentials outside the configured platform origin.');
    const headers = { Accept: 'application/json' };
    if (authenticated) headers['X-Api-Key'] = apiKey;
    const options = { method, headers, redirect: 'error', signal: AbortSignal.timeout(60_000) };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify(body);
    }

    let response;
    let text;
    try {
      response = await fetch(url, options);
      text = await response.text();
    } catch (error) {
      if (error && error.name === 'TimeoutError') fail(`Request to ${route} timed out after 60 seconds.`);
      fail(`Could not complete request to ${route}; verify the platform URL and network connection.`);
    }

    if (!expectedStatuses.includes(response.status)) {
      const detail = text.trim().slice(0, 1200);
      fail(`${method} ${route} returned HTTP ${response.status}${detail ? `: ${detail}` : '.'}`);
    }
    if (!text) return null;
    try {
      return JSON.parse(text);
    } catch {
      if (route === '/health/ready') return text.trim();
      fail(`${method} ${route} returned a non-JSON response.`);
    }
  };
}

function makeUserRequest(targetName, marker) {
  return [
    'This is an explicitly authorized live-platform end-to-end test in a disposable repository.',
    `Create exactly one new file at the repository root named ${targetName}.`,
    `The complete UTF-8 contents must be exactly this single line, with no quotes and no trailing newline: ${marker}`,
    `Use FileWriteTool with path ${targetName}, content ${marker}, and expectedHash NEW.`,
    'Do not edit or create any other file, and do not run commands. Stop after the one file is written.'
  ].join('\n');
}

async function fetchSnapshot(request, sessionId) {
  const encodedId = encodeURIComponent(sessionId);
  const session = await request(`/api/agent/sessions/${encodedId}`);
  const tasks = await request(`/api/agent/sessions/${encodedId}/tasks`);
  return { session, tasks };
}

async function requestCancellation(request, sessionId) {
  try {
    const snapshot = await fetchSnapshot(request, sessionId);
    if (TERMINAL_STATES.has(snapshot.session.state)) return;
    await request(`/api/agent/sessions/${encodeURIComponent(sessionId)}/cancel`, {
      method: 'POST', body: {}, expectedStatuses: [202, 404]
    });
    const deadline = Date.now() + 15_000;
    while (Date.now() < deadline) {
      await delay(1000);
      const afterCancel = await request(`/api/agent/sessions/${encodeURIComponent(sessionId)}`);
      if (TERMINAL_STATES.has(afterCancel.state)) return;
    }
  } catch {
    // Cancellation is best-effort on the error path. The primary failure is reported,
    // and no workspace cleanup is attempted after an unsuccessful live run.
  }
}

async function runLivePlatformE2E(env = process.env) {
  const config = readConfig(env);
  verifyFixture(config.localRepositoryPath);

  const runId = crypto.randomUUID().replace(/-/g, '');
  const marker = `LAP_LIVE_PLATFORM_E2E_${runId.toUpperCase()}`;
  const targetName = `lap-live-e2e-${runId}.txt`;
  const targetPath = path.join(config.localRepositoryPath, targetName);
  try {
    fs.lstatSync(targetPath);
    fail(`Generated test target already exists: ${targetName}`);
  } catch (error) {
    if (error && error.code !== 'ENOENT') throw error;
  }
  assertTargetIsNotIgnored(config.localRepositoryPath, targetName);

  const request = createApiClient(config.baseUrl, config.apiKey);
  let sessionId = null;
  let success = false;
  let verificationApproved = false;

  try {
    console.log(`Checking live platform readiness at ${config.baseUrl} ...`);
    await request('/health/ready', { authenticated: false });

    const [models, repositories] = await Promise.all([
      request('/api/models'),
      request('/api/repositories')
    ]);
    if (!Array.isArray(models) || !Array.isArray(repositories)) fail('The live platform returned an invalid model or repository list.');

    const model = models.find(item => item && item.modelId === config.modelId && String(item.providerId).toLowerCase() === 'ollama');
    if (!model) fail(`Model ${config.modelId} is not registered with the live Ollama provider for this API-key owner.`);
    const repository = repositories.find(item => item && String(item.id).toLowerCase() === config.repositoryId.toLowerCase());
    if (!repository) fail('The API key cannot see the configured repository, or its workspace path is outside the server allowlist.');
    if (repository.localPath !== config.expectedServerRepositoryPath) {
      fail(`Repository path mismatch. The API reports ${repository.localPath}; LAP_E2E_SERVER_REPOSITORY_PATH is ${config.expectedServerRepositoryPath}.`);
    }

    console.log(`Starting a real model-backed agent session for disposable repository ${repository.localPath} ...`);
    const created = await request('/api/agent/sessions', {
      method: 'POST',
      body: {
        repositoryId: config.repositoryId,
        modelId: config.modelId,
        userRequest: makeUserRequest(targetName, marker)
      },
      expectedStatuses: [201]
    });
    if (!created || !created.id) fail('The live platform did not return a session ID after creation.');
    sessionId = created.id;
    console.log(`Session ${sessionId} created; polling persisted session/task state through the authenticated API.`);

    const deadline = Date.now() + config.timeoutMs;
    let lastProgress = '';
    let finalSnapshot = null;

    while (Date.now() < deadline) {
      if (interrupted) fail('Interrupted by signal.');
      const snapshot = await fetchSnapshot(request, sessionId);
      if (!snapshot.session || !Array.isArray(snapshot.tasks)) fail('The live platform returned an invalid session snapshot.');
      if (String(snapshot.session.id).toLowerCase() !== String(sessionId).toLowerCase() ||
          String(snapshot.session.repositoryId).toLowerCase() !== config.repositoryId.toLowerCase() ||
          snapshot.session.modelIdUsed !== config.modelId) {
        fail('The live platform returned a session bound to an unexpected ID, repository, or model.');
      }
      const validatedTasks = validateAllTasks(snapshot.tasks, { targetName, marker });

      const progress = `${snapshot.session.state}|${snapshot.tasks.map(task => `${task.toolName || task.type}:${task.status}`).join(',')}`;
      if (progress !== lastProgress) {
        console.log(`State: ${snapshot.session.state || '(unknown)'}${snapshot.tasks.length ? `; tasks: ${snapshot.tasks.map(task => `${task.toolName || task.type}=${task.status}`).join(', ')}` : ''}`);
        lastProgress = progress;
      }

      if (snapshot.session.state === 'Failed' || snapshot.session.state === 'Cancelled') {
        fail(`The live agent session finished in ${snapshot.session.state}: ${snapshot.session.failureReason || 'no failure reason was returned.'}`);
      }

      const awaiting = validatedTasks.filter(entry => entry.task.status === 'AwaitingApproval');
      if (awaiting.length > 0) {
        if (snapshot.session.state !== 'AwaitingApproval') {
          fail('The session has an approval-pending task but is not in AwaitingApproval state; no approval was sent.');
        }
        if (awaiting.length !== 1 || awaiting[0].kind !== 'verification') {
          fail('The live session requested approval for something other than one exact system verification task; no approval was sent.');
        }
        assertWorkspaceState(config.localRepositoryPath, targetName, {
          expectTarget: fs.existsSync(targetPath),
          allowBuildArtifacts: verificationApproved
        });
        if (!fs.existsSync(targetPath)) {
          fail('The platform reached build/test verification before creating the exact marker file; refusing to approve verification.');
        }
        assertTargetContent(config.localRepositoryPath, targetName, marker);
        const entry = awaiting[0];
        console.log(`Approving the exact system-generated ${entry.toolName} task after explicit environment opt-in; no model-planned command is approved.`);
        await request(`/api/agent/sessions/${encodeURIComponent(sessionId)}/approve`, {
          method: 'POST', body: { taskId: entry.task.id }, expectedStatuses: [202]
        });
        verificationApproved = true;
        await delay(config.pollIntervalMs);
        continue;
      }

      if (TERMINAL_STATES.has(snapshot.session.state)) {
        if (snapshot.session.state !== 'Completed') {
          fail(`The live session finished in ${snapshot.session.state}, not Completed.`);
        }
        assertSuccessfulSession(snapshot.session, validatedTasks, config.localRepositoryPath, targetName, marker);
        finalSnapshot = snapshot;
        success = true;
        break;
      }

      // Before any explicit BuildTool approval, there must be no ignored output from
      // the model. After that approval only the fixture's dotnet bin/ and obj/ are
      // expected ignored directories.
      assertWorkspaceState(config.localRepositoryPath, targetName, {
        expectTarget: fs.existsSync(targetPath),
        allowBuildArtifacts: verificationApproved
      });
      if (fs.existsSync(targetPath)) assertTargetContent(config.localRepositoryPath, targetName, marker);
      await delay(config.pollIntervalMs);
    }

    if (!success) fail(`Live end-to-end session did not complete within ${config.timeoutMs / 1000} seconds.`);

    console.log(`PASS: real Ollama-backed session ${sessionId} completed; exact file mutation, BuildTool, TestTool, persisted API polling, and verification outputs were checked.`);
    if (config.keepOutputFile) {
      console.log(`Keeping the verified test output at ${targetPath} because LAP_E2E_KEEP_FILE=1.`);
    } else {
      assertTargetContent(config.localRepositoryPath, targetName, marker);
      assertWorkspaceState(config.localRepositoryPath, targetName, { expectTarget: true, allowBuildArtifacts: true });
      fs.unlinkSync(targetPath);
      assertWorkspaceState(config.localRepositoryPath, targetName, { expectTarget: false, allowBuildArtifacts: true });
      console.log(`Removed the verified one-off marker file ${targetName}; dotnet bin/obj outputs and the persisted platform session are retained.`);
    }
    return { sessionId, modelId: config.modelId, repositoryId: config.repositoryId, finalState: finalSnapshot.session.state };
  } catch (error) {
    if (sessionId && !success) await requestCancellation(request, sessionId);
    throw error;
  }
}

if (require.main === module) {
  process.on('SIGINT', () => { interrupted = true; });
  process.on('SIGTERM', () => { interrupted = true; });
  runLivePlatformE2E().catch(error => {
    const message = error instanceof Error ? error.message : String(error);
    const secret = String(process.env.LAP_E2E_API_KEY || '');
    console.error(`Live platform E2E failed: ${secret ? message.split(secret).join('[REDACTED]') : message}`);
    process.exitCode = interrupted ? 130 : 1;
  });
}

module.exports = {
  ALLOW_BUILD_AND_TEST_VALUE,
  ALLOW_DISPOSABLE_REPOSITORY_VALUE,
  FIXTURE_HASHES,
  assertTargetContent,
  assertWorkspaceState,
  makeUserRequest,
  parseBaseUrl,
  parseTaskArguments,
  readConfig,
  validateAllTasks,
  validateTask,
  verifyFixture
};
