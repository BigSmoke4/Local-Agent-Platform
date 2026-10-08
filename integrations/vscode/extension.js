'use strict';

const vscode = require('vscode');
const { ApiClient } = require('./api-client');
const { getChangedFilePaths } = require('./session-files');
const { listRepositoryFiles, mapServerRepositoryPath, resolveRepositoryFile, resolveRepositoryRoot } = require('./repository-files');

const apiKeySecretName = 'localAgentPlatform.apiKey';
const activeStates = new Set(['Created', 'Understanding', 'Planning', 'Executing', 'AwaitingApproval', 'Verifying', 'Repairing']);
const terminalStates = new Set(['Completed', 'Failed', 'Cancelled']);

class PlatformTreeProvider {
  constructor(context, output) {
    this.context = context;
    this.output = output;
    this.emitter = new vscode.EventEmitter();
    this.onDidChangeTreeData = this.emitter.event;
  }

  dispose() { this.emitter.dispose(); }
  refresh() { this.emitter.fire(undefined); }
  getTreeItem(element) { return element; }

  async client() {
    const apiKey = await this.context.secrets.get(apiKeySecretName);
    const baseUrl = vscode.workspace.getConfiguration('localAgentPlatform').get('baseUrl', 'http://localhost:8080');
    return new ApiClient(baseUrl, apiKey);
  }

  async getChildren(element) {
    let client;
    try {
      client = await this.client();
      if (!client.apiKey) {
        return [new NoticeItem('Configure an API key to connect.', {
          command: 'localAgentPlatform.configureApiKey', title: 'Configure API Key', arguments: []
        })];
      }
      if (element && element.kind === 'changedFilesGroup') {
        return element.paths.map(relativePath => new ChangedFileItem(element.session, relativePath));
      }
      if (element && element.kind === 'session') {
        const tasks = await client.get(`/api/agent/sessions/${encodeURIComponent(element.session.id)}/tasks`);
        if (!Array.isArray(tasks)) return [];
        const changedPaths = getChangedFilePaths(tasks);
        const changedGroup = changedPaths.length > 0 ? [new ChangedFilesGroup(element.session, changedPaths)] : [];
        return [...changedGroup, ...tasks.map(task => new TaskItem(element.session, task))];
      }
      const sessions = await client.get('/api/agent/sessions');
      if (!Array.isArray(sessions) || sessions.length === 0) return [new NoticeItem('No recent agent sessions.')];
      return sessions.map(session => new SessionItem(session));
    } catch (error) {
      return [new NoticeItem(`Could not load platform data: ${safeMessage(error)}`)];
    }
  }
}

class SessionItem extends vscode.TreeItem {
  constructor(session) {
    super(session.userRequest || '(empty request)', vscode.TreeItemCollapsibleState.Collapsed);
    this.kind = 'session';
    this.session = session;
    const age = session.createdAtUtc ? new Date(session.createdAtUtc).toLocaleString() : '';
    this.description = [session.state, age].filter(Boolean).join(' · ');
    this.tooltip = `${session.state || 'Unknown state'}\n${session.failureReason || session.finalSummary || ''}`;
    this.contextValue = activeStates.has(session.state) ? 'lapCancellableSession' : 'lapSession';
    this.command = {
      command: 'localAgentPlatform.showSession',
      title: 'Show Session Summary',
      arguments: [session]
    };
  }
}

class TaskItem extends vscode.TreeItem {
  constructor(session, task) {
    super(task.description || task.type || '(task)', vscode.TreeItemCollapsibleState.None);
    this.kind = 'task';
    this.session = session;
    this.task = task;
    this.description = [task.status, task.toolName].filter(Boolean).join(' · ');
    this.contextValue = task.status === 'AwaitingApproval' ? 'lapApproval' : 'lapTask';
    this.tooltip = task.error || task.description || '';
    this.command = {
      command: 'localAgentPlatform.showTask',
      title: 'Show Task Details',
      arguments: [session, task]
    };
  }
}

class ChangedFilesGroup extends vscode.TreeItem {
  constructor(session, paths) {
    super(`Changed files (${paths.length})`, vscode.TreeItemCollapsibleState.Collapsed);
    this.kind = 'changedFilesGroup';
    this.session = session;
    this.paths = paths;
    this.contextValue = 'lapChangedFilesGroup';
    this.iconPath = new vscode.ThemeIcon('diff-modified');
  }
}

