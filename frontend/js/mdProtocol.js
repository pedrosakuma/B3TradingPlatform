// Minimal binary protocol for B3MarketDataPlatform's WebSocket
// (subset used by the trader UI). Mirrors the wire format documented
// in the MD repo's WEBSOCKET_API.md / WEBSOCKET-PROTOCOL.md.
//
// We deliberately *do not* depend on the MD repo's frontend protocol
// helper — keeping a thin, hand-rolled subset here means the trader UI
// has no upstream coupling beyond the wire.
//
// (T1, issue #67): adds MBP book frames (BOOK_SNAPSHOT, LEVEL_*,
// BOOK_CLEARED) and server-side candles (CANDLE_*). MBO order frames
// (ORDER_*) remain out of scope — see RFC #66 for rationale.
//
// Wire protocol v2 (MD repo PR #61): the framing is an 8-byte header
// `[len u32][type u16][headerFlags u16]` (was the 4-byte `[len u16]
// [type u16]`); DataFlags/SubscribeFlags widened to u32; and the hot
// fixed frames are laid out largest-field-first so the `side` byte now
// trails LevelUpdate/LevelDeleted. headerFlags is reserved (0) and
// ignored on receive for forward-compat.

export const MSG = {
  SUBSCRIBE: 0x0001,
  UNSUBSCRIBE: 0x0002,
  SUBSCRIBE_OK: 0x0010,
  SUBSCRIBE_ERROR: 0x0011,
  BOOK_SNAPSHOT: 0x0020,
  INFO_SNAPSHOT: 0x0021,
  LEVEL_SNAPSHOT: 0x0022,
  TRADE: 0x0033,
  BOOK_CLEARED: 0x0034,
  TRADE_BUST: 0x0035,
  LEVEL_UPDATE: 0x0037,
  LEVEL_DELETED: 0x0038,
  SERVER_STATUS: 0x0050,
  CANDLE_SNAPSHOT: 0x0060,
  CANDLE_UPDATE: 0x0061,
};

// DataFlags bits we use. `Trades` alone is enough to update last-trade
// price; `Info` adds the periodic snapshot which seeds the cache before
// the first live trade and surfaces venue-side trading status. `Mbp`
// streams aggregated price levels (DOB panel — issue #68). `Book` is
// kept for completeness but the trader UI doesn't render full MBO.
export const FLAGS = {
  BOOK: 0x01,
  INFO: 0x02,
  MBP: 0x08,
  TRADES: 0x10,
};

// CandleSnapshot.flags bits. Server may stream a multi-frame snapshot
// (FIRST on the head, LAST on the tail) when the candle history
// doesn't fit in a single frame.
export const CANDLE_FLAGS = {
  FIRST: 0x01,
  LAST: 0x02,
};

// Side encoding for LevelUpdate/LevelDeleted (uint8). Bid=0, Ask=1 —
// verified against MD worker.js (`side === 0 ? bidLevels : askLevels`).
//
// NOTE: BookCleared uses a *different* wire encoding (0=both,
// 1=bid-only, 2=ask-only — verified against upstream worker.js
// `BookCleared` handler). The decoder normalises that to `SIDE.BID`,
// `SIDE.ASK`, or `null` (both) so consumers can use one enum everywhere.
export const SIDE = {
  BID: 0,
  ASK: 1,
};

// Order matches the bit indices in InfoSnapshot.fieldMask. We only
// surface the two prices the trader UI uses; extending later is just
// adding entries here in bit order.
const INFO_FIELDS = [
  'OpeningPrice', 'ClosingPrice', 'HighPrice', 'LowPrice',
  'LastTradePrice', 'LastTradeSize', 'SettlementPrice', 'TheoreticalOpeningPrice',
  'TheoreticalOpeningSize', 'AuctionImbalanceSize', 'TradeVolume', 'VwapPrice',
  'NetChange', 'NumberOfTrades', 'OpenInterest', 'PriceBandLow',
  'PriceBandHigh', 'TradingReferencePrice', 'AvgDailyTradedQty', 'MaxTradeVol',
  'TradingStatus', 'TradingEvent', 'PriceLimitType', 'MinPriceIncrement',
];

// SBE exponents per price field (negative power of 10). Anything not
// listed is treated as a raw integer.
const FIELD_DECIMALS = {
  OpeningPrice: 4, ClosingPrice: 8, HighPrice: 4, LowPrice: 4,
  LastTradePrice: 4, SettlementPrice: 4, TheoreticalOpeningPrice: 4,
  VwapPrice: 4, NetChange: 8, PriceBandLow: 4, PriceBandHigh: 4,
  TradingReferencePrice: 8,
};

// SBE Price exponent (-4) for trade/level/candle prices. All B3 cash-equity
// prices encode with 4 decimals; we divide here so consumers see decimals
// (R$ 30.50 instead of 305000). Risk consumers care about the divided value,
// not the i64 mantissa, and we are nowhere near 2^53 at typical magnitudes.
const PRICE_DIVISOR = 10_000;

