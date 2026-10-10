'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { ApiClient } = require('../api-client');

const apiKey = `lap_${'a'.repeat(48)}`;

test('sends owner API requests with the API key and JSON body', async () => {
  let captured;
  const client = new ApiClient('http://localhost:8080/', apiKey, async (url, options) => {
    captured = { url, options };
    return new Response(JSON.stringify({ id: 'session-1' }), {
      status: 201,
      headers: { 'content-type': 'application/json' }
    });
  });

  const body = { repositoryId: 'repo-1', userRequest: 'Fix the bug', modelId: 'local-model' };
  const result = await client.post('/api/agent/sessions', body);

  assert.equal(captured.url, 'http://localhost:8080/api/agent/sessions');
  assert.equal(captured.options.headers['X-Api-Key'], apiKey);
  assert.equal(captured.options.headers['Content-Type'], 'application/json');
  assert.equal(captured.options.redirect, 'error');
  assert.deepEqual(JSON.parse(captured.options.body), body);
  assert.deepEqual(result, { id: 'session-1' });
});

test('refuses to send an API key over plain HTTP to a non-loopback host', () => {
  assert.throws(() => new ApiClient('http://192.168.1.8:8080', apiKey, async () => new Response()), /HTTPS URL/);
});

test('allows HTTPS for an explicitly configured remote or reverse-proxy endpoint', () => {
  const client = new ApiClient('https://agent.example.test', apiKey, async () => new Response('[]'));
  assert.equal(client.baseUrl, 'https://agent.example.test');
});

test('does not include the API key in authentication error messages', async () => {
  const client = new ApiClient('http://127.0.0.1:8080', apiKey, async () => new Response('', { status: 401 }));

  await assert.rejects(client.get('/api/models'), error => {
    assert.match(error.message, /API key was rejected/);
    assert.equal(error.message.includes(apiKey), false);
    return true;
  });
});

test('does not make a request if no API key is configured', async () => {
  let called = false;
  const client = new ApiClient('http://localhost:8080', '', async () => { called = true; return new Response('[]'); });

  await assert.rejects(client.get('/api/models'), /Configure an API key/);
  assert.equal(called, false);
});