class ChangedFileItem extends vscode.TreeItem {
  constructor(session, relativePath) {
    super(relativePath, vscode.TreeItemCollapsibleState.None);
    this.kind = 'changedFile';
    this.contextValue = 'lapChangedFile';
    this.iconPath = new vscode.ThemeIcon('diff-modified');
    this.command = {
      command: 'localAgentPlatform.openChangedFile',
      title: 'Open Changed File',
      arguments: [session, relativePath]
    };
  }
}

class NoticeItem extends vscode.TreeItem {
  constructor(label, command) {
    super(label, vscode.TreeItemCollapsibleState.None);
    this.contextValue = 'lapNotice';
    if (command) this.command = command;
  }
}

function safeMessage(error) {
  return error instanceof Error ? error.message : 'Unknown error.';
}

function localRepositoryPath(repository) {
  const mappings = vscode.workspace.getConfiguration('localAgentPlatform')
    .get('repositoryPathMappings', []);
  return mapServerRepositoryPath(repository.localPath, mappings);
}

function createOutput() {
  return vscode.window.createOutputChannel('Local Agent Platform');
}

async function withClient(context, action) {
  try {
    const apiKey = await context.secrets.get(apiKeySecretName);
    const baseUrl = vscode.workspace.getConfiguration('localAgentPlatform').get('baseUrl', 'http://localhost:8080');
    const client = new ApiClient(baseUrl, apiKey);
    if (!apiKey) {
      const choice = await vscode.window.showWarningMessage(
        'Configure an API key before connecting to Local Agent Platform.',
        'Configure API Key');
      if (choice) await vscode.commands.executeCommand('localAgentPlatform.configureApiKey');
      return;
    }
    return await action(client);
  } catch (error) {
    vscode.window.showErrorMessage(safeMessage(error));
    return undefined;
  }
}

async function configureApiKey(context, provider) {
  const input = await vscode.window.showInputBox({
    title: 'Local Agent Platform API Key',
    prompt: 'Paste an API key created in the platform web UI. It is stored in VS Code SecretStorage.',
    password: true,
    ignoreFocusOut: true,
    validateInput: value => value && /^lap_[a-f0-9]{48}$/.test(value.trim())
      ? undefined
      : 'Expected a key in the form lap_ followed by 48 hexadecimal characters.'
  });
  if (input === undefined) return;
  await context.secrets.store(apiKeySecretName, input.trim());
  provider.refresh();
  vscode.window.showInformationMessage('Local Agent Platform API key saved in SecretStorage.');
}

async function clearApiKey(context, provider) {
  const existing = await context.secrets.get(apiKeySecretName);
  if (!existing) {
    vscode.window.showInformationMessage('No Local Agent Platform API key is stored.');
    return;
  }
  const decision = await vscode.window.showWarningMessage(
    'Remove the Local Agent Platform API key from VS Code SecretStorage?',
    { modal: true },
    'Clear API Key');
  if (decision !== 'Clear API Key') return;
  await context.secrets.delete(apiKeySecretName);
  provider.refresh();
  vscode.window.showInformationMessage('Stored API key removed.');
}

async function openRepository(context) {
  await withClient(context, async client => {
    const repositories = await client.get('/api/repositories');
    if (!Array.isArray(repositories) || repositories.length === 0) {
      vscode.window.showWarningMessage('No allowed repositories are registered for this account.');
      return;
    }

    const repository = await vscode.window.showQuickPick(repositories.map(item => ({
      label: item.projectName,
      description: item.localPath,
      localPath: item.localPath
    })), { title: 'Open an allowed repository', placeHolder: 'The server returns only repositories inside its configured workspace roots.' });
    if (!repository) return;

    let localRoot;
    try { localRoot = await resolveRepositoryRoot(localRepositoryPath(repository)); }
    catch (error) {
      vscode.window.showWarningMessage(`${safeMessage(error)} Configure localAgentPlatform.repositoryPathMappings if the server path differs from the editor host.`);
      return;
    }

    const decision = await vscode.window.showWarningMessage(
      `Open this repository in a new VS Code window?\n\n${repository.projectName}\nServer: ${repository.localPath}\nVS Code: ${localRoot}`,
      { modal: true },
      'Open Repository');
    if (decision !== 'Open Repository') return;

    await vscode.commands.executeCommand(
      'vscode.openFolder', vscode.Uri.file(localRoot), { forceNewWindow: true });
  });
}

