// Minimal binary protocol for B3MarketDataPlatform's WebSocket
// (subset used by the trader UI). Mirrors the wire format documented
// in the MD repo's WEBSOCKET_API.md / WEBSOCKET-PROTOCOL.md.
//
// We deliberately *do not* depend on the MD repo's frontend protocol
// helper — keeping a thin, hand-rolled subset here means the trader UI
// has no upstream coupling beyond the wire.

export const MSG = {
  SUBSCRIBE: 0x0001,
  UNSUBSCRIBE: 0x0002,
  SUBSCRIBE_OK: 0x0010,
  SUBSCRIBE_ERROR: 0x0011,
  INFO_SNAPSHOT: 0x0021,
  TRADE: 0x0033,
  TRADE_BUST: 0x0035,
  SERVER_STATUS: 0x0050,
};

// DataFlags bits we use. `Trades` alone is enough to update last-trade
// price; `Info` adds the periodic snapshot which seeds the cache before
// the first live trade and surfaces venue-side trading status.
export const FLAGS = {
  INFO: 0x02,
  TRADES: 0x10,
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

const SUBSCRIBE_ERROR_NAMES = { 1: 'UnknownSymbol', 2: 'NotReady' };

const encoder = new TextEncoder();
const decoder = new TextDecoder();

// ── Builders ──────────────────────────────────────────────────────────

export function buildSubscribe(symbol, flags = FLAGS.TRADES | FLAGS.INFO) {
  const symBytes = encoder.encode(symbol.toUpperCase().trim());
  if (symBytes.length === 0 || symBytes.length > 255) {
    throw new Error(`invalid symbol length: ${symBytes.length}`);
  }
  const totalLen = 4 + 1 + 1 + symBytes.length;
  const buf = new ArrayBuffer(totalLen);
  const v = new DataView(buf);
  v.setUint16(0, totalLen, true);
  v.setUint16(2, MSG.SUBSCRIBE, true);
  v.setUint8(4, flags);
  v.setUint8(5, symBytes.length);
  new Uint8Array(buf, 6).set(symBytes);
  return buf;
}

export function buildUnsubscribe(securityId) {
  const buf = new ArrayBuffer(12);
  const v = new DataView(buf);
  v.setUint16(0, 12, true);
  v.setUint16(2, MSG.UNSUBSCRIBE, true);
  v.setBigUint64(4, BigInt(securityId), true);
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
  while (off + 4 <= total) {
    const v = new DataView(arrayBuffer, off, Math.min(4, total - off));
    const len = v.getUint16(0, true);
    if (len < 4 || off + len > total) break; // truncated/garbage; bail
    const ev = parseOne(arrayBuffer, off, len);
    if (ev) out.push(ev);
    off += len;
  }
  return out;
}

function parseOne(buf, base, len) {
  const v = new DataView(buf, base, len);
  const type = v.getUint16(2, true);
  let o = 4;
  switch (type) {
    case MSG.SERVER_STATUS:
      return { type: 'ServerStatus', ready: v.getUint8(o) === 1 };

    case MSG.SUBSCRIBE_OK: {
      const securityId = v.getBigUint64(o, true); o += 8;
      const flags = v.getUint8(o); o += 1;
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
      const price = Number(priceMantissa) / 10_000;
      return { type: 'Trade', securityId, price, qty, tradeId };
    }

    case MSG.TRADE_BUST: {
      const securityId = v.getBigUint64(o, true); o += 8;
      const tradeId = Number(v.getBigInt64(o, true));
      return { type: 'TradeBust', securityId, tradeId };
    }

    default:
      return null; // forward-compat: ignore unknown types
  }
}
