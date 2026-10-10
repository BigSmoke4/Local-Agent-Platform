'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');

const ignoredDirectoryNames = new Set([
  '.git', 'bin', 'obj', 'node_modules', '.vs', '.idea', 'dist', 'build', '.vscode',
  '.venv', 'venv', 'env', '.tox', '.next', '.nuxt', '.svelte-kit', '.vite', 'coverage',
  'target', 'vendor', '.terraform', '.gradle', '.mypy_cache', '.ruff_cache', '__pycache__'
]);
const maxOpenFileBytes = 10 * 1024 * 1024;

function isWithin(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative === '' || (relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative));
}

function serverPathApi(value) {
  if (path.posix.isAbsolute(value)) return path.posix;
  if (path.win32.isAbsolute(value)) return path.win32;
  return null;
}

function mapServerRepositoryPath(serverPath, mappings = []) {
  if (typeof serverPath !== 'string' || serverPath.includes(String.fromCharCode(0))) {
    throw new Error('Server repository path must be absolute.');
  }
  const sourceApi = serverPathApi(serverPath);
  if (!sourceApi) throw new Error('Server repository path must be absolute.');
  if (!Array.isArray(mappings)) throw new Error('Repository path mappings must be an array.');

  const sourcePath = sourceApi.normalize(serverPath);
  const validMappings = mappings.map(mapping => {
    if (!mapping || typeof mapping.serverRoot !== 'string' || typeof mapping.localRoot !== 'string' ||
        !serverPathApi(mapping.serverRoot) || !path.isAbsolute(mapping.localRoot) ||
        mapping.serverRoot.includes(String.fromCharCode(0)) || mapping.localRoot.includes(String.fromCharCode(0))) {
      throw new Error('Each repository path mapping needs an absolute serverRoot and localRoot.');
    }
    const mappingApi = serverPathApi(mapping.serverRoot);
    return {
      pathApi: mappingApi,
      serverRoot: mappingApi.normalize(mapping.serverRoot),
      localRoot: path.resolve(mapping.localRoot)
    };
  }).sort((left, right) => right.serverRoot.length - left.serverRoot.length);

  for (const mapping of validMappings) {
    if (mapping.pathApi !== sourceApi) continue;
    const relative = sourceApi.relative(mapping.serverRoot, sourcePath);
    if (relative === '..' || relative.startsWith(`..${sourceApi.sep}`) || sourceApi.isAbsolute(relative)) continue;
    const segments = relative.split(/[\\/]+/).filter(Boolean);
    const mappedPath = path.resolve(mapping.localRoot, ...segments);
    if (!isWithin(mapping.localRoot, mappedPath)) throw new Error('Mapped repository path escapes its localRoot.');
    return mappedPath;
  }
  return serverPath;
}

async function resolveRepositoryRoot(localPath, fileSystem = fs) {
  if (typeof localPath !== 'string' || localPath.trim() === '') {
    throw new Error('Repository path is missing.');
  }
  let rootInfo;
  try { rootInfo = await fileSystem.lstat(localPath); }
  catch { throw new Error('Repository path is not available on this VS Code machine.'); }
  if (!rootInfo.isDirectory() || rootInfo.isSymbolicLink()) {
    throw new Error('Repository path is not a regular local directory.');
  }
  try { return await fileSystem.realpath(localPath); }
  catch { throw new Error('Repository path could not be resolved safely.'); }
}

async function listRepositoryFiles(localPath, {
  maxFiles = 5000,
  maxEntries = 50000,
  maxDepth = 32,
  fileSystem = fs
} = {}) {
  const root = await resolveRepositoryRoot(localPath, fileSystem);
  const pending = [{ directory: root, depth: 0 }];
  const files = [];
  let entriesVisited = 0;
  let truncated = false;
  let stop = false;

  while (pending.length > 0 && !stop) {
    const current = pending.pop();
    let directory;
    try { directory = await fileSystem.opendir(current.directory); }
    catch { throw new Error(`Cannot enumerate repository directory: ${path.relative(root, current.directory) || '.'}`); }

    try {
      for await (const entry of directory) {
        entriesVisited++;
        if (entriesVisited > maxEntries) {
          truncated = true;
          stop = true;
          break;
        }

        if (entry.isSymbolicLink()) continue;
        const fullPath = path.join(current.directory, entry.name);
        if (entry.isDirectory()) {
          if (ignoredDirectoryNames.has(entry.name.toLowerCase())) continue;
          if (current.depth >= maxDepth) {
            truncated = true;
            continue;
          }
          pending.push({ directory: fullPath, depth: current.depth + 1 });
          continue;
        }
        if (!entry.isFile()) continue;
        if (files.length >= maxFiles) {
          truncated = true;
          stop = true;
          break;
        }
        files.push({ relativePath: path.relative(root, fullPath).split(path.sep).join('/') });
      }
    } catch (error) {
      if (error instanceof Error && error.message.startsWith('Cannot enumerate repository directory:')) throw error;
      throw new Error(`Repository enumeration was interrupted: ${path.relative(root, current.directory) || '.'}`);
    } finally {
      try { await directory.close(); } catch { /* for-await closes completed directories */ }
    }
  }

  files.sort((left, right) => left.relativePath < right.relativePath ? -1 : left.relativePath > right.relativePath ? 1 : 0);
  return { files, truncated };
}

async function resolveRepositoryFile(localPath, relativePath, fileSystem = fs) {
  const root = await resolveRepositoryRoot(localPath, fileSystem);
  if (typeof relativePath !== 'string' || relativePath.trim() === '' || path.isAbsolute(relativePath) ||
      /^[a-zA-Z]:[\\/]/.test(relativePath) || relativePath.startsWith('\\\\')) {
    throw new Error('A relative repository file path is required.');
  }

  let candidate;
  try { candidate = path.resolve(root, relativePath); }
  catch { throw new Error('File path could not be resolved safely.'); }
  if (!isWithin(root, candidate) || candidate === root) {
    throw new Error('File path is outside the repository.');
  }

  let fileInfo;
  try { fileInfo = await fileSystem.lstat(candidate); }
  catch { throw new Error('File is no longer available in the repository.'); }
  if (!fileInfo.isFile() || fileInfo.isSymbolicLink()) {
    throw new Error('Only regular, non-linked files can be opened.');
  }
  if (fileInfo.size > maxOpenFileBytes) {
    throw new Error('Files larger than 10 MiB are not opened by the extension.');
  }

  let canonicalFile;
  try { canonicalFile = await fileSystem.realpath(candidate); }
  catch { throw new Error('File path could not be resolved safely.'); }
  if (!isWithin(root, canonicalFile)) throw new Error('File path resolves outside the repository.');
  return canonicalFile;
}

module.exports = { listRepositoryFiles, mapServerRepositoryPath, resolveRepositoryFile, resolveRepositoryRoot };
