'use strict';

const assert = require('node:assert/strict');
const vscode = require('vscode');

const extensionId = 'localagentplatform.local-agent-platform-vscode';
const contributedCommands = [
  'localAgentPlatform.configureApiKey',
  'localAgentPlatform.clearApiKey',
  'localAgentPlatform.startSession',
  'localAgentPlatform.openRepository',
  'localAgentPlatform.openRepositoryFile',
  'localAgentPlatform.refreshSessions',
  'localAgentPlatform.showSession',
  'localAgentPlatform.showTask',
  'localAgentPlatform.openChangedFile',
  'localAgentPlatform.approveTask',
  'localAgentPlatform.cancelSession'
];

suite('Local Agent Platform Extension Host', () => {
  test('activates the installed extension and registers its contributed commands', async () => {
    const extension = vscode.extensions.getExtension(extensionId);
    assert.ok(extension, `Extension ${extensionId} must be discoverable in the host.`);

    await extension.activate();
    assert.equal(extension.isActive, true);

    const registeredCommands = new Set(await vscode.commands.getCommands(true));
    for (const command of contributedCommands) {
      assert.ok(registeredCommands.has(command), `Missing registered command: ${command}`);
    }

    const configuration = vscode.workspace.getConfiguration('localAgentPlatform');
    assert.equal(configuration.get('baseUrl'), 'http://localhost:8080');
    assert.deepEqual(configuration.get('repositoryPathMappings'), []);
  });

  test('refresh command is callable after activation without platform credentials', async () => {
    await vscode.commands.executeCommand('localAgentPlatform.refreshSessions');
  });
});
