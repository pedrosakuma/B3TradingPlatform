// Candle (T3) state reducer tests for frontend/js/state.js.
// Runs with `node --test frontend/test/state-candle.test.mjs`.
//
// Coverage: multi-frame snapshot ready-gate, FIRST/LAST flag handling,
// mid-stream join (no FIRST) stays not-ready, update replace-vs-append,
// MAX_BARS cap, watchlist trim + chartSymbol auto-pick, resolution
// validation.

import { test } from 'node:test';
import assert from 'node:assert/strict';

let n = 0;
async function freshState() {
  n += 1;
  return await import(`../js/state.js?bust=${n}`);
}

const RES = 60;

function bar(time, o, h, l, c, vol = 100) {
  return { time, open: o, high: h, low: l, close: c, volume: vol, avg: (h + l) / 2 };
}

test('single-frame snapshot (FIRST|LAST) flips ready immediately', async () => {
  const s = await freshState();
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES,
    candles: [bar(1000, 32.0, 32.5, 31.9, 32.4)],
    isFirst: true, isLast: true,
  });
  const e = s.getState().candles.get('PETR4').get(RES);
  assert.equal(e.ready, true);
  assert.equal(e.bars.length, 1);
});

test('multi-frame snapshot stays not-ready until LAST', async () => {
  const s = await freshState();
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES,
    candles: [bar(1000, 32, 33, 31, 32.5)],
    isFirst: true, isLast: false,
  });
  let e = s.getState().candles.get('PETR4').get(RES);
  assert.equal(e.ready, false);
  assert.equal(e.bars.length, 1);

  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES,
    candles: [bar(1060, 32.5, 33.5, 32, 33)],
    isFirst: false, isLast: false,
  });
  e = s.getState().candles.get('PETR4').get(RES);
  assert.equal(e.ready, false);
  assert.equal(e.bars.length, 2);

  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES,
    candles: [bar(1120, 33, 34, 33, 33.8)],
    isFirst: false, isLast: true,
  });
  e = s.getState().candles.get('PETR4').get(RES);
  assert.equal(e.ready, true);
  assert.equal(e.bars.length, 3);
});

test('mid-stream join (no FIRST seen) stays not-ready even on LAST', async () => {
  const s = await freshState();
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES,
    candles: [bar(1000, 32, 33, 31, 32.5)],
    isFirst: false, isLast: true,
  });
  const e = s.getState().candles.get('PETR4').get(RES);
  assert.equal(e.ready, false);
  assert.equal(e.bars.length, 1);
});

test('FIRST after partial sequence resets accumulation', async () => {
  const s = await freshState();
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES,
    candles: [bar(1000, 1, 1, 1, 1), bar(1060, 2, 2, 2, 2)],
    isFirst: true, isLast: false,
  });
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES,
    candles: [bar(2000, 9, 9, 9, 9)],
    isFirst: true, isLast: true,
  });
  const e = s.getState().candles.get('PETR4').get(RES);
  assert.equal(e.bars.length, 1);
  assert.equal(e.bars[0].time, 2000);
  assert.equal(e.ready, true);
});

test('candle.update before ready is dropped', async () => {
  const s = await freshState();
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES,
    candles: [bar(1000, 1, 1, 1, 1)],
    isFirst: true, isLast: false,
  });
  s.applyMdCandleUpdate({
    symbol: 'PETR4', resolution: RES,
    candle: bar(1060, 2, 2, 2, 2),
  });
  const e = s.getState().candles.get('PETR4').get(RES);
  assert.equal(e.bars.length, 1); // update ignored
});

test('candle.update with same time replaces last bar', async () => {
  const s = await freshState();
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES,
    candles: [bar(1000, 32, 32.5, 31.9, 32.2)],
    isFirst: true, isLast: true,
  });
  s.applyMdCandleUpdate({
    symbol: 'PETR4', resolution: RES,
    candle: bar(1000, 32, 32.8, 31.9, 32.7, 250),
  });
  const e = s.getState().candles.get('PETR4').get(RES);
  assert.equal(e.bars.length, 1);
  assert.equal(e.bars[0].close, 32.7);
  assert.equal(e.bars[0].volume, 250);
});

test('candle.update with new time appends a new bar', async () => {
  const s = await freshState();
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES,
    candles: [bar(1000, 32, 32.5, 31.9, 32.2)],
    isFirst: true, isLast: true,
  });
  s.applyMdCandleUpdate({
    symbol: 'PETR4', resolution: RES,
    candle: bar(1060, 32.2, 32.6, 32.1, 32.5),
  });
  const e = s.getState().candles.get('PETR4').get(RES);
  assert.equal(e.bars.length, 2);
  assert.equal(e.bars[1].time, 1060);
});

