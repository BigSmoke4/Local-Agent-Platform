'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { getChangedFilePaths, normalizeRelativePath } = require('../session-files');

test('derives unique changed-file paths only from completed write/edit tasks', () => {
  const tasks = [
    { status: 'Completed', toolName: 'FileWriteTool', argumentsJson: '{"path":"src\\\\App.cs"}' },
    { status: 'Completed', toolName: 'FileEditTool', argumentsJson: '{"path":"src/App.cs"}' },
    { status: 'Failed', toolName: 'FileEditTool', argumentsJson: '{"path":"src/Failed.cs"}' },
    { status: 'AwaitingApproval', toolName: 'FileWriteTool', argumentsJson: '{"path":"src/Pending.cs"}' },
    { status: 'Completed', toolName: 'FileReadTool', argumentsJson: '{"path":"src/ReadOnly.cs"}' },
    { status: 'Completed', toolName: 'FileWriteTool', argumentsJson: 'not-json' }
  ];

  assert.deepEqual(getChangedFilePaths(tasks), ['src/App.cs']);
});

test('rejects absolute and traversal paths in persisted task metadata', () => {
  assert.equal(normalizeRelativePath('../outside.txt'), null);
  assert.equal(normalizeRelativePath('/etc/passwd'), null);
  assert.equal(normalizeRelativePath('C:\\outside.txt'), null);
  assert.equal(normalizeRelativePath('src/../outside.txt'), null);
  assert.equal(normalizeRelativePath('src\\App.cs'), 'src/App.cs');
});
