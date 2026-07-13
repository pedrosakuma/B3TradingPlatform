// Standalone decoder tests for frontend/js/mdProtocol.js. No deps —
// runs with `node --test frontend/test/decoder.test.mjs` (Node 18+).
//
// Coverage: every MSG.* the parser handles, including the v2 additions
// (BOOK_*, LEVEL_*, CANDLE_*) introduced in issue #67. Each test builds
// a wire-shaped buffer matching the layout documented in mdProtocol.js
// and asserts the decoded object shape, types, and price scaling.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  MSG,
  FLAGS,
  CANDLE_FLAGS,
  SIDE,
  buildSubscribe,
  buildUnsubscribe,
  parseFrames,
} from '../js/mdProtocol.js';

// ── helpers ──────────────────────────────────────────────────────────

function frame(type, payloadBytes) {
  // v2 header = 8 bytes (len LE u32, type LE u16, headerFlags LE u16);
  // body = payloadBytes
  const total = 8 + payloadBytes.length;
  const buf = new ArrayBuffer(total);
  const v = new DataView(buf);
  v.setUint32(0, total, true);
  v.setUint16(4, type, true);
  v.setUint16(6, 0, true); // headerFlags reserved
  new Uint8Array(buf, 8).set(payloadBytes);
  return new Uint8Array(buf);
}

function pack(...parts) {
  // concat Uint8Arrays
  const total = parts.reduce((n, p) => n + p.length, 0);
  const out = new Uint8Array(total);
  let off = 0;
  for (const p of parts) { out.set(p, off); off += p.length; }
  return out;
}

function u8(n) { return new Uint8Array([n & 0xff]); }
function u16(n) {
  const b = new Uint8Array(2);
  new DataView(b.buffer).setUint16(0, n, true);
  return b;
}
function u32(n) {
  const b = new Uint8Array(4);
  new DataView(b.buffer).setUint32(0, n, true);
  return b;
}
function i64(n) {
  const b = new Uint8Array(8);
  new DataView(b.buffer).setBigInt64(0, BigInt(n), true);
  return b;
}
function u64(n) {
  const b = new Uint8Array(8);
  new DataView(b.buffer).setBigUint64(0, BigInt(n), true);
  return b;
}
function strLen8(s) {
  // u8 length prefix + utf8 bytes
  const bytes = new TextEncoder().encode(s);
  return pack(u8(bytes.length), bytes);
}

function parseSingle(frameBytes) {
  // parseFrames takes ArrayBuffer; strip Uint8Array view safely.
  const ab = frameBytes.buffer.slice(
    frameBytes.byteOffset,
    frameBytes.byteOffset + frameBytes.byteLength
  );
  const events = parseFrames(ab);
  assert.equal(events.length, 1, 'expected exactly one decoded event');
  return events[0];
}

// ── flag/constant exports ────────────────────────────────────────────

test('FLAGS exposes BOOK and MBP for issue #67', () => {
  assert.equal(FLAGS.BOOK, 0x01);
  assert.equal(FLAGS.INFO, 0x02);
  assert.equal(FLAGS.MBP, 0x08);
  assert.equal(FLAGS.TRADES, 0x10);
});

test('CANDLE_FLAGS exposes FIRST and LAST', () => {
  assert.equal(CANDLE_FLAGS.FIRST, 0x01);
  assert.equal(CANDLE_FLAGS.LAST, 0x02);
});

test('SIDE.BID is 0 and SIDE.ASK is 1', () => {
  assert.equal(SIDE.BID, 0);
  assert.equal(SIDE.ASK, 1);
});

// ── builders ─────────────────────────────────────────────────────────

test('buildSubscribe defaults to TRADES|INFO', () => {
  const buf = buildSubscribe('PETR4');
  const v = new DataView(buf);
  assert.equal(v.getUint32(0, true), buf.byteLength); // total length
  assert.equal(v.getUint16(4, true), MSG.SUBSCRIBE);
  assert.equal(v.getUint16(6, true), 0); // headerFlags reserved
  assert.equal(v.getUint32(8, true), FLAGS.TRADES | FLAGS.INFO);
  assert.equal(v.getUint8(12), 5); // 'PETR4' length
});

test('buildSubscribe accepts custom flags (e.g. INFO|TRADES|MBP)', () => {
  const buf = buildSubscribe('VALE3', FLAGS.TRADES | FLAGS.INFO | FLAGS.MBP);
  const v = new DataView(buf);
  assert.equal(v.getUint32(8, true), FLAGS.TRADES | FLAGS.INFO | FLAGS.MBP);
});

