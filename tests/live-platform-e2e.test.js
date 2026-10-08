'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');

const e2e = require('../scripts/run-live-platform-e2e');
const fixtureSource = path.resolve(__dirname, 'fixtures/live-platform-e2e');

function git(repositoryPath, args, extraEnv = {}) {
  const result = spawnSync('git', ['-C', repositoryPath, ...args], {
    encoding: 'utf8',
    env: { ...process.env, ...extraEnv },
    windowsHide: true
  });
  assert.equal(result.status, 0, result.stderr || result.stdout);
}

function makeFixtureWorktree(t) {
  const repositoryPath = fs.mkdtempSync(path.join(os.tmpdir(), 'lap-live-e2e-fixture-'));
  for (const name of fs.readdirSync(fixtureSource)) {
    fs.copyFileSync(path.join(fixtureSource, name), path.join(repositoryPath, name));
  }
  git(repositoryPath, ['init', '--quiet']);
  git(repositoryPath, ['add', '--all']);
  git(repositoryPath, ['-c', 'user.name=E2E Fixture', '-c', 'user.email=e2e-fixture@example.invalid', 'commit', '--quiet', '-m', 'Prepare disposable E2E fixture']);
  t.after(() => fs.rmSync(repositoryPath, { recursive: true, force: true }));
  return repositoryPath;
}

function fileWriteTask(targetName, marker, extra = {}) {
  return {
    id: 'task-file-write',
    type: 'ToolCall',
    toolName: 'FileWriteTool',
    argumentsJson: JSON.stringify({ path: targetName, content: marker, expectedHash: 'NEW' }),
    status: 'Pending',
    ...extra
  };
}

test('accepts loopback HTTP and rejects remote cleartext or credential-bearing platform URLs', () => {
  assert.equal(e2e.parseBaseUrl('http://localhost:8080/'), 'http://localhost:8080');
  assert.equal(e2e.parseBaseUrl('https://platform.example.test/'), 'https://platform.example.test');
  assert.throws(() => e2e.parseBaseUrl('http://platform.example.test/'), /Plain HTTP is allowed only/);
  assert.throws(() => e2e.parseBaseUrl('https://user:pass@platform.example.test/'), /origin-only/);
  assert.throws(() => e2e.parseBaseUrl('https://platform.example.test/app'), /origin-only/);
});

test('requires explicit disposable-repository and build/test approvals before any API request', () => {
  const baseEnv = {
    LAP_E2E_BASE_URL: 'http://localhost:8080',
    LAP_E2E_API_KEY: 'lap_test_not_a_real_key',
    LAP_E2E_REPOSITORY_ID: '01234567-89ab-4cde-8f01-23456789abcd',
    LAP_E2E_SERVER_REPOSITORY_PATH: '/workspace/e2e',
    LAP_E2E_LOCAL_REPOSITORY_PATH: os.tmpdir(),
    LAP_E2E_MODEL_ID: 'test-model:1b'
  };
  assert.throws(() => e2e.readConfig(baseEnv), /LAP_E2E_CONFIRM_DISPOSABLE_REPOSITORY/);
  assert.throws(() => e2e.readConfig({
    ...baseEnv,
    LAP_E2E_CONFIRM_DISPOSABLE_REPOSITORY: e2e.ALLOW_DISPOSABLE_REPOSITORY_VALUE
  }), /LAP_E2E_ALLOW_BUILD_AND_TEST/);
  assert.doesNotThrow(() => e2e.readConfig({
    ...baseEnv,
    LAP_E2E_CONFIRM_DISPOSABLE_REPOSITORY: e2e.ALLOW_DISPOSABLE_REPOSITORY_VALUE,
    LAP_E2E_ALLOW_BUILD_AND_TEST: e2e.ALLOW_BUILD_AND_TEST_VALUE
  }));
});

test('pins the disposable fixture contents and requires a clean Git worktree', t => {
  const repositoryPath = makeFixtureWorktree(t);
  assert.equal(e2e.verifyFixture(repositoryPath), true);
  fs.appendFileSync(path.join(repositoryPath, 'DisposableTests.cs'), '\n// unexpected change\n');
  assert.throws(() => e2e.verifyFixture(repositoryPath), /completely clean|differs from the reviewed test fixture/);
});

