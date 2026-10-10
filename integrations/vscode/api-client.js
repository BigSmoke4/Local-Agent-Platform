'use strict';

class ApiClient {
  constructor(baseUrl, apiKey, fetchImpl = globalThis.fetch) {
    const configuredUrl = String(baseUrl || '').trim();
    this.apiKey = String(apiKey || '').trim();
    this.fetchImpl = fetchImpl;
    if (configuredUrl) {
      let parsed;
      try { parsed = new URL(configuredUrl); }
      catch { throw new Error('localAgentPlatform.baseUrl must be a valid HTTP(S) URL.'); }
      const localHosts = new Set(['localhost', '127.0.0.1', '[::1]']);
      if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password ||
          parsed.search || parsed.hash || parsed.pathname !== '/' ||
          (parsed.protocol === 'http:' && !localHosts.has(parsed.hostname.toLowerCase()))) {
        throw new Error('Use an HTTPS URL, or plain HTTP on localhost/127.0.0.1/::1 only.');
      }
      this.baseUrl = parsed.origin;
    } else {
      this.baseUrl = '';
    }
  }

  async request(path, { method = 'GET', body, signal } = {}) {
    if (!this.baseUrl) throw new Error('Set localAgentPlatform.baseUrl in VS Code settings.');
    if (!this.apiKey) throw new Error('Configure an API key with “Local Agent Platform: Configure API Key”.');
    if (typeof this.fetchImpl !== 'function') throw new Error('This VS Code runtime does not provide HTTP fetch support.');

    const headers = {
      Accept: 'application/json',
      'X-Api-Key': this.apiKey
    };
    // Never forward the API key to a redirect target outside the configured origin.
    const options = { method, headers, signal, redirect: 'error' };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify(body);
    }

    let response;
    let text;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 15000);
    const forwardAbort = () => controller.abort();
    if (signal) {
      if (signal.aborted) controller.abort();
      else signal.addEventListener('abort', forwardAbort, { once: true });
    }
    options.signal = controller.signal;
    try {
      response = await this.fetchImpl(`${this.baseUrl}${path}`, options);
      text = await response.text();
    } catch {
      throw new Error(controller.signal.aborted
        ? 'The Local Agent Platform request timed out or was cancelled.'
        : 'Could not reach the Local Agent Platform. Check its URL and that the local service is running.');
    } finally {
      clearTimeout(timeout);
      if (signal) signal.removeEventListener('abort', forwardAbort);
    }
    let payload = null;
    if (text) {
      try { payload = JSON.parse(text); }
      catch { payload = null; }
    }

    if (response.status === 401 || response.status === 403) {
      throw new Error('The API key was rejected. Configure a valid key in VS Code.');
    }
    if (!response.ok) {
      const detail = payload && typeof payload.error === 'string' ? payload.error : `HTTP ${response.status}`;
      throw new Error(`Local Agent Platform request failed: ${detail}`);
    }
    return payload;
  }

  get(path, signal) { return this.request(path, { signal }); }
  post(path, body, signal) { return this.request(path, { method: 'POST', body, signal }); }
}

module.exports = { ApiClient };