async function openRepositoryFile(context) {
  await withClient(context, async client => {
    const repositories = await client.get('/api/repositories');
    if (!Array.isArray(repositories) || repositories.length === 0) {
      vscode.window.showWarningMessage('No allowed repositories are registered for this account.');
      return;
    }

    const repository = await vscode.window.showQuickPick(repositories.map(item => ({
      label: item.projectName,
      description: item.localPath,
      localPath: item.localPath
    })), { title: 'Choose a repository', placeHolder: 'Choose an allowed repository with a local checkout on this VS Code machine.' });
    if (!repository) return;

    let localPath;
    let files;
    let truncated;
    try {
      localPath = localRepositoryPath(repository);
      ({ files, truncated } = await listRepositoryFiles(localPath));
    } catch (error) {
      vscode.window.showWarningMessage(`${safeMessage(error)} Configure localAgentPlatform.repositoryPathMappings if the server path differs from the editor host.`);
      return;
    }
    if (files.length === 0) {
      vscode.window.showWarningMessage('No regular files were found in that local repository.');
      return;
    }
    if (truncated) {
      vscode.window.showWarningMessage('The repository file list was bounded; generated and linked directories are skipped, and the chooser may be incomplete.');
    }

    const selected = await vscode.window.showQuickPick(files.map(file => ({
      label: file.relativePath,
      relativePath: file.relativePath
    })), {
      title: `Open a file in ${repository.projectName}`,
      placeHolder: 'Search repository-relative paths',
      matchOnDescription: true
    });
    if (!selected) return;

    const fullPath = await resolveRepositoryFile(localPath, selected.relativePath);
    const document = await vscode.workspace.openTextDocument(vscode.Uri.file(fullPath));
    await vscode.window.showTextDocument(document);
  });
}

async function openChangedFile(context, session, relativePath) {
  if (!session || !session.repositoryId || !relativePath) return;
  await withClient(context, async client => {
    const repositories = await client.get('/api/repositories');
    const repository = Array.isArray(repositories)
      ? repositories.find(item => item.id === session.repositoryId)
      : undefined;
    if (!repository) {
      vscode.window.showWarningMessage('The session repository is no longer available to this account.');
      return;
    }

    let localPath;
    let fullPath;
    try {
      localPath = localRepositoryPath(repository);
      fullPath = await resolveRepositoryFile(localPath, relativePath);
    } catch (error) {
      vscode.window.showWarningMessage(`${safeMessage(error)} Check the local repository path mapping and checkout.`);
      return;
    }

    const document = await vscode.workspace.openTextDocument(vscode.Uri.file(fullPath));
    await vscode.window.showTextDocument(document);
  });
}

async function startSession(context, provider) {
  await withClient(context, async client => {
    const [repositories, models] = await Promise.all([
      client.get('/api/repositories'),
      client.get('/api/models')
    ]);
    if (!Array.isArray(repositories) || repositories.length === 0) {
      vscode.window.showWarningMessage('No allowed repositories are registered for this account. Register one in the web UI first.');
      return;
    }
    if (!Array.isArray(models) || models.length === 0) {
      vscode.window.showWarningMessage('No local models are registered. Add a model in the web UI first.');
      return;
    }

    const repository = await vscode.window.showQuickPick(repositories.map(item => ({
      label: item.projectName,
      description: item.localPath,
      repositoryId: item.id
    })), { title: 'Choose a repository', placeHolder: 'Only repositories allowed by the server are listed.' });
    if (!repository) return;

    const model = await vscode.window.showQuickPick(models.map(item => ({
      label: item.name,
      description: `${item.providerId} · ${item.modelId}${item.isDefault ? ' · default' : ''}`,
      modelId: item.modelId
    })), { title: 'Choose a local model' });
    if (!model) return;

    const userRequest = await vscode.window.showInputBox({
      title: 'Coding request',
      prompt: `Request for ${repository.projectName}`,
      placeHolder: 'Describe the change you want the agent to make.',
      ignoreFocusOut: true,
      validateInput: value => value.trim().length > 0 && value.length <= 4000
        ? undefined
        : 'Enter a request of 1–4,000 characters.'
    });
    if (userRequest === undefined) return;

    const session = await client.post('/api/agent/sessions', {
      repositoryId: repository.repositoryId,
      userRequest: userRequest.trim(),
      modelId: model.modelId
    });
    provider.refresh();
    vscode.window.showInformationMessage(`Agent session started (${session.state}).`);
  });
}