test('multi-resolution caching for same symbol', async () => {
  const s = await freshState();
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: 60,
    candles: [bar(1000, 1, 1, 1, 1)],
    isFirst: true, isLast: true,
  });
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: 300,
    candles: [bar(1000, 5, 5, 5, 5)],
    isFirst: true, isLast: true,
  });
  const perRes = s.getState().candles.get('PETR4');
  assert.equal(perRes.size, 2);
  assert.equal(perRes.get(60).bars[0].close, 1);
  assert.equal(perRes.get(300).bars[0].close, 5);
});

test('setWatchlist drops candles for removed symbols', async () => {
  const s = await freshState();
  s.setWatchlist(['PETR4', 'VALE3']);
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES, candles: [bar(1, 1, 1, 1, 1)],
    isFirst: true, isLast: true,
  });
  s.applyMdCandleSnapshot({
    symbol: 'VALE3', resolution: RES, candles: [bar(1, 2, 2, 2, 2)],
    isFirst: true, isLast: true,
  });
  s.setWatchlist(['PETR4']);
  assert.equal(s.getState().candles.has('PETR4'), true);
  assert.equal(s.getState().candles.has('VALE3'), false);
});

test('setWatchlist auto-picks first symbol as selectedSymbol when empty', async () => {
  const s = await freshState();
  assert.equal(s.getState().selectedSymbol, null);
  s.setWatchlist(['PETR4', 'VALE3']);
  assert.equal(s.getState().selectedSymbol, 'PETR4');
});

test('setWatchlist clears selectedSymbol if it was removed', async () => {
  const s = await freshState();
  s.setWatchlist(['PETR4', 'VALE3']);
  s.setSelectedSymbol('VALE3');
  s.setWatchlist(['PETR4']);
  // selectedSymbol cleared because VALE3 left, then auto-picked first.
  assert.equal(s.getState().selectedSymbol, 'PETR4');
});

test('setChartResolution rejects unsupported values', async () => {
  const s = await freshState();
  s.setChartResolution(60);
  assert.equal(s.getState().chartResolution, 60);
  s.setChartResolution(45); // not in CHART_RESOLUTIONS
  assert.equal(s.getState().chartResolution, 60);
  s.setChartResolution(900);
  assert.equal(s.getState().chartResolution, 900);
});

test('clearAllCandles wipes all symbols/resolutions', async () => {
  const s = await freshState();
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES, candles: [bar(1, 1, 1, 1, 1)],
    isFirst: true, isLast: true,
  });
  s.clearAllCandles();
  assert.equal(s.getState().candles.size, 0);
});

test('removeCandlesSymbol drops only that symbol', async () => {
  const s = await freshState();
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES, candles: [bar(1, 1, 1, 1, 1)],
    isFirst: true, isLast: true,
  });
  s.applyMdCandleSnapshot({
    symbol: 'VALE3', resolution: RES, candles: [bar(1, 2, 2, 2, 2)],
    isFirst: true, isLast: true,
  });
  s.removeCandlesSymbol('PETR4');
  assert.equal(s.getState().candles.has('PETR4'), false);
  assert.equal(s.getState().candles.has('VALE3'), true);
});

test('snapshot append also honours MAX_BARS cap', async () => {
  const s = await freshState();
  // First batch with 400 bars.
  const first = Array.from({ length: 400 }, (_, i) => bar(i, 1, 1, 1, 1));
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES, candles: first,
    isFirst: true, isLast: false,
  });
  // Second batch pushes total over 600 (MAX_BARS).
  const second = Array.from({ length: 400 }, (_, i) => bar(400 + i, 2, 2, 2, 2));
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES, candles: second,
    isFirst: false, isLast: true,
  });
  const e = s.getState().candles.get('PETR4').get(RES);
  assert.equal(e.bars.length, 600);
  // Newest bars retained.
  assert.equal(e.bars[e.bars.length - 1].time, 799);
});

test('candles reducers notify "candles" slice', async () => {
  const s = await freshState();
  const seen = [];
  s.subscribe((slice) => seen.push(slice));
  s.applyMdCandleSnapshot({
    symbol: 'PETR4', resolution: RES, candles: [bar(1, 1, 1, 1, 1)],
    isFirst: true, isLast: true,
  });
  assert.ok(seen.includes('candles'));
});

test('setChartSymbol back-compat shim writes selectedSymbol', async () => {
  const s = await freshState();
  const seen = [];
  s.subscribe((slice) => seen.push(slice));
  s.setChartSymbol('PETR4');
  assert.equal(s.getState().selectedSymbol, 'PETR4');
  assert.ok(seen.includes('selectedSymbol'));
});