test('permits only one exact, new marker-file write and read-only fixture inspection', () => {
  const targetName = 'lap-live-e2e-0123456789abcdef.txt';
  const marker = 'LAP_LIVE_PLATFORM_E2E_0123456789ABCDEF';
  const context = { targetName, marker };
  assert.equal(e2e.validateTask(fileWriteTask(targetName, marker), context).kind, 'write');
  assert.equal(e2e.validateTask({ type: 'ToolCall', toolName: 'FileReadTool', argumentsJson: '{"path":"Disposable.csproj"}' }, context).kind, 'read');
  assert.equal(e2e.validateTask({ type: 'ToolCall', toolName: 'DirectoryListTool', argumentsJson: '{"path":"."}' }, context).kind, 'list');
  assert.throws(() => e2e.validateTask(fileWriteTask('../outside.txt', marker), context), /other than the unique/);
  assert.throws(() => e2e.validateTask(fileWriteTask(targetName, 'different content'), context), /other than the unique/);
  assert.throws(() => e2e.validateTask({ type: 'ToolCall', toolName: 'TerminalTool', argumentsJson: '{"command":"echo unsafe"}' }, context), /Only fixture reads\/listing/);
  assert.throws(() => e2e.validateTask({ type: 'ToolCall', toolName: 'FileEditTool', argumentsJson: '{}' }, context), /Only fixture reads\/listing/);
});

test('only recognizes the exact system-generated verification tasks for explicit approval', () => {
  const build = {
    type: 'Verification', toolName: 'BuildTool', argumentsJson: '{}',
    description: 'Run an approved workspace build for verification. Build targets can execute repository code.',
    status: 'AwaitingApproval'
  };
  const testTask = {
    type: 'Verification', toolName: 'TestTool', argumentsJson: '{}',
    description: 'Run approved workspace tests for verification. Tests execute repository code.',
    status: 'AwaitingApproval'
  };
  assert.equal(e2e.validateTask(build, {}).toolName, 'BuildTool');
  assert.equal(e2e.validateTask(testTask, {}).toolName, 'TestTool');
  assert.throws(() => e2e.validateTask({ ...build, type: 'ToolCall' }, {}), /Refusing to run or approve/);
  assert.throws(() => e2e.validateTask({ ...build, argumentsJson: '{"target":"."}' }, {}), /unexpected verification task/);
  assert.throws(() => e2e.validateTask({ ...build, description: 'Model-planned build task' }, {}), /unexpected verification task/);
});

test('detects workspace mutations and verifies/removes no unexpected target content', t => {
  const repositoryPath = makeFixtureWorktree(t);
  const targetName = 'lap-live-e2e-test-output.txt';
  const marker = 'LAP_LIVE_PLATFORM_E2E_TEST';
  e2e.assertWorkspaceState(repositoryPath, targetName, { expectTarget: false, allowBuildArtifacts: false });
  fs.writeFileSync(path.join(repositoryPath, targetName), `${marker}\n`);
  e2e.assertTargetContent(repositoryPath, targetName, marker);
  e2e.assertWorkspaceState(repositoryPath, targetName, { expectTarget: true, allowBuildArtifacts: false });
  fs.mkdirSync(path.join(repositoryPath, 'bin'));
  fs.mkdirSync(path.join(repositoryPath, 'obj'));
  e2e.assertWorkspaceState(repositoryPath, targetName, { expectTarget: true, allowBuildArtifacts: true });
  fs.writeFileSync(path.join(repositoryPath, 'unexpected.txt'), 'unexpected');
  assert.throws(() => e2e.assertWorkspaceState(repositoryPath, targetName, { expectTarget: true, allowBuildArtifacts: false }), /changed unexpectedly/);
});

test('constructs a bounded, unique-file request without authorizing unrelated commands', () => {
  const request = e2e.makeUserRequest('lap-live-e2e-deadbeef.txt', 'LAP_LIVE_PLATFORM_E2E_DEADBEEF');
  assert.match(request, /expectedHash NEW/);
  assert.match(request, /Do not edit or create any other file/);
  assert.match(request, /do not run commands/);
});