function showSession(output, session) {
  if (!session) return;
  output.clear();
  output.appendLine(`Request: ${session.userRequest || ''}`);
  output.appendLine(`State: ${session.state || 'Unknown'}`);
  if (session.createdAtUtc) output.appendLine(`Created: ${new Date(session.createdAtUtc).toLocaleString()}`);
  if (session.failureReason) output.appendLine(`Failure: ${session.failureReason}`);
  output.appendLine('');
  output.appendLine(session.finalSummary || 'No final summary is available yet.');
  output.show(true);
}

function showTask(output, session, task) {
  if (!task) return;
  output.clear();
  output.appendLine(`Session: ${session && session.userRequest || task.agentSessionId || ''}`);
  output.appendLine(`Task: ${task.description || task.type || ''}`);
  output.appendLine(`Status: ${task.status || 'Unknown'}`);
  if (task.toolName) output.appendLine(`Tool: ${task.toolName}`);
  if (task.argumentsJson) output.appendLine(`Arguments: ${task.argumentsJson}`);
  if (task.error) output.appendLine(`Error: ${task.error}`);
  if (task.output) {
    output.appendLine('');
    output.appendLine('Output:');
    output.appendLine(task.output);
  }
  output.show(true);
}

async function approveTask(context, provider, item) {
  if (!item || !item.session || !item.task || item.task.status !== 'AwaitingApproval') return;
  const decision = await vscode.window.showWarningMessage(
    `Approve this one-time tool action? Review the exact tool arguments below.\n\n${item.task.description}\nTool: ${item.task.toolName || 'unspecified'}\nArguments: ${item.task.argumentsJson || '(none)'}`,
    { modal: true },
    'Approve Once');
  if (decision !== 'Approve Once') return;

  await withClient(context, async client => {
    await client.post(`/api/agent/sessions/${encodeURIComponent(item.session.id)}/approve`, { taskId: item.task.id });
    vscode.window.showInformationMessage('Approval queued. The session will resume if the task is still pending.');
    provider.refresh();
  });
}

async function cancelSession(context, provider, item) {
  if (!item || !item.session || terminalStates.has(item.session.state)) return;
  const decision = await vscode.window.showWarningMessage(
    'Cancel this agent session? An active tool process will be asked to stop.',
    { modal: true },
    'Cancel Session');
  if (decision !== 'Cancel Session') return;

  await withClient(context, async client => {
    await client.post(`/api/agent/sessions/${encodeURIComponent(item.session.id)}/cancel`);
    provider.refresh();
  });
}

function activate(context) {
  const output = createOutput();
  const provider = new PlatformTreeProvider(context, output);
  const view = vscode.window.createTreeView('localAgentPlatform.sessions', { treeDataProvider: provider, showCollapseAll: true });
  const refreshTimer = setInterval(() => {
    if (view.visible) provider.refresh();
  }, 15000);

  context.subscriptions.push(
    output,
    provider,
    view,
    { dispose: () => clearInterval(refreshTimer) },
    view.onDidChangeVisibility(event => {
      if (event.visible) provider.refresh();
    }),
    vscode.workspace.onDidChangeConfiguration(event => {
      if (event.affectsConfiguration('localAgentPlatform.baseUrl')) provider.refresh();
    }),
    vscode.commands.registerCommand('localAgentPlatform.configureApiKey', () => configureApiKey(context, provider)),
    vscode.commands.registerCommand('localAgentPlatform.clearApiKey', () => clearApiKey(context, provider)),
    vscode.commands.registerCommand('localAgentPlatform.startSession', () => startSession(context, provider)),
    vscode.commands.registerCommand('localAgentPlatform.openRepository', () => openRepository(context)),
    vscode.commands.registerCommand('localAgentPlatform.openRepositoryFile', () => openRepositoryFile(context)),
    vscode.commands.registerCommand('localAgentPlatform.refreshSessions', () => provider.refresh()),
    vscode.commands.registerCommand('localAgentPlatform.showSession', session => showSession(output, session)),
    vscode.commands.registerCommand('localAgentPlatform.showTask', (session, task) => showTask(output, session, task)),
    vscode.commands.registerCommand('localAgentPlatform.openChangedFile', (session, relativePath) => openChangedFile(context, session, relativePath)),
    vscode.commands.registerCommand('localAgentPlatform.approveTask', item => approveTask(context, provider, item)),
    vscode.commands.registerCommand('localAgentPlatform.cancelSession', item => cancelSession(context, provider, item))
  );

  provider.refresh();
}

function deactivate() {}

module.exports = { activate, deactivate };
