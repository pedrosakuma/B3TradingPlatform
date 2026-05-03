// Pre-trade validation helpers. These are advisory client-side checks;
// the backend is still the source of truth for risk and exchange rules.

const DEFAULTS = {
  tickSize: 0.01,
  lotSize: 100,
  fatFingerThreshold: 0.10, // 10% deviation from last trade
};

// Per-symbol overrides — populated as the platform grows. Until then
// the defaults serve PETR4 / VALE3 / etc adequately (lot=100, tick=0.01).
const PER_SYMBOL = {
  // Example: "BOVA11": { tickSize: 0.01, lotSize: 10 }
};

export function rulesFor(symbol) {
  const o = PER_SYMBOL[symbol?.toUpperCase()] ?? {};
  return {
    tickSize:           o.tickSize           ?? DEFAULTS.tickSize,
    lotSize:            o.lotSize            ?? DEFAULTS.lotSize,
    fatFingerThreshold: o.fatFingerThreshold ?? DEFAULTS.fatFingerThreshold,
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

// Stable key for a payload so the UI can detect "same submission"
// when the user clicks Submit a second time to override the warning.
export function payloadKey(payload) {
  return [payload.symbol, payload.side, payload.type, payload.quantity, payload.price ?? ""].join("|");
}
