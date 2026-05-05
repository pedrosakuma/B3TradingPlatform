// Trade tape (T4) state reducer tests for frontend/js/state.js.
// Runs with `node --test frontend/test/state-tape.test.mjs`.
//
// Coverage: side inference (up/down/flat), ring buffer cap (TAPE_MAX),
// applyMdTradeBust marks the entry, missing tradeId is a no-op,
// removeMdSymbol drops tape entries, clearAllTape, setTapeSymbol
// normalises empty string to null, setWatchlist drops stale symbols
// + resets tapeSymbol, slice notifications fire.

import { test } from 'node:test';
import assert from 'node:assert/strict';

let n = 0;
async function freshState() {
  n += 1;
  return await import(`../js/state.js?bust=${n}`);
}

test('first trade for a symbol has side=flat', async () => {
  const s = await freshState();
  s.applyMdTrade({ symbol: 'PETR4', price: 32.10, qty: 100, tradeId: 1 });
  const arr = s.getState().tape.get('PETR4');
  assert.equal(arr.length, 1);
  assert.equal(arr[0].side, 'flat');
  assert.equal(arr[0].busted, false);
  assert.equal(arr[0].tradeId, 1);
});

test('side is inferred from the previous trade price', async () => {
  const s = await freshState();
  s.applyMdTrade({ symbol: 'PETR4', price: 32.10, qty: 100, tradeId: 1 });
  s.applyMdTrade({ symbol: 'PETR4', price: 32.15, qty: 100, tradeId: 2 });
  s.applyMdTrade({ symbol: 'PETR4', price: 32.05, qty: 100, tradeId: 3 });
  s.applyMdTrade({ symbol: 'PETR4', price: 32.05, qty: 100, tradeId: 4 });
  const arr = s.getState().tape.get('PETR4');
  assert.deepEqual(arr.map(e => e.side), ['flat', 'up', 'down', 'flat']);
});

test('side inference is per-symbol (independent histories)', async () => {
  const s = await freshState();
  s.applyMdTrade({ symbol: 'PETR4', price: 32.10, qty: 100, tradeId: 1 });
  s.applyMdTrade({ symbol: 'VALE3', price: 65.00, qty: 100, tradeId: 2 });
  const vale = s.getState().tape.get('VALE3');
  assert.equal(vale[0].side, 'flat');
});

test('tape per-symbol ring is capped at TAPE_MAX', async () => {
  const s = await freshState();
  for (let i = 0; i < 250; i++) {
    s.applyMdTrade({ symbol: 'PETR4', price: 32.0 + (i % 5) * 0.01, qty: 100, tradeId: i });
  }
  const arr = s.getState().tape.get('PETR4');
  assert.equal(arr.length, 200);
  // Newest (highest tradeId) must still be the last element.
  assert.equal(arr[arr.length - 1].tradeId, 249);
  // Oldest retained = 250 - 200 = 50.
  assert.equal(arr[0].tradeId, 50);
});

test('applyMdTradeBust marks the matching entry busted', async () => {
  const s = await freshState();
  s.applyMdTrade({ symbol: 'PETR4', price: 32.10, qty: 100, tradeId: 1 });
  s.applyMdTrade({ symbol: 'PETR4', price: 32.20, qty: 100, tradeId: 2 });
  s.applyMdTradeBust({ symbol: 'PETR4', tradeId: 2 });
  const arr = s.getState().tape.get('PETR4');
  assert.equal(arr.find(e => e.tradeId === 2).busted, true);
  assert.equal(arr.find(e => e.tradeId === 1).busted, false);
});

test('applyMdTradeBust is a no-op when tradeId is unknown', async () => {
  const s = await freshState();
  s.applyMdTrade({ symbol: 'PETR4', price: 32.10, qty: 100, tradeId: 1 });
  s.applyMdTradeBust({ symbol: 'PETR4', tradeId: 999 });
  const arr = s.getState().tape.get('PETR4');
  assert.equal(arr[0].busted, false);
});

test('applyMdTradeBust on unknown symbol is a no-op', async () => {
  const s = await freshState();
  s.applyMdTradeBust({ symbol: 'NOPE', tradeId: 1 });
  assert.equal(s.getState().tape.size, 0);
});

test('removeMdSymbol drops tape entries for that symbol', async () => {
  const s = await freshState();
  s.applyMdTrade({ symbol: 'PETR4', price: 32.10, qty: 100, tradeId: 1 });
  s.applyMdTrade({ symbol: 'VALE3', price: 65.00, qty: 100, tradeId: 2 });
  s.removeMdSymbol('PETR4');
  assert.equal(s.getState().tape.has('PETR4'), false);
  assert.equal(s.getState().tape.has('VALE3'), true);
});

test('clearAllTape empties every per-symbol ring', async () => {
  const s = await freshState();
  s.applyMdTrade({ symbol: 'PETR4', price: 32.10, qty: 100, tradeId: 1 });
  s.applyMdTrade({ symbol: 'VALE3', price: 65.00, qty: 100, tradeId: 2 });
  s.clearAllTape();
  assert.equal(s.getState().tape.size, 0);
});

test('setTapeShowAll toggles the tape "all" mode', async () => {
  const s = await freshState();
  assert.equal(s.getState().tapeShowAll, true); // default
  s.setTapeShowAll(false);
  assert.equal(s.getState().tapeShowAll, false);
  s.setTapeShowAll(true);
  assert.equal(s.getState().tapeShowAll, true);
});

test('setTapeSymbol back-compat: empty enables show-all, non-empty disables', async () => {
  const s = await freshState();
  s.setTapeSymbol('PETR4');
  assert.equal(s.getState().selectedSymbol, 'PETR4');
  assert.equal(s.getState().tapeShowAll, false);
  s.setTapeSymbol('');
  assert.equal(s.getState().tapeShowAll, true);
});

test('setWatchlist drops tape for removed symbols and resets selectedSymbol', async () => {
  const s = await freshState();
  s.setWatchlist(['PETR4', 'VALE3']);
  s.applyMdTrade({ symbol: 'PETR4', price: 32.10, qty: 100, tradeId: 1 });
  s.applyMdTrade({ symbol: 'VALE3', price: 65.00, qty: 100, tradeId: 2 });
  s.setSelectedSymbol('PETR4');
  s.setWatchlist(['VALE3']); // PETR4 leaves the watchlist
  assert.equal(s.getState().tape.has('PETR4'), false);
  assert.equal(s.getState().tape.has('VALE3'), true);
  // PETR4 left → auto-pick the only remaining symbol.
  assert.equal(s.getState().selectedSymbol, 'VALE3');
});

test('applyMdTrade emits a tape notification', async () => {
  const s = await freshState();
  let count = 0;
  const off = s.subscribe(slice => { if (slice === 'tape') count += 1; });
  s.applyMdTrade({ symbol: 'PETR4', price: 32.10, qty: 100, tradeId: 1 });
  off();
  assert.equal(count, 1);
});

test('applyMdTradeBust emits a tape notification on a hit', async () => {
  const s = await freshState();
  s.applyMdTrade({ symbol: 'PETR4', price: 32.10, qty: 100, tradeId: 1 });
  let count = 0;
  const off = s.subscribe(slice => { if (slice === 'tape') count += 1; });
  s.applyMdTradeBust({ symbol: 'PETR4', tradeId: 1 });
  s.applyMdTradeBust({ symbol: 'PETR4', tradeId: 999 }); // miss → no notify
  off();
  assert.equal(count, 1);
});
