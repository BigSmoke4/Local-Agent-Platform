'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const Module = require('node:module');

const state = {
  apiKey: `lap_${'b'.repeat(48)}`,
  baseUrl: 'http://localhost:8080',
  pathMappings: [],
  handlers: new Map(),
  treeProvider: null,
  requests: [],
  quickPickResults: [],
  warningResults: [],
  inputResults: [],
  messages: [],
  openedDocuments: [],
  shownDocuments: [],
  executedCommands: []
};

function disposable() { return { dispose() {} }; }
class FakeEventEmitter {
  constructor() { this.listeners = new Set(); }
  event = listener => { this.listeners.add(listener); return disposable(); };
  fire(value) { for (const listener of this.listeners) listener(value); }
  dispose() { this.listeners.clear(); }
}
class FakeTreeItem {
  constructor(label, collapsibleState) { this.label = label; this.collapsibleState = collapsibleState; }
}

const fakeVscode = {
  EventEmitter: FakeEventEmitter,
  TreeItem: FakeTreeItem,
  ThemeIcon: class FakeThemeIcon { constructor(id) { this.id = id; } },
  TreeItemCollapsibleState: { None: 0, Collapsed: 1 },
  Uri: { file: filePath => ({ fsPath: filePath, path: filePath }) },
  workspace: {
    getConfiguration: () => ({
      get: (key, fallback) => key === 'baseUrl' ? state.baseUrl
        : key === 'repositoryPathMappings' ? state.pathMappings
          : fallback
    }),
    onDidChangeConfiguration: () => disposable(),
    openTextDocument: async uri => { state.openedDocuments.push(uri); return { uri }; }
  },
  window: {
    createOutputChannel: () => ({ clear() {}, appendLine() {}, show() {}, dispose() {} }),
    createTreeView: (_id, options) => {
      state.treeProvider = options.treeDataProvider;
      return {
        visible: true,
        onDidChangeVisibility: () => disposable(),
        dispose() {}
      };
    },
    showQuickPick: async items => {
      const next = state.quickPickResults.shift();
      if (typeof next === 'function') return next(items);
      if (typeof next === 'number') return items[next];
      return next;
    },
    showInputBox: async () => state.inputResults.shift(),
    showWarningMessage: async (...args) => {
      state.messages.push({ type: 'warning', args });
      return state.warningResults.shift();
    },
    showInformationMessage: async (...args) => state.messages.push({ type: 'info', args }),
    showErrorMessage: async (...args) => state.messages.push({ type: 'error', args }),
    showTextDocument: async document => { state.shownDocuments.push(document); }
  },
  commands: {
    registerCommand: (id, handler) => {
      state.handlers.set(id, handler);
      return disposable();
    },
    executeCommand: async (...args) => { state.executedCommands.push(args); }
  }
};

const originalLoad = Module._load;
Module._load = function(request, parent, isMain) {
  if (request === 'vscode') return fakeVscode;
  return originalLoad.call(this, request, parent, isMain);
};
const { activate } = require('../extension');
Module._load = originalLoad;

function response(payload, status = 200) {
  return new Response(payload === undefined ? '' : JSON.stringify(payload), { status });
}

function resetState() {
  state.handlers.clear();
  state.treeProvider = null;
  state.requests = [];
  state.quickPickResults = [];
  state.warningResults = [];
  state.inputResults = [];
  state.messages = [];
  state.openedDocuments = [];
  state.shownDocuments = [];
  state.executedCommands = [];
  state.baseUrl = 'http://localhost:8080';
  state.pathMappings = [];
  globalThis.fetch = async (url, options) => {
    const parsed = new URL(url);
    state.requests.push({ url: parsed.pathname, options });
    if (parsed.pathname === '/api/repositories') {
      return response([{ id: 'repo-1', projectName: 'Example', localPath: '/local/example' }]);
    }
    if (parsed.pathname === '/api/models') {
      return response([{ id: 'model-registration-1', providerId: 'ollama', modelId: 'qwen-local', name: 'Qwen Local', isDefault: true }]);
    }
    if (parsed.pathname === '/api/agent/sessions' && options.method === 'POST') {
      return response({ id: 'session-1', state: 'Created' }, 201);
    }
    if (/\/approve$/.test(parsed.pathname) || /\/cancel$/.test(parsed.pathname)) return response(undefined, 202);
    throw new Error(`Unexpected test route ${options.method} ${parsed.pathname}`);
  };
}

function activateForTest() {
  const context = {
    subscriptions: [],
    secrets: {
      get: async key => key === 'localAgentPlatform.apiKey' ? state.apiKey : undefined,
      store: async () => {},
      delete: async () => {}
    }
  };
  activate(context);
  return context;
}

function disposeContext(context) {
  for (const subscription of context.subscriptions) subscription.dispose();
}

test('start-session command posts the selected repository, model, and user request', async t => {
  resetState();
  const context = activateForTest();
  t.after(() => disposeContext(context));
  state.quickPickResults.push(0, 0);
  state.inputResults.push('Add a regression test');

  await state.handlers.get('localAgentPlatform.startSession')();

  const request = state.requests.find(item => item.url === '/api/agent/sessions' && item.options.method === 'POST');
  assert.ok(request);
  assert.equal(request.options.headers['X-Api-Key'], state.apiKey);
  assert.deepEqual(JSON.parse(request.options.body), {
    repositoryId: 'repo-1', modelId: 'qwen-local', userRequest: 'Add a regression test'
  });
  assert.equal(state.messages.some(message => message.type === 'error'), false);
});