const SUBSCRIBE_ERROR_NAMES = { 1: 'UnknownSymbol', 2: 'NotReady' };

const encoder = new TextEncoder();
const decoder = new TextDecoder();

// ── Builders ──────────────────────────────────────────────────────────

const DEFAULT_SUBSCRIBE_FLAGS = FLAGS.TRADES | FLAGS.INFO;

export function buildSubscribe(symbol, flags = DEFAULT_SUBSCRIBE_FLAGS) {
  const symBytes = encoder.encode(symbol.toUpperCase().trim());
  if (symBytes.length === 0 || symBytes.length > 255) {
    throw new Error(`invalid symbol length: ${symBytes.length}`);
  }
  // v2: [len u32][type u16][headerFlags u16][flags u32][symLen u8][symbol]
  const totalLen = 8 + 4 + 1 + symBytes.length;
  const buf = new ArrayBuffer(totalLen);
  const v = new DataView(buf);
  v.setUint32(0, totalLen, true);
  v.setUint16(4, MSG.SUBSCRIBE, true);
  v.setUint16(6, 0, true); // headerFlags reserved
  v.setUint32(8, flags >>> 0, true);
  v.setUint8(12, symBytes.length);
  new Uint8Array(buf, 13).set(symBytes);
  return buf;
}

export function buildUnsubscribe(securityId) {
  // v2: [len u32][type u16][headerFlags u16][securityId u64] = 16 bytes
  const buf = new ArrayBuffer(16);
  const v = new DataView(buf);
  v.setUint32(0, 16, true);
  v.setUint16(4, MSG.UNSUBSCRIBE, true);
  v.setUint16(6, 0, true); // headerFlags reserved
  v.setBigUint64(8, BigInt(securityId), true);
  return buf;
}

// ── Parser ────────────────────────────────────────────────────────────

/**
 * Parse every frame in a binary WebSocket message. The MD server may
 * coalesce multiple protocol frames into one WS message; each frame
 * carries its own length prefix. Returns an array of decoded events.
 *
 * Skips unknown message types defensively (forward-compat with new
 * MSG.* values added under the v1-additive guarantee).
 */
export function parseFrames(arrayBuffer) {
  const out = [];
  const total = arrayBuffer.byteLength;
  let off = 0;
  while (off + 8 <= total) {
    const v = new DataView(arrayBuffer, off, 8);
    const len = v.getUint32(0, true);
    if (len < 8 || off + len > total) break; // truncated/garbage; bail
    let ev = null;
    try { ev = parseOne(arrayBuffer, off, len); }
    catch { /* malformed payload of a known type; skip this frame, keep parsing */ }
    if (ev) out.push(ev);
    off += len;
  }
  return out;
}

