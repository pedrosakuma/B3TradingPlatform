// Fase 2 (#398). Algos state slice reducers.
// Run: `node --test frontend/test/algos-state.test.mjs`.

import { test } from 'node:test';
import assert from 'node:assert/strict';

let n = 0;
async function freshState() {
  n += 1;
  return await import(`../js/state.js?bust-algos=${n}`);
}

function dto(id, overrides = {}) {
  return {
    algoId: String(id),
    symbol: 'PETR4',
    securityId: 1,
    side: 'Buy',
    type: 'Iceberg',
    totalQuantity: 1000,
    filledQuantity: 0,
    remainingQuantity: 1000,
    status: 'Running',
    terminalReason: 'None',
    createdAtUtc: '2026-01-01T13:00:00Z',
    terminalAtUtc: null,
    iceberg: { displayQuantity: 100, limitPrice: 30.50 },
    twap: null, vwap: null, pov: null, pegged: null,
    ...overrides,
  };
}

test('applyAlgoSnapshot replaces wholesale and keys by algoId', async () => {
  const s = await freshState();
  s.applyAlgoSnapshot([dto(1), dto(2)]);
  assert.equal(s.getState().algos.size, 2);
  assert.equal(s.getState().algos.get('1').symbol, 'PETR4');
  // Replacing — items not present in the second snapshot are dropped.
  s.applyAlgoSnapshot([dto(3)]);
  assert.equal(s.getState().algos.size, 1);
  assert.ok(s.getState().algos.has('3'));
  assert.ok(!s.getState().algos.has('1'));
});

test('applyAlgoSnapshot tolerates non-array/null', async () => {
  const s = await freshState();
  s.applyAlgoSnapshot(null);
  assert.equal(s.getState().algos.size, 0);
  s.applyAlgoSnapshot(undefined);
  assert.equal(s.getState().algos.size, 0);
});

test('applyAlgoDelta upserts by algoId', async () => {
  const s = await freshState();
  s.applyAlgoSnapshot([dto(1)]);
  s.applyAlgoDelta(dto(1, { filledQuantity: 250, remainingQuantity: 750 }));
  assert.equal(s.getState().algos.get('1').filledQuantity, 250);
  s.applyAlgoDelta(dto(7));
  assert.equal(s.getState().algos.size, 2);
});

test('applyAlgoDelta no-ops on malformed rows', async () => {
  const s = await freshState();
  s.applyAlgoDelta(null);
  s.applyAlgoDelta({});
  s.applyAlgoDelta({ algoId: 42 }); // not a string
  assert.equal(s.getState().algos.size, 0);
});

test('isTerminalAlgoStatus matches the backend terminal enum', async () => {
  const s = await freshState();
  for (const t of ['Completed', 'Cancelled', 'Rejected', 'Expired', 'Failed']) {
    assert.ok(s.isTerminalAlgoStatus(t), `${t} should be terminal`);
  }
  for (const t of ['Running', 'Cancelling', 'PendingNew', 'New']) {
    assert.ok(!s.isTerminalAlgoStatus(t), `${t} should not be terminal`);
  }
});

test('clearAlgos drops the slice + selection + inflight sets', async () => {
  const s = await freshState();
  s.applyAlgoSnapshot([dto(1)]);
  s.setSelectedAlgoId('1');
  s.markAlgoCancelInflight('1', true);
  s.markAlgoModifyInflight('1', true);
  s.clearAlgos();
  assert.equal(s.getState().algos.size, 0);
  assert.equal(s.getState().selectedAlgoId, null);
  assert.equal(s.getState().inflightAlgoCancels.size, 0);
  assert.equal(s.getState().inflightAlgoModifies.size, 0);
});

test('clearAll() at a session boundary drops algos too', async () => {
  const s = await freshState();
  s.applyAlgoSnapshot([dto(1), dto(2)]);
  s.setSelectedAlgoId('1');
  s.markAlgoCancelInflight('1', true);
  s.clearAll();
  assert.equal(s.getState().algos.size, 0);
  assert.equal(s.getState().selectedAlgoId, null);
  assert.equal(s.getState().inflightAlgoCancels.size, 0);
});

test('subscribe() fires "algos" slice on snapshot/delta', async () => {
  const s = await freshState();
  const seen = [];
  s.subscribe((slice) => seen.push(slice));
  s.applyAlgoSnapshot([dto(1)]);
  s.applyAlgoDelta(dto(2));
  assert.ok(seen.includes('algos'));
});
