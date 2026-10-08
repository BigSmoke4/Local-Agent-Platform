'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const extensionRoot = path.resolve(__dirname, '..');
const manifest = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.json'), 'utf8'));
const source = fs.readFileSync(path.join(extensionRoot, manifest.main), 'utf8');

test('manifest points to a real extension entry point and session view', () => {
  assert.equal(fs.existsSync(path.join(extensionRoot, manifest.main)), true);
  assert.ok(manifest.engines.vscode);
  assert.ok(manifest.contributes.views.explorer.some(view => view.id === 'localAgentPlatform.sessions'));
  assert.ok(manifest.activationEvents.includes('onView:localAgentPlatform.sessions'));
});

test('every contributed command is registered by the extension', () => {
  const registered = new Set([...source.matchAll(/registerCommand\(['"]([^'"]+)/g)].map(match => match[1]));
  const contributed = manifest.contributes.commands.map(command => command.command);
  assert.ok(contributed.length > 0);
  for (const command of contributed) assert.ok(registered.has(command), `missing command handler: ${command}`);
});

test('extension host development configuration is present and valid', () => {
  const launchPath = path.join(extensionRoot, '.vscode', 'launch.json');
  const launch = JSON.parse(fs.readFileSync(launchPath, 'utf8'));
  assert.ok(launch.configurations.some(configuration => configuration.type === 'extensionHost'));
});

test('package metadata includes repository and matching standalone MIT license', () => {
  const rootLicense = fs.readFileSync(path.resolve(extensionRoot, '..', '..', 'LICENSE'), 'utf8');
  const extensionLicense = fs.readFileSync(path.join(extensionRoot, 'LICENSE'), 'utf8');
  assert.equal(manifest.license, 'MIT');
  assert.equal(manifest.repository.url, 'https://github.com/BigSmoke4/Local-Agent-Platform.git');
  assert.equal(extensionLicense, rootLicense);
});
