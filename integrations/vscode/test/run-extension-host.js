'use strict';

const path = require('node:path');
const { runTests } = require('@vscode/test-electron');

const extensionRoot = path.resolve(__dirname, '..');

runTests({
  extensionDevelopmentPath: extensionRoot,
  extensionTestsPath: path.resolve(extensionRoot, 'test', 'extension-host-suite'),
  launchArgs: [
    '--disable-gpu',
    '--no-sandbox',
    '--disable-dev-shm-usage'
  ]
}).catch(error => {
  console.error('VS Code Extension Host test run failed:', error);
  process.exitCode = 1;
});