test('buildSubscribe normalises symbol to uppercase', () => {
  const buf = buildSubscribe('  petr4  ');
  const sym = new TextDecoder().decode(new Uint8Array(buf, 13));
  assert.equal(sym, 'PETR4');
});

test('buildUnsubscribe encodes 16-byte frame with securityId', () => {
  const buf = buildUnsubscribe(900_000_000_001n);
  assert.equal(buf.byteLength, 16);
  const v = new DataView(buf);
  assert.equal(v.getUint16(4, true), MSG.UNSUBSCRIBE);
  assert.equal(v.getBigUint64(8, true), 900_000_000_001n);
});

// ── existing frames (regression) ─────────────────────────────────────

test('ServerStatus(ready=1) decodes', () => {
  const ev = parseSingle(frame(MSG.SERVER_STATUS, u8(1)));
  assert.deepEqual(ev, { type: 'ServerStatus', ready: true });
});

test('SubscribeOk decodes securityId, flags, symbol', () => {
  const ev = parseSingle(frame(
    MSG.SUBSCRIBE_OK,
    pack(u64(900_000_000_003n), u32(FLAGS.TRADES | FLAGS.INFO), strLen8('ITUB4'))
  ));
  assert.equal(ev.type, 'SubscribeOk');
  assert.equal(ev.securityId, 900_000_000_003n);
  assert.equal(ev.flags, FLAGS.TRADES | FLAGS.INFO);
  assert.equal(ev.symbol, 'ITUB4');
});

test('SubscribeError maps known code to name', () => {
  const ev = parseSingle(frame(MSG.SUBSCRIBE_ERROR, pack(u8(2), strLen8('FOO'))));
  assert.equal(ev.type, 'SubscribeError');
  assert.equal(ev.errorCode, 2);
  assert.equal(ev.errorName, 'NotReady');
  assert.equal(ev.symbol, 'FOO');
});

test('Trade divides price by 1e4', () => {
  // 32.50 → 325000 mantissa
  const ev = parseSingle(frame(
    MSG.TRADE,
    pack(u64(900_000_000_001n), i64(325000), i64(100), i64(7777))
  ));
  assert.equal(ev.type, 'Trade');
  assert.equal(ev.price, 32.5);
  assert.equal(ev.qty, 100);
  assert.equal(ev.tradeId, 7777);
});

test('TradeBust decodes', () => {
  const ev = parseSingle(frame(MSG.TRADE_BUST, pack(u64(900_000_000_001n), i64(7777))));
  assert.deepEqual(ev, { type: 'TradeBust', securityId: 900_000_000_001n, tradeId: 7777 });
});

test('InfoSnapshot decodes selected fields with SBE exponents', () => {
  // mask: bit 4 (LastTradePrice, /1e4), bit 17 (TradingReferencePrice, /1e8)
  const mask = (1 << 4) | (1 << 17);
  const ev = parseSingle(frame(
    MSG.INFO_SNAPSHOT,
    pack(u64(900_000_000_001n), u32(mask), i64(305000), i64(3050000000n))
  ));
  assert.equal(ev.type, 'InfoSnapshot');
  assert.equal(ev.fields.LastTradePrice, 30.5);
  assert.equal(ev.fields.TradingReferencePrice, 30.5);
});

// ── new frames (issue #67) ───────────────────────────────────────────

test('BookSnapshot is a marker frame with only securityId', () => {
  const ev = parseSingle(frame(MSG.BOOK_SNAPSHOT, u64(900_000_000_001n)));
  assert.deepEqual(ev, { type: 'BookSnapshot', securityId: 900_000_000_001n });
});

test('BookCleared wire 0 = both sides (null)', () => {
  const ev = parseSingle(frame(MSG.BOOK_CLEARED, pack(u64(900n), u8(0))));
  assert.deepEqual(ev, { type: 'BookCleared', securityId: 900n, side: null });
});

test('BookCleared wire 1 = bid only', () => {
  const ev = parseSingle(frame(MSG.BOOK_CLEARED, pack(u64(900n), u8(1))));
  assert.equal(ev.side, SIDE.BID);
});

