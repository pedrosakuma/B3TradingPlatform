// Tests for frontend/js/protocol.js's defaultMarketDataUrl() (#572):
// deploy-time window.__B3_CONFIG__.marketDataWsUrl override, falling back
// to the localhost dev guess, then "". No deps — runs with
// `node --test frontend/test/market-data-url.test.mjs` (Node 18+).
//
// protocol.js reads the bare `location` global (browser convention), which
// Node doesn't provide, so we stub it on globalThis for the duration of
// each test and restore it afterwards.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { defaultMarketDataUrl } from '../js/protocol.js';

function withLocation(loc, fn) {
  const prevLocation = globalThis.location;
  const prevWindow = globalThis.window;
  globalThis.location = loc;
  try {
    return fn();
  } finally {
    globalThis.location = prevLocation;
    globalThis.window = prevWindow;
  }
}

test('prefers window.__B3_CONFIG__.marketDataWsUrl when set, even off-localhost', () => {
  withLocation({ protocol: 'https:', hostname: 'trader.example.com' }, () => {
    globalThis.window = { __B3_CONFIG__: { marketDataWsUrl: 'wss://10.0.0.5:8080/ws' } };
    assert.equal(defaultMarketDataUrl(), 'wss://10.0.0.5:8080/ws');
  });
});

test('ignores an empty-string configured value and falls back', () => {
  withLocation({ protocol: 'http:', hostname: 'localhost' }, () => {
    globalThis.window = { __B3_CONFIG__: { marketDataWsUrl: '' } };
    assert.equal(defaultMarketDataUrl(), 'ws://localhost:8081/ws');
  });
});

test('falls back to the localhost dev guess when no config is present', () => {
  withLocation({ protocol: 'http:', hostname: '127.0.0.1' }, () => {
    globalThis.window = {};
    assert.equal(defaultMarketDataUrl(), 'ws://127.0.0.1:8081/ws');
  });
});

test('falls back to "" off-localhost with no config', () => {
  withLocation({ protocol: 'https:', hostname: 'trader.example.com' }, () => {
    globalThis.window = {};
    assert.equal(defaultMarketDataUrl(), '');
  });
});

test('uses wss scheme for the localhost guess under https', () => {
  withLocation({ protocol: 'https:', hostname: 'localhost' }, () => {
    globalThis.window = {};
    assert.equal(defaultMarketDataUrl(), 'wss://localhost:8081/ws');
  });
});
