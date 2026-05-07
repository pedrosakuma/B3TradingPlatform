// Pre-trade validation helpers. These are advisory client-side checks;
// the backend is still the source of truth for risk and exchange rules.

const DEFAULTS = {
  tickSize: 0.01,
  lotSize: 100,
  fatFingerThreshold: 0.10, // 10% deviation from last trade
  // Soft cap on quantity expressed as a multiple of the lot size. A
  // ticket above this needs an explicit confirm. 100× lot ≈ 10_000
  // shares for the typical PETR4/VALE3 lot, which is comfortably
  // larger than the median manual trade but small enough to catch
  // an extra zero typo (100k where 10k was meant).
  maxQuantityLotMultiple: 100,
  // Notional confirmation threshold for Market orders, in BRL.
  // Limit orders are already covered by the fat-finger check on
  // price, but Market orders have no price to compare against — so
  // we estimate notional from `qty * lastPrice` and arm a confirm
  // when it crosses this number.
  marketNotionalConfirm: 500_000,
};

// Per-symbol overrides — populated as the platform grows. Until then
// the defaults serve PETR4 / VALE3 / etc adequately (lot=100, tick=0.01).
const PER_SYMBOL = {
  // Example: "BOVA11": { tickSize: 0.01, lotSize: 10 }
};

export function rulesFor(symbol) {
  const o = PER_SYMBOL[symbol?.toUpperCase()] ?? {};
  return {
    tickSize:              o.tickSize              ?? DEFAULTS.tickSize,
    lotSize:               o.lotSize               ?? DEFAULTS.lotSize,
    fatFingerThreshold:    o.fatFingerThreshold    ?? DEFAULTS.fatFingerThreshold,
    maxQuantityLotMultiple: o.maxQuantityLotMultiple ?? DEFAULTS.maxQuantityLotMultiple,
    marketNotionalConfirm: o.marketNotionalConfirm ?? DEFAULTS.marketNotionalConfirm,
  };
}

// Return null if valid, else { code, message } — caller decides how
// to render. Callers must pass `lastPrice` from market data when the
// fat-finger check is wanted; pass null/undefined to skip it.
export function validateOrder(payload, lastPrice) {
  if (!payload.symbol) return { code: "symbol_required", message: "symbol required" };

  const qty = Number(payload.quantity);
  if (!Number.isFinite(qty) || qty <= 0)
    return { code: "qty_invalid", message: "quantity must be positive" };

  const rules = rulesFor(payload.symbol);

  if (qty % rules.lotSize !== 0)
    return {
      code: "lot_size",
      message: `quantity must be a multiple of ${rules.lotSize} for ${payload.symbol}`,
    };

  if (payload.type === "Limit") {
    const px = Number(payload.price);
    if (!Number.isFinite(px) || px <= 0)
      return { code: "price_required", message: "limit price required" };

    // Tick alignment: avoid floating-point drift by comparing
    // (price / tick) to its rounded integer within a small epsilon.
    const ratio = px / rules.tickSize;
    const rounded = Math.round(ratio);
    if (Math.abs(ratio - rounded) > 1e-6)
      return {
        code: "tick_size",
        message: `price must be a multiple of ${rules.tickSize.toFixed(2)} for ${payload.symbol}`,
      };
  }

  return null;
}

// Returns { warn: true, deviation: n } when the limit price strays by
// more than the threshold from the last observed trade. Returns null
// otherwise (no warning, OR not a limit order, OR no reference price).
export function fatFingerCheck(payload, lastPrice) {
  if (payload.type !== "Limit") return null;
  if (!Number.isFinite(lastPrice) || lastPrice <= 0) return null;

  const px = Number(payload.price);
  if (!Number.isFinite(px) || px <= 0) return null;

  const rules = rulesFor(payload.symbol);
  const deviation = Math.abs(px - lastPrice) / lastPrice;
  if (deviation <= rules.fatFingerThreshold) return null;
  return { warn: true, deviation, lastPrice, threshold: rules.fatFingerThreshold };
}

// Soft cap on quantity. Returns { warn, qty, lotSize, multiple, threshold }
// when qty exceeds `maxQuantityLotMultiple × lotSize`, else null. Catches
// the classic extra-zero typo (100_000 where 10_000 was meant).
export function quantityGuardCheck(payload) {
  const qty = Number(payload.quantity);
  if (!Number.isFinite(qty) || qty <= 0) return null;
  const rules = rulesFor(payload.symbol);
  const cap = rules.maxQuantityLotMultiple * rules.lotSize;
  if (qty <= cap) return null;
  return {
    warn: true,
    qty,
    lotSize: rules.lotSize,
    multiple: rules.maxQuantityLotMultiple,
    threshold: cap,
  };
}

// Notional confirmation for Market orders. We have no limit price to
// fat-finger-check, so the only safety net is "this would spend more
// than R$ X" — armed once and confirmed on a second click.
// Returns { warn, notional, lastPrice, threshold } or null.
export function marketNotionalCheck(payload, lastPrice) {
  if (payload.type !== "Market") return null;
  const qty = Number(payload.quantity);
  if (!Number.isFinite(qty) || qty <= 0) return null;
  if (!Number.isFinite(lastPrice) || lastPrice <= 0) return null;

  const rules = rulesFor(payload.symbol);
  const notional = qty * lastPrice;
  if (notional < rules.marketNotionalConfirm) return null;
  return { warn: true, notional, lastPrice, threshold: rules.marketNotionalConfirm };
}

// Run every advisory pre-trade check and return them in a stable order.
// Callers render a combined warning and arm a single "click again to
// confirm" override keyed by `payloadKey`.
export function pretradeWarnings(payload, lastPrice) {
  const out = [];
  const q  = quantityGuardCheck(payload);   if (q)  out.push({ kind: "qty", ...q });
  const ff = fatFingerCheck(payload, lastPrice); if (ff) out.push({ kind: "fat_finger", ...ff });
  const m  = marketNotionalCheck(payload, lastPrice); if (m) out.push({ kind: "market_notional", ...m });
  return out;
}

// Stable key for a payload so the UI can detect "same submission"
// when the user clicks Submit a second time to override the warning.
export function payloadKey(payload) {
  return [payload.symbol, payload.side, payload.type, payload.quantity, payload.price ?? ""].join("|");
}
