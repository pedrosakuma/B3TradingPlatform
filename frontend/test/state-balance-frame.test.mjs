// Frontend reducer tests for the new `balance.me` WS channel
// (issue #385, fed by #386 fan-out). Runs with
// `node --test frontend/test/state-balance-frame.test.mjs`.
//
// Coverage: snapshot + delta share the same reducer; both casing
// variants (`Available` PascalCase vs `available` camelCase) supported
// since STJ defaults to PascalCase but a future client-cased shape
// shouldn't break the widget; null/garbage frames are no-ops; clearAll
// (logout / WS reconnect) drops the slice so the next session can't
// inherit it.

import { test } from 'node:test';
import assert from 'node:assert/strict';

let n = 0;
async function freshState() {
  n += 1;
  return await import(`../js/state.js?bust-balance=${n}`);
}

test('applyBalanceFrame stores Available from a PascalCase wire frame', async () => {
  const s = await freshState();
  s.applyBalanceFrame({ Available: 1234.56 });
  assert.deepEqual(s.getState().balance, { available: 1234.56 });
});

test('applyBalanceFrame accepts camelCase `available` too', async () => {
  const s = await freshState();
  s.applyBalanceFrame({ available: 99.99 });
  assert.deepEqual(s.getState().balance, { available: 99.99 });
});

test('applyBalanceFrame supports string-encoded decimals (defensive)', async () => {
  const s = await freshState();
  s.applyBalanceFrame({ Available: '500.25' });
  assert.deepEqual(s.getState().balance, { available: 500.25 });
});

test('applyBalanceFrame treats null / missing / NaN as a no-op (keeps prior value)', async () => {
  const s = await freshState();
  s.applyBalanceFrame({ Available: 100 });
  s.applyBalanceFrame(null);
  s.applyBalanceFrame({ Available: null });
  s.applyBalanceFrame({ Available: 'not-a-number' });
  assert.deepEqual(s.getState().balance, { available: 100 });
});

test('applyBalanceFrame replaces wholesale on each frame (snapshot + delta share shape)', async () => {
  const s = await freshState();
  s.applyBalanceFrame({ Available: 100 });
  s.applyBalanceFrame({ Available: 87.66 }); // post-fee delta
  s.applyBalanceFrame({ Available: 0 });
  assert.deepEqual(s.getState().balance, { available: 0 });
});

test('clearAll drops the balance slice (logout / WS reconnect boundary)', async () => {
  const s = await freshState();
  s.applyBalanceFrame({ Available: 7500 });
  s.clearAll();
  assert.equal(s.getState().balance, null);
});

test('applyBalanceFrame notifies the "balance" slice', async () => {
  const s = await freshState();
  const slices = [];
  s.subscribe((slice) => slices.push(slice));
  s.applyBalanceFrame({ Available: 1 });
  assert.ok(slices.includes('balance'),
    `expected "balance" notification, got: ${JSON.stringify(slices)}`);
});
