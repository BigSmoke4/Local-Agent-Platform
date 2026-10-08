'use strict';

function normalizeRelativePath(value) {
  if (typeof value !== 'string' || value.trim() === '') return null;
  const normalized = value.trim().replace(/\\/g, '/');
  if (normalized.length > 1024 || normalized.startsWith('/') || /^[a-zA-Z]:/.test(normalized) || normalized.startsWith('//')) return null;
  const segments = normalized.split('/').filter(segment => segment !== '' && segment !== '.');
  if (segments.length === 0 || segments.some(segment => segment === '..')) return null;
  return segments.join('/');
}

function getChangedFilePaths(tasks) {
  const paths = new Set();
  if (!Array.isArray(tasks)) return [];
  for (const task of tasks) {
    if (task.status !== 'Completed' || !['FileWriteTool', 'FileEditTool'].includes(task.toolName)) continue;
    if (typeof task.argumentsJson !== 'string' || task.argumentsJson.length > 200_000) continue;
    try {
      const args = JSON.parse(task.argumentsJson);
      const relativePath = normalizeRelativePath(args && args.path);
      if (relativePath) paths.add(relativePath);
    } catch {
      // Corrupt persisted task metadata is not a usable changed-file reference.
    }
  }
  return [...paths].sort((left, right) => left < right ? -1 : left > right ? 1 : 0);
}

module.exports = { getChangedFilePaths, normalizeRelativePath };
