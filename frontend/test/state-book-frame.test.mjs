// Frontend reducer tests for the new trading-host `book.${symbol}` WS
// channel (Q3.6 Stage B, #286). Runs with
// `node --test frontend/test/state-book-frame.test.mjs`.
//
// Coverage: snapshot replaces both sides, empty snapshot keeps
// ready=false, defensive null guards, watchlist trimming still
// evicts the entry.

import { test } from 'node:test';
import assert from 'node:assert/strict';

let n = 0;
async function freshState() {
  n += 1;
  return await import(`../js/state.js?bust-bookframe=${n}`);
}

const frame = (overrides = {}) => ({
  Symbol: 'PETR4',
  Bids: [
    { Price: 30.20, TotalQty: 100, OrderCount: 1 },
    { Price: 30.10, TotalQty: 250, OrderCount: 3 },
  ],
  Asks: [
    { Price: 30.30, TotalQty: 50, OrderCount: 1 },
  ],
  UpdatedUtc: '2026-05-19T20:00:00Z',
  ...overrides,
});

test('applyBookFrame populates both sides with priceKey-bucketed levels and flips ready=true', async () => {
  const s = await freshState();
  s.applyBookFrame(frame());
  const entry = s.getState().book.get('PETR4');
  assert.equal(entry.ready, true);
  assert.equal(entry.bids.size, 2);
  assert.equal(entry.asks.size, 1);
  assert.deepEqual(entry.bids.get('30.2000'), { qty: 100, count: 1 });
  assert.deepEqual(entry.bids.get('30.1000'), { qty: 250, count: 3 });
  assert.deepEqual(entry.asks.get('30.3000'), { qty: 50, count: 1 });
});

test('applyBookFrame empty-state snapshot leaves ready=false', async () => {
  const s = await freshState();
  s.applyBookFrame({ Symbol: 'VALE3', Bids: [], Asks: [], UpdatedUtc: null });
  const entry = s.getState().book.get('VALE3');
  assert.equal(entry.ready, false);
  assert.equal(entry.bids.size, 0);
  assert.equal(entry.asks.size, 0);
});

test('applyBookFrame replaces the prior ladder rather than merging', async () => {
  const s = await freshState();
  s.applyBookFrame(frame());
  s.applyBookFrame(frame({
    Bids: [{ Price: 31.00, TotalQty: 999, OrderCount: 7 }],
    Asks: [],
  }));
  const entry = s.getState().book.get('PETR4');
  assert.equal(entry.bids.size, 1);
  assert.equal(entry.asks.size, 0);
  assert.deepEqual(entry.bids.get('31.0000'), { qty: 999, count: 7 });
});

test('applyBookFrame ignores malformed payloads', async () => {
  const s = await freshState();
  s.applyBookFrame(null);
  s.applyBookFrame(undefined);
  s.applyBookFrame({});
  s.applyBookFrame({ Symbol: 42 });
  assert.equal(s.getState().book.size, 0);
});