test('BookCleared wire 2 = ask only', () => {
  const ev = parseSingle(frame(MSG.BOOK_CLEARED, pack(u64(900n), u8(2))));
  assert.equal(ev.side, SIDE.ASK);
});

test('BookCleared with side byte omitted = both (null)', () => {
  const ev = parseSingle(frame(MSG.BOOK_CLEARED, u64(900n)));
  assert.equal(ev.side, null);
});

test('LevelSnapshot decodes bid/ask arrays with prices /1e4', () => {
  // 2 bids: (30.50, 100, 1), (30.49, 200, 3)
  // 1 ask:  (30.51, 50, 2)
  const ev = parseSingle(frame(MSG.LEVEL_SNAPSHOT, pack(
    u64(900n),
    u16(2), u16(1),
    i64(305000), i64(100), u32(1),
    i64(304900), i64(200), u32(3),
    i64(305100), i64(50), u32(2),
  )));
  assert.equal(ev.type, 'LevelSnapshot');
  assert.equal(ev.bids.length, 2);
  assert.equal(ev.asks.length, 1);
  assert.deepEqual(ev.bids[0], { price: 30.5, qty: 100, count: 1 });
  assert.deepEqual(ev.bids[1], { price: 30.49, qty: 200, count: 3 });
  assert.deepEqual(ev.asks[0], { price: 30.51, qty: 50, count: 2 });
});

test('LevelSnapshot with zero counts decodes empty arrays', () => {
  const ev = parseSingle(frame(MSG.LEVEL_SNAPSHOT, pack(u64(900n), u16(0), u16(0))));
  assert.equal(ev.bids.length, 0);
  assert.equal(ev.asks.length, 0);
});

test('LevelSnapshot asymmetric: bid-only', () => {
  const ev = parseSingle(frame(MSG.LEVEL_SNAPSHOT, pack(
    u64(900n), u16(1), u16(0),
    i64(305000), i64(100), u32(1),
  )));
  assert.equal(ev.bids.length, 1);
  assert.equal(ev.asks.length, 0);
  assert.deepEqual(ev.bids[0], { price: 30.5, qty: 100, count: 1 });
});

test('LevelSnapshot asymmetric: ask-only', () => {
  const ev = parseSingle(frame(MSG.LEVEL_SNAPSHOT, pack(
    u64(900n), u16(0), u16(1),
    i64(305100), i64(50), u32(2),
  )));
  assert.equal(ev.bids.length, 0);
  assert.equal(ev.asks.length, 1);
  assert.deepEqual(ev.asks[0], { price: 30.51, qty: 50, count: 2 });
});

test('LevelUpdate decodes side, price, qty, count', () => {
  // v2 layout: secId, price, qty, count, side(u8) at the end.
  const ev = parseSingle(frame(MSG.LEVEL_UPDATE, pack(
    u64(900n), i64(305000), i64(150), u32(2), u8(SIDE.BID)
  )));
  assert.deepEqual(ev, {
    type: 'LevelUpdate',
    securityId: 900n,
    side: SIDE.BID,
    price: 30.5,
    qty: 150,
    count: 2,
  });
});

test('LevelDeleted decodes side and price', () => {
  // v2 layout: secId, price, side(u8) at the end.
  const ev = parseSingle(frame(MSG.LEVEL_DELETED, pack(
    u64(900n), i64(305100), u8(SIDE.ASK)
  )));
  assert.deepEqual(ev, {
    type: 'LevelDeleted',
    securityId: 900n,
    side: SIDE.ASK,
    price: 30.51,
  });
});

test('CandleSnapshot decodes OHLC/volume/avg with price scaling', () => {
  // 1 candle: 1m resolution, FIRST|LAST flags, OHLC ~30.5, vol 1000, avg 30.45
  const ev = parseSingle(frame(MSG.CANDLE_SNAPSHOT, pack(
    u64(900n),
    u16(60),                                         // resolution (seconds)
    u8(CANDLE_FLAGS.FIRST | CANDLE_FLAGS.LAST),
    u16(1),                                          // count
    i64(1_700_000_000_000),                          // time (unix ms)
    i64(305000), i64(305500), i64(304500), i64(305200), // OHLC
    i64(1000), i64(304500),                          // volume, avg
  )));
  assert.equal(ev.type, 'CandleSnapshot');
  assert.equal(ev.resolution, 60);
  assert.equal(ev.isFirst, true);
  assert.equal(ev.isLast, true);
  assert.equal(ev.candles.length, 1);
  const c = ev.candles[0];
  assert.equal(c.time, 1_700_000_000_000);
  assert.equal(c.open, 30.5);
  assert.equal(c.high, 30.55);
  assert.equal(c.low, 30.45);
  assert.equal(c.close, 30.52);
  assert.equal(c.volume, 1000);
  assert.equal(c.avg, 30.45);
});