test('open-file command maps a server container path to a safe local checkout', async t => {
  resetState();
  const mappingRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'lap-vscode-command-'));
  const root = path.join(mappingRoot, 'example');
  t.after(() => fs.rm(mappingRoot, { recursive: true, force: true }));
  await fs.mkdir(path.join(root, 'src'), { recursive: true });
  await fs.writeFile(path.join(root, 'src', 'Program.cs'), 'class Program {}');
  globalThis.fetch = async (url, options) => {
    const parsed = new URL(url);
    state.requests.push({ url: parsed.pathname, options });
    if (parsed.pathname === '/api/repositories') {
      return response([{ id: 'repo-1', projectName: 'Example', localPath: '/container/workspace/example' }]);
    }
    throw new Error(`Unexpected test route ${parsed.pathname}`);
  };
  state.pathMappings = [{ serverRoot: '/container/workspace', localRoot: mappingRoot }];
  const context = activateForTest();
  t.after(() => disposeContext(context));
  state.quickPickResults.push(0, items => items.find(item => item.label === 'src/Program.cs'));

  await state.handlers.get('localAgentPlatform.openRepositoryFile')();

  assert.equal(state.openedDocuments.length, 1, JSON.stringify(state.messages));
  assert.equal(state.openedDocuments[0].fsPath, path.join(root, 'src', 'Program.cs'));
  assert.equal(state.shownDocuments.length, 1);
});

test('changed-file group exposes completed file writes and opens the mapped file', async t => {
  resetState();
  const mappingRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'lap-vscode-changed-'));
  const root = path.join(mappingRoot, 'example');
  t.after(() => fs.rm(mappingRoot, { recursive: true, force: true }));
  await fs.mkdir(path.join(root, 'src'), { recursive: true });
  await fs.writeFile(path.join(root, 'src', 'Changed.cs'), 'class Changed {}');
  state.pathMappings = [{ serverRoot: '/container/workspace', localRoot: mappingRoot }];
  globalThis.fetch = async (url, options) => {
    const parsed = new URL(url);
    state.requests.push({ url: parsed.pathname, options });
    if (parsed.pathname === '/api/agent/sessions/session-1/tasks') {
      return response([{
        id: 'task-1', orderIndex: 0, type: 'ToolCall', description: 'Update file',
        toolName: 'FileEditTool', argumentsJson: '{"path":"src/Changed.cs"}',
        status: 'Completed', output: null, error: null, retryCount: 0
      }]);
    }
    if (parsed.pathname === '/api/repositories') {
      return response([{ id: 'repo-1', projectName: 'Example', localPath: '/container/workspace/example' }]);
    }
    throw new Error(`Unexpected test route ${parsed.pathname}`);
  };
  const context = activateForTest();
  t.after(() => disposeContext(context));
  const session = { id: 'session-1', repositoryId: 'repo-1', state: 'Completed', userRequest: 'Update file' };

  const children = await state.treeProvider.getChildren({ kind: 'session', session });
  assert.equal(children[0].label, 'Changed files (1)');
  const [changedFile] = await state.treeProvider.getChildren(children[0]);
  assert.equal(changedFile.label, 'src/Changed.cs');
  await state.handlers.get(changedFile.command.command)(...changedFile.command.arguments);

  assert.equal(state.openedDocuments[0].fsPath, path.join(root, 'src', 'Changed.cs'));
});

test('open-repository command confirms then opens only a checked local repository path', async t => {
  resetState();
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'lap-vscode-open-root-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  globalThis.fetch = async (url, options) => {
    const parsed = new URL(url);
    state.requests.push({ url: parsed.pathname, options });
    if (parsed.pathname === '/api/repositories') {
      return response([{ id: 'repo-1', projectName: 'Example', localPath: root }]);
    }
    throw new Error(`Unexpected test route ${parsed.pathname}`);
  };
  const context = activateForTest();
  t.after(() => disposeContext(context));
  state.quickPickResults.push(0);
  state.warningResults.push('Open Repository');

  await state.handlers.get('localAgentPlatform.openRepository')();

  assert.deepEqual(state.executedCommands[0], [
    'vscode.openFolder', { fsPath: root, path: root }, { forceNewWindow: true }
  ], JSON.stringify(state.messages));
});

test('approval command shows the exact arguments and sends the one-time task approval', async t => {
  resetState();
  const context = activateForTest();
  t.after(() => disposeContext(context));
  state.warningResults.push('Approve Once');
  const item = {
    session: { id: 'session-1' },
    task: { id: 'task-1', status: 'AwaitingApproval', description: 'Run tests', toolName: 'TerminalTool', argumentsJson: '{"command":"dotnet test"}' }
  };

  await state.handlers.get('localAgentPlatform.approveTask')(item);

  const confirmation = state.messages.find(message => message.type === 'warning');
  assert.match(confirmation.args[0], /\{"command":"dotnet test"\}/);
  const request = state.requests.find(entry => /\/approve$/.test(entry.url));
  assert.ok(request);
  assert.deepEqual(JSON.parse(request.options.body), { taskId: 'task-1' });
});

test('cancel command confirms and sends a request for a nonterminal session', async t => {
  resetState();
  const context = activateForTest();
  t.after(() => disposeContext(context));
  state.warningResults.push('Cancel Session');

  await state.handlers.get('localAgentPlatform.cancelSession')({ session: { id: 'session-1', state: 'Executing' } });

  assert.ok(state.requests.some(entry => /\/cancel$/.test(entry.url)));
});
