'use strict';

const Mocha = require('mocha');
const path = require('node:path');

function run() {
  const mocha = new Mocha({ ui: 'tdd', color: true, timeout: 10000 });
  mocha.addFile(path.resolve(__dirname, 'extension-host.test.js'));

  return new Promise((resolve, reject) => {
    mocha.run(failures => {
      if (failures > 0) reject(new Error(`${failures} Extension Host test(s) failed.`));
      else resolve();
    });
  });
}

module.exports = { run };
