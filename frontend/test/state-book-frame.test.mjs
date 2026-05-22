// Frontend reducer tests for the new trading-host `book.${symbol}` WS
// channel (Q3.6 Stage B, #286). Runs with
// `node --test frontend/test/state-book-frame.test.mjs`.
//
// Coverage: snapshot replaces both sides, empty snapshot keeps
// ready=false, defensive null guards, watchlist trimming still
// evicts the entry.
//
// IMPORTANT: WebSocketHub serializes outbound frames with
// JsonSerializerDefaults.Web, so the wire payload is camelCase
// (`symbol`, `bids`, `asks`, `updatedUtc`, level fields `price`,
// `totalQty`, `orderCount`). Fixtures here must match the wire
// shape — early test fixtures used PascalCase and silently masked
// a real bug where the reducer dropped every live frame (#382
// follow-up: DOB stayed empty even with EnableBook=true and a
// populated book on the wire).

import { test } from 'node:test';
import assert from 'node:assert/strict';

let n = 0;
async function freshState() {
  n += 1;
  return await import(`../js/state.js?bust-bookframe=${n}`);
}

const frame = (overrides = {}) => ({
  symbol: 'PETR4',
  bids: [
    { price: 30.20, totalQty: 100, orderCount: 1 },
    { price: 30.10, totalQty: 250, orderCount: 3 },
  ],
  asks: [
    { price: 30.30, totalQty: 50, orderCount: 1 },
  ],
  updatedUtc: '2026-05-19T20:00:00Z',
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
  s.applyBookFrame({ symbol: 'VALE3', bids: [], asks: [], updatedUtc: null });
  const entry = s.getState().book.get('VALE3');
  assert.equal(entry.ready, false);
  assert.equal(entry.bids.size, 0);
  assert.equal(entry.asks.size, 0);
});

// #379. Live-but-empty: the trading-host stamps UpdatedUtc on the
// populated → empty edge (and serves it from _lastSent to late
// subscribers) so the FE can tell "MD never spoke" (cold start,
// updatedUtc=null → ready=false → "check MD settings" copy) apart from
// "MD is live, just nothing resting" (updatedUtc=iso → ready=true →
// the renderer falls through to the per-side "empty" muted-cell).
test('applyBookFrame live-empty frame (zero sides + non-null updatedUtc) flips ready=true', async () => {
  const s = await freshState();
  s.applyBookFrame({ symbol: 'ITUB4', bids: [], asks: [], updatedUtc: '2026-05-22T14:00:00Z' });
  const entry = s.getState().book.get('ITUB4');
  assert.equal(entry.ready, true);
  assert.equal(entry.bids.size, 0);
  assert.equal(entry.asks.size, 0);
});

test('applyBookFrame replaces the prior ladder rather than merging', async () => {
  const s = await freshState();
  s.applyBookFrame(frame());
  s.applyBookFrame(frame({
    bids: [{ price: 31.00, totalQty: 999, orderCount: 7 }],
    asks: [],
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
  s.applyBookFrame({ symbol: 42 });
  // Pascal-case shape (pre-#382 wire mismatch) is also rejected so a
  // future serializer regression surfaces in tests instead of silently
  // emptying the DOB.
  s.applyBookFrame({ Symbol: 'PETR4', Bids: [], Asks: [], UpdatedUtc: '2026-05-19T20:00:00Z' });
  assert.equal(s.getState().book.size, 0);
});
