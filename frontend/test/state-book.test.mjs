// DOB (Depth-of-Book) state reducer tests for frontend/js/state.js.
// Runs with `node --test frontend/test/state-book.test.mjs`.
//
// Coverage: snapshot/level/cleared semantics, ready-gate (incremental
// drops before snapshot), watchlist trimming book + dobSymbol, and
// clearAllBooks. Each test resets the module by re-importing via a
// cache-busting query string; state.js holds module-scope mutable
// state so isolation matters.

import { test } from 'node:test';
import assert from 'node:assert/strict';

let n = 0;
async function freshState() {
  n += 1;
  return await import(`../js/state.js?bust=${n}`);
}

test('book.snapshot marker resets entry to not-ready', async () => {
  const s = await freshState();
  s.applyMdLevelSnapshot({
    symbol: 'PETR4',
    bids: [{ price: 32.50, qty: 100, count: 1 }],
    asks: [{ price: 32.55, qty: 200, count: 2 }],
  });
  assert.equal(s.getState().book.get('PETR4').ready, true);

  s.applyMdBookSnapshot({ symbol: 'PETR4' });
  const entry = s.getState().book.get('PETR4');
  assert.equal(entry.ready, false);
  assert.equal(entry.bids.size, 0);
  assert.equal(entry.asks.size, 0);
});

test('level.snapshot replaces both sides and flips ready=true', async () => {
  const s = await freshState();
  s.applyMdLevelSnapshot({
    symbol: 'VALE3',
    bids: [
      { price: 65.00, qty: 100, count: 1 },
      { price: 64.99, qty: 200, count: 2 },
    ],
    asks: [{ price: 65.05, qty: 300, count: 3 }],
  });
  const e = s.getState().book.get('VALE3');
  assert.equal(e.ready, true);
  assert.equal(e.bids.size, 2);
  assert.equal(e.asks.size, 1);
  assert.deepEqual(e.bids.get('65.0000'), { qty: 100, count: 1 });
  assert.deepEqual(e.asks.get('65.0500'), { qty: 300, count: 3 });
});

test('level.update before snapshot is dropped (ready=false)', async () => {
  const s = await freshState();
  s.applyMdBookSnapshot({ symbol: 'ITUB4' });
  s.applyMdLevelUpdate({ symbol: 'ITUB4', side: 0, price: 30.00, qty: 500, count: 1 });
  const e = s.getState().book.get('ITUB4');
  assert.equal(e.ready, false);
  assert.equal(e.bids.size, 0);
});

test('level.update with valid side after snapshot updates the bucket', async () => {
  const s = await freshState();
  s.applyMdLevelSnapshot({ symbol: 'PETR4', bids: [], asks: [] });
  s.applyMdLevelUpdate({ symbol: 'PETR4', side: 0, price: 32.50, qty: 1000, count: 4 });
  s.applyMdLevelUpdate({ symbol: 'PETR4', side: 1, price: 32.55, qty: 2000, count: 5 });
  const e = s.getState().book.get('PETR4');
  assert.deepEqual(e.bids.get('32.5000'), { qty: 1000, count: 4 });
  assert.deepEqual(e.asks.get('32.5500'), { qty: 2000, count: 5 });
});

test('level.update with invalid side is silently ignored', async () => {
  const s = await freshState();
  s.applyMdLevelSnapshot({ symbol: 'PETR4', bids: [], asks: [] });
  s.applyMdLevelUpdate({ symbol: 'PETR4', side: 99, price: 32.50, qty: 1, count: 1 });
  const e = s.getState().book.get('PETR4');
  assert.equal(e.bids.size, 0);
  assert.equal(e.asks.size, 0);
});

test('level.deleted removes only the targeted side', async () => {
  const s = await freshState();
  s.applyMdLevelSnapshot({
    symbol: 'PETR4',
    bids: [{ price: 32.50, qty: 100, count: 1 }],
    asks: [{ price: 32.55, qty: 200, count: 2 }],
  });
  s.applyMdLevelDeleted({ symbol: 'PETR4', side: 0, price: 32.50 });
  const e = s.getState().book.get('PETR4');
  assert.equal(e.bids.size, 0);
  assert.equal(e.asks.size, 1);
});

test('book.cleared with null clears both sides', async () => {
  const s = await freshState();
  s.applyMdLevelSnapshot({
    symbol: 'PETR4',
    bids: [{ price: 32.50, qty: 100, count: 1 }],
    asks: [{ price: 32.55, qty: 200, count: 2 }],
  });
  s.applyMdBookCleared({ symbol: 'PETR4', side: null });
  const e = s.getState().book.get('PETR4');
  assert.equal(e.bids.size, 0);
  assert.equal(e.asks.size, 0);
});

test('book.cleared with side=BID clears only the bid side', async () => {
  const s = await freshState();
  s.applyMdLevelSnapshot({
    symbol: 'PETR4',
    bids: [{ price: 32.50, qty: 100, count: 1 }],
    asks: [{ price: 32.55, qty: 200, count: 2 }],
  });
  s.applyMdBookCleared({ symbol: 'PETR4', side: 0 });
  const e = s.getState().book.get('PETR4');
  assert.equal(e.bids.size, 0);
  assert.equal(e.asks.size, 1);
});

test('setWatchlist drops books for symbols no longer watched', async () => {
  const s = await freshState();
  s.setWatchlist(['PETR4', 'VALE3']);
  s.applyMdLevelSnapshot({ symbol: 'PETR4', bids: [{ price: 1, qty: 1, count: 1 }], asks: [] });
  s.applyMdLevelSnapshot({ symbol: 'VALE3', bids: [{ price: 2, qty: 2, count: 2 }], asks: [] });
  s.setWatchlist(['PETR4']);
  assert.equal(s.getState().book.has('PETR4'), true);
  assert.equal(s.getState().book.has('VALE3'), false);
});

test('setWatchlist clears dobSymbol if it was removed', async () => {
  const s = await freshState();
  s.setWatchlist(['PETR4', 'VALE3']);
  s.setDobSymbol('VALE3');
  assert.equal(s.getState().dobSymbol, 'VALE3');
  s.setWatchlist(['PETR4']);
  assert.equal(s.getState().dobSymbol, null);
});

test('setWatchlist preserves dobSymbol if still in list', async () => {
  const s = await freshState();
  s.setWatchlist(['PETR4', 'VALE3']);
  s.setDobSymbol('PETR4');
  s.setWatchlist(['PETR4', 'ITUB4']);
  assert.equal(s.getState().dobSymbol, 'PETR4');
});

test('clearAllBooks empties the book Map', async () => {
  const s = await freshState();
  s.applyMdLevelSnapshot({ symbol: 'PETR4', bids: [{ price: 1, qty: 1, count: 1 }], asks: [] });
  s.applyMdLevelSnapshot({ symbol: 'VALE3', bids: [{ price: 2, qty: 2, count: 2 }], asks: [] });
  s.clearAllBooks();
  assert.equal(s.getState().book.size, 0);
});

test('setDobSymbol notifies "dobSymbol" slice', async () => {
  const s = await freshState();
  const seen = [];
  s.subscribe((slice) => seen.push(slice));
  s.setDobSymbol('PETR4');
  assert.ok(seen.includes('dobSymbol'));
});

test('book reducers notify "book" slice', async () => {
  const s = await freshState();
  const seen = [];
  s.subscribe((slice) => seen.push(slice));
  s.applyMdLevelSnapshot({ symbol: 'PETR4', bids: [], asks: [] });
  assert.ok(seen.includes('book'));
});