test('CandleSnapshot streams: only FIRST flag on head, only LAST on tail', () => {
  const head = parseSingle(frame(MSG.CANDLE_SNAPSHOT, pack(
    u64(900n), u16(60), u8(CANDLE_FLAGS.FIRST), u16(0)
  )));
  const tail = parseSingle(frame(MSG.CANDLE_SNAPSHOT, pack(
    u64(900n), u16(60), u8(CANDLE_FLAGS.LAST), u16(0)
  )));
  assert.equal(head.isFirst, true);
  assert.equal(head.isLast, false);
  assert.equal(tail.isFirst, false);
  assert.equal(tail.isLast, true);
});

test('CandleSnapshot intermediate frame has neither FIRST nor LAST', () => {
  const mid = parseSingle(frame(MSG.CANDLE_SNAPSHOT, pack(
    u64(900n), u16(60), u8(0), u16(0)
  )));
  assert.equal(mid.isFirst, false);
  assert.equal(mid.isLast, false);
});

test('CandleUpdate decodes single in-progress candle', () => {
  const ev = parseSingle(frame(MSG.CANDLE_UPDATE, pack(
    u64(900n),
    u16(60),
    i64(1_700_000_060_000),
    i64(305200), i64(305800), i64(305000), i64(305700),
    i64(500), i64(305400),
  )));
  assert.equal(ev.type, 'CandleUpdate');
  assert.equal(ev.resolution, 60);
  assert.deepEqual(ev.candle, {
    time: 1_700_000_060_000,
    open: 30.52,
    high: 30.58,
    low: 30.5,
    close: 30.57,
    volume: 500,
    avg: 30.54,
  });
});

// ── multi-frame coalescing ───────────────────────────────────────────

test('parseFrames decodes multiple coalesced frames in one buffer', () => {
  const f1 = frame(MSG.TRADE, pack(u64(900n), i64(305000), i64(100), i64(1)));
  const f2 = frame(MSG.LEVEL_UPDATE, pack(u64(900n), i64(304900), i64(50), u32(1), u8(SIDE.BID)));
  const merged = pack(f1, f2);
  const ab = merged.buffer.slice(0);
  const events = parseFrames(ab);
  assert.equal(events.length, 2);
  assert.equal(events[0].type, 'Trade');
  assert.equal(events[1].type, 'LevelUpdate');
});

test('parseFrames skips unknown message types (forward-compat)', () => {
  // Use a message type the decoder doesn't recognise (0x00FF, reserved).
  const unknown = frame(0x00FF, u64(900n));
  const known = frame(MSG.TRADE, pack(u64(900n), i64(305000), i64(100), i64(1)));
  const ab = pack(unknown, known).buffer.slice(0);
  const events = parseFrames(ab);
  // Unknown is skipped (returns null), known still decodes.
  assert.equal(events.length, 1);
  assert.equal(events[0].type, 'Trade');
});

test('parseFrames bails on truncated buffer (no throw)', () => {
  // Frame claims length=32 but buffer only has the 8-byte header.
  const bad = new Uint8Array(8);
  new DataView(bad.buffer).setUint32(0, 32, true);
  new DataView(bad.buffer).setUint16(4, MSG.TRADE, true);
  const events = parseFrames(bad.buffer);
  assert.equal(events.length, 0);
});

test('parseFrames skips a malformed known-type frame and keeps parsing', () => {
  // LEVEL_UPDATE declares its full length but its payload is truncated
  // to just securityId — DataView reads past the end will throw. The
  // wrapper must catch, drop only that frame, and keep parsing the
  // following valid Trade frame in the same coalesced buffer.
  const malformed = frame(MSG.LEVEL_UPDATE, u64(900n)); // payload too short
  const good = frame(MSG.TRADE, pack(u64(900n), i64(305000), i64(100), i64(1)));
  const ab = pack(malformed, good).buffer.slice(0);
  const events = parseFrames(ab);
  assert.equal(events.length, 1);
  assert.equal(events[0].type, 'Trade');
});