function parseOne(buf, base, len) {
  const v = new DataView(buf, base, len);
  const type = v.getUint16(4, true);
  let o = 8;
  switch (type) {
    case MSG.SERVER_STATUS:
      return { type: 'ServerStatus', ready: v.getUint8(o) === 1 };

    case MSG.SUBSCRIBE_OK: {
      const securityId = v.getBigUint64(o, true); o += 8;
      const flags = v.getUint32(o, true); o += 4;
      const sLen = v.getUint8(o); o += 1;
      const symbol = decoder.decode(new Uint8Array(buf, base + o, sLen));
      return { type: 'SubscribeOk', securityId, flags, symbol };
    }

    case MSG.SUBSCRIBE_ERROR: {
      const code = v.getUint8(o); o += 1;
      const sLen = v.getUint8(o); o += 1;
      const symbol = decoder.decode(new Uint8Array(buf, base + o, sLen));
      return {
        type: 'SubscribeError',
        symbol,
        errorCode: code,
        errorName: SUBSCRIBE_ERROR_NAMES[code] || `Code ${code}`,
      };
    }

    case MSG.INFO_SNAPSHOT: {
      const securityId = v.getBigUint64(o, true); o += 8;
      const mask = v.getUint32(o, true); o += 4;
      const fields = {};
      for (let i = 0; i < INFO_FIELDS.length; i++) {
        if (!(mask & (1 << i))) continue;
        if (o + 8 > len) break;
        const raw = Number(v.getBigInt64(o, true)); o += 8;
        const exp = FIELD_DECIMALS[INFO_FIELDS[i]];
        fields[INFO_FIELDS[i]] = exp ? raw / Math.pow(10, exp) : raw;
      }
      return { type: 'InfoSnapshot', securityId, fields };
    }

    case MSG.TRADE: {
      const securityId = v.getBigUint64(o, true); o += 8;
      const priceMantissa = v.getBigInt64(o, true); o += 8;
      const qty = Number(v.getBigInt64(o, true)); o += 8;
      const tradeId = Number(v.getBigInt64(o, true));
      // Price field has SBE exponent -4. Use Number() because at typical
      // B3 magnitudes (R$ 1e0–1e3 with 4 decimals) we are nowhere near
      // 2^53; risk consumers care about the divided value, not the i64.
      const price = Number(priceMantissa) / PRICE_DIVISOR;
      return { type: 'Trade', securityId, price, qty, tradeId };
    }

    case MSG.TRADE_BUST: {
      const securityId = v.getBigUint64(o, true); o += 8;
      const tradeId = Number(v.getBigInt64(o, true));
      return { type: 'TradeBust', securityId, tradeId };
    }

    case MSG.BOOK_SNAPSHOT: {
      // Marker frame: server announces a fresh book after subscribe with
      // the BOOK flag. Concrete levels arrive in subsequent LEVEL_SNAPSHOT
      // (MBP) or ORDER_ADDED (MBO; not consumed here).
      const securityId = v.getBigUint64(o, true);
      return { type: 'BookSnapshot', securityId };
    }

    case MSG.BOOK_CLEARED: {
      // Wire: 0=both sides, 1=bid only, 2=ask only. Older frames may
      // omit the byte — treat as bilateral. Translated to our SIDE
      // enum (or null = both) so consumers don't need to know the
      // wire-vs-enum delta.
      const securityId = v.getBigUint64(o, true); o += 8;
      const wire = o < len ? v.getUint8(o) : 0;
      let side;
      if (wire === 1) side = SIDE.BID;
      else if (wire === 2) side = SIDE.ASK;
      else side = null; // 0 (or omitted) = both
      return { type: 'BookCleared', securityId, side };
    }

    case MSG.LEVEL_SNAPSHOT: {
      // securityId(8) + bidCount(2) + askCount(2) + N entries.
      // Entry layout: price(8 i64 mantissa, /1e4) + qty(8 i64) + count(4 u32) = 20 bytes.
      const securityId = v.getBigUint64(o, true); o += 8;
      const bidCount = v.getUint16(o, true); o += 2;
      const askCount = v.getUint16(o, true); o += 2;
      const bids = new Array(bidCount);
      for (let i = 0; i < bidCount; i++) {
        const price = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
        const qty = Number(v.getBigInt64(o, true)); o += 8;
        const count = v.getUint32(o, true); o += 4;
        bids[i] = { price, qty, count };
      }
      const asks = new Array(askCount);
      for (let i = 0; i < askCount; i++) {
        const price = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
        const qty = Number(v.getBigInt64(o, true)); o += 8;
        const count = v.getUint32(o, true); o += 4;
        asks[i] = { price, qty, count };
      }
      return { type: 'LevelSnapshot', securityId, bids, asks };
    }

    case MSG.LEVEL_UPDATE: {
      // v2 layout: secId, price, qty, orderCount(u32), side(u8) at the end.
      const securityId = v.getBigUint64(o, true); o += 8;
      const price = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
      const qty = Number(v.getBigInt64(o, true)); o += 8;
      const count = v.getUint32(o, true); o += 4;
      const side = v.getUint8(o);
      return { type: 'LevelUpdate', securityId, side, price, qty, count };
    }

    case MSG.LEVEL_DELETED: {
      // v2 layout: secId, price, side(u8) at the end.
      const securityId = v.getBigUint64(o, true); o += 8;
      const price = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
      const side = v.getUint8(o);
      return { type: 'LevelDeleted', securityId, side, price };
    }

    case MSG.CANDLE_SNAPSHOT: {
      // securityId(8) + resolution(2 u16, seconds) + flags(1) + count(2) + N candles.
      // Candle layout: time(8 unix-ms i64) + OHLC(4×8 i64, /1e4) + volume(8 i64) + avg(8 i64, /1e4) = 56 bytes.
      const securityId = v.getBigUint64(o, true); o += 8;
      const resolution = v.getUint16(o, true); o += 2;
      const flags = v.getUint8(o); o += 1;
      const count = v.getUint16(o, true); o += 2;
      const candles = new Array(count);
      for (let i = 0; i < count; i++) {
        const time = Number(v.getBigInt64(o, true)); o += 8;
        const open = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
        const high = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
        const low = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
        const close = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
        const volume = Number(v.getBigInt64(o, true)); o += 8;
        const avg = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
        candles[i] = { time, open, high, low, close, volume, avg };
      }
      return {
        type: 'CandleSnapshot',
        securityId,
        resolution,
        candles,
        isFirst: !!(flags & CANDLE_FLAGS.FIRST),
        isLast: !!(flags & CANDLE_FLAGS.LAST),
      };
    }

    case MSG.CANDLE_UPDATE: {
      const securityId = v.getBigUint64(o, true); o += 8;
      const resolution = v.getUint16(o, true); o += 2;
      const time = Number(v.getBigInt64(o, true)); o += 8;
      const open = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
      const high = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
      const low = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
      const close = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR; o += 8;
      const volume = Number(v.getBigInt64(o, true)); o += 8;
      const avg = Number(v.getBigInt64(o, true)) / PRICE_DIVISOR;
      return {
        type: 'CandleUpdate',
        securityId,
        resolution,
        candle: { time, open, high, low, close, volume, avg },
      };
    }

    default:
      return null; // forward-compat: ignore unknown types
  }
}
