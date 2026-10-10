'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { listRepositoryFiles, mapServerRepositoryPath, resolveRepositoryFile } = require('../repository-files');

async function makeTempDirectory(t, prefix) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), prefix));
  t.after(() => fs.rm(directory, { recursive: true, force: true }));
  return directory;
}

test('maps server roots using longest path-boundary match on POSIX and Windows paths', async t => {
  const root = await makeTempDirectory(t, 'lap-vscode-map-');
  const nested = path.join(root, 'nested');
  await fs.mkdir(nested);
  const mappings = [
    { serverRoot: '/workspace', localRoot: root },
    { serverRoot: '/workspace/project', localRoot: nested }
  ];

  assert.equal(mapServerRepositoryPath('/workspace/project/src', mappings), path.join(nested, 'src'));
  assert.equal(mapServerRepositoryPath('/workspace-other/project', mappings), '/workspace-other/project');
  assert.equal(mapServerRepositoryPath(String.raw`C:\workspace\repo`, [
    { serverRoot: String.raw`C:\workspace`, localRoot: root }
  ]), path.join(root, 'repo'));
  assert.throws(() => mapServerRepositoryPath('/workspace/repo', [
    { serverRoot: 'relative', localRoot: root }
  ]), /absolute serverRoot and localRoot/);
});

test('lists regular repository files while skipping generated and linked directories', async t => {
  const root = await makeTempDirectory(t, 'lap-vscode-files-');
  const outside = await makeTempDirectory(t, 'lap-vscode-outside-');
  await fs.mkdir(path.join(root, 'src'));
  await fs.mkdir(path.join(root, 'node_modules'));
  await fs.writeFile(path.join(root, 'src', 'Program.cs'), 'class Program {}');
  await fs.writeFile(path.join(root, 'node_modules', 'ignored.js'), 'ignored');
  await fs.writeFile(path.join(outside, 'secret.txt'), 'outside');
  await fs.symlink(outside, path.join(root, 'linked'), 'dir');

  const result = await listRepositoryFiles(root);
  assert.deepEqual(result.files.map(file => file.relativePath), ['src/Program.cs']);
  assert.equal(result.truncated, false);
});

test('bounds the file list and reports incomplete results', async t => {
  const root = await makeTempDirectory(t, 'lap-vscode-bound-');
  await fs.writeFile(path.join(root, 'a.txt'), 'a');
  await fs.writeFile(path.join(root, 'b.txt'), 'b');

  const result = await listRepositoryFiles(root, { maxFiles: 1 });
  assert.equal(result.files.length, 1);
  assert.equal(result.truncated, true);
});

test('resolves only regular files canonically contained by the repository', async t => {
  const root = await makeTempDirectory(t, 'lap-vscode-resolve-');
  const outside = await makeTempDirectory(t, 'lap-vscode-outside-');
  await fs.mkdir(path.join(root, 'src'));
  await fs.writeFile(path.join(root, 'src', 'Program.cs'), 'class Program {}');
  await fs.writeFile(path.join(outside, 'secret.txt'), 'outside');
  await fs.symlink(path.join(outside, 'secret.txt'), path.join(root, 'linked.txt'), 'file');

  const resolved = await resolveRepositoryFile(root, 'src/Program.cs');
  assert.equal(resolved, path.join(root, 'src', 'Program.cs'));
  await assert.rejects(resolveRepositoryFile(root, '../lap-vscode-outside/secret.txt'), /outside the repository/);
  await assert.rejects(resolveRepositoryFile(root, 'linked.txt'), /regular, non-linked files/);
});

test('rejects files larger than the editor-open limit', async t => {
  const root = await makeTempDirectory(t, 'lap-vscode-large-');
  const largeFile = path.join(root, 'large.txt');
  await fs.writeFile(largeFile, '');
  await fs.truncate(largeFile, 10 * 1024 * 1024 + 1);

  await assert.rejects(resolveRepositoryFile(root, 'large.txt'), /larger than 10 MiB/);
});

test('rejects a linked repository root', async t => {
  const actual = await makeTempDirectory(t, 'lap-vscode-root-');
  const parent = await makeTempDirectory(t, 'lap-vscode-root-link-');
  const linkedRoot = path.join(parent, 'repo');
  await fs.symlink(actual, linkedRoot, 'dir');

  await assert.rejects(listRepositoryFiles(linkedRoot), /regular local directory/);
});
