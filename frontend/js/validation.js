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

  // Q1.4 (#256). Limit + StopLimit + MarketWithLeftover all carry a
  // limit price that must clear the price/tick guards. Market /
  // StopLoss have no limit price (StopLoss carries StopPrice instead,
  // validated in the ticket UI's validateTicketState).
  if (payload.type === "Limit" || payload.type === "StopLimit" || payload.type === "MarketWithLeftover") {
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
// Includes every field the trader can edit between submits — bumping
// TIF, StopPrice, or GoodTillDate must produce a fresh key so the
// fat-finger override doesn't carry over to a meaningfully different
// order. Absent fields use stable placeholders ("" / "Day") so legacy
// Limit/Day shapes hash identically to the pre-Q1.4 form of the key.
export function payloadKey(payload) {
  return [
    payload.symbol,
    payload.side,
    payload.type,
    payload.quantity,
    payload.price ?? "",
    payload.timeInForce ?? "Day",
    payload.stopPrice ?? "",
    payload.goodTillDate ?? "",
  ].join("|");
}

// ── Fase 2 (#398). Algos creation validation ──────────────────────
//
// Pure-function mirror of `AlgoEndpoints.MapAlgo` POST /algo validation
// (PT-BR messages). Returns `{ ok: true }` or `{ ok: false, error,
// detail? }`. Callers normalise the form input into the same shape
// the REST request expects (CreateAlgoRequest) before invoking this.

const ALGO_TYPES = ["Iceberg", "Twap", "Vwap", "Pov", "Pegged"];
const ALGO_SIDES = ["Buy", "Sell"];
const ALGO_CHILD_TYPES_LIMIT_MARKET = ["Limit", "Market"];
const PEGGED_REFS = ["Mid", "Best", "Last"];

function _err(error, detail) {
  return detail === undefined ? { ok: false, error } : { ok: false, error, detail };
}

function _validateChildTypeAndPrice(params, allowMarket = true) {
  const childType = params.childOrderType;
  const allowed = allowMarket ? ALGO_CHILD_TYPES_LIMIT_MARKET : ["Limit"];
  if (!allowed.includes(childType)) {
    return _err(`childOrderType deve ser ${allowed.join(" ou ")}`);
  }
  if (childType === "Limit") {
    if (params.childPrice == null || !(params.childPrice > 0)) {
      return _err("childPrice é obrigatório (> 0) quando childOrderType=Limit");
    }
  }
  return null;
}

function _validateStartEnd(params) {
  if (!params.startUtc || !params.endUtc) {
    return _err("startUtc e endUtc são obrigatórios");
  }
  const s = Date.parse(params.startUtc);
  const e = Date.parse(params.endUtc);
  if (!Number.isFinite(s) || !Number.isFinite(e)) {
    return _err("startUtc e endUtc devem ser datas ISO-8601 válidas");
  }
  if (e <= s) {
    return _err("endUtc deve ser maior que startUtc");
  }
  return null;
}

export function validateCreateAlgo(payload) {
  if (!payload || typeof payload !== "object") return _err("payload inválido");
  const { symbol, side, type, totalQuantity, securityId } = payload;
  if (!symbol || typeof symbol !== "string") return _err("symbol é obrigatório");
  if (!ALGO_SIDES.includes(side)) return _err("side deve ser Buy ou Sell");
  if (!ALGO_TYPES.includes(type)) return _err(`type deve ser um de ${ALGO_TYPES.join(", ")}`);
  if (!Number.isFinite(Number(totalQuantity)) || Number(totalQuantity) <= 0) {
    return _err("totalQuantity deve ser positivo");
  }
  if (securityId == null || !(Number(securityId) > 0)) {
    return _err("securityId é obrigatório (> 0)");
  }

  switch (type) {
    case "Iceberg": {
      const p = payload.iceberg;
      if (!p) return _err("bloco iceberg é obrigatório");
      const dq = Number(p.displayQuantity);
      if (!Number.isFinite(dq) || dq <= 0) return _err("displayQuantity deve ser positivo");
      if (dq > Number(totalQuantity)) return _err("displayQuantity não pode exceder totalQuantity");
      if (p.limitPrice != null && !(p.limitPrice > 0)) return _err("limitPrice deve ser positivo quando informado");
      return { ok: true };
    }
    case "Twap": {
      const p = payload.twap;
      if (!p) return _err("bloco twap é obrigatório");
      const sliceCount = Number(p.sliceCount);
      if (!Number.isFinite(sliceCount) || sliceCount <= 0) return _err("sliceCount deve ser positivo");
      const childErr = _validateChildTypeAndPrice(p, true);
      if (childErr) return childErr;
      const seErr = _validateStartEnd(p);
      if (seErr) return seErr;
      const floor = Math.floor(Number(totalQuantity) / sliceCount);
      if (floor <= 0) {
        return _err("totalQuantity / sliceCount deve ser ≥ 1", { impliedSliceQuantity: floor });
      }
      return { ok: true };
    }
    case "Vwap": {
      const p = payload.vwap;
      if (!p) return _err("bloco vwap é obrigatório");
      const childErr = _validateChildTypeAndPrice(p, true);
      if (childErr) return childErr;
      const seErr = _validateStartEnd(p);
      if (seErr) return seErr;
      if (p.tickIntervalSeconds != null && !(p.tickIntervalSeconds > 0)) {
        return _err("tickIntervalSeconds deve ser positivo");
      }
      if (p.sliceMaxPct != null && !(p.sliceMaxPct > 0 && p.sliceMaxPct <= 1)) {
        return _err("sliceMaxPct deve estar em (0, 1]");
      }
      if (p.participationCap != null && !(p.participationCap > 0 && p.participationCap <= 1)) {
        return _err("participationCap deve estar em (0, 1]");
      }
      if (p.priceLimit != null && !(p.priceLimit > 0)) return _err("priceLimit deve ser positivo quando informado");
      return { ok: true };
    }
    case "Pov": {
      const p = payload.pov;
      if (!p) return _err("bloco pov é obrigatório");
      const childErr = _validateChildTypeAndPrice(p, true);
      if (childErr) return childErr;
      const seErr = _validateStartEnd(p);
      if (seErr) return seErr;
      if (!(p.participationRate > 0 && p.participationRate <= 1)) {
        return _err("participationRate é obrigatório em (0, 1]");
      }
      if (p.tickIntervalSeconds != null && !(p.tickIntervalSeconds > 0)) {
        return _err("tickIntervalSeconds deve ser positivo");
      }
      if (p.minSliceQty != null && !(p.minSliceQty >= 1)) {
        return _err("minSliceQty deve ser ≥ 1");
      }
      if (p.priceLimit != null && !(p.priceLimit > 0)) return _err("priceLimit deve ser positivo quando informado");
      return { ok: true };
    }
    case "Pegged": {
      const p = payload.pegged;
      if (!p) return _err("bloco pegged é obrigatório");
      const ref = typeof p.ref === "string" ? p.ref : "";
      const normalized = PEGGED_REFS.find(r => r.toLowerCase() === ref.toLowerCase());
      if (!normalized) return _err(`ref deve ser ${PEGGED_REFS.join(" | ")}`);
      if (p.repegIntervalMs != null && !(p.repegIntervalMs > 0)) {
        return _err("repegIntervalMs deve ser positivo");
      }
      if (p.tickSize != null && !(p.tickSize > 0)) {
        return _err("tickSize deve ser positivo");
      }
      if (p.childOrderType != null && p.childOrderType !== "Limit") {
        return _err("Pegged só aceita childOrderType=Limit");
      }
      if (!Number.isFinite(Number(p.offsetTicks))) {
        return _err("offsetTicks é obrigatório (inteiro)");
      }
      if (p.priceLimit != null && !(p.priceLimit > 0)) return _err("priceLimit deve ser positivo quando informado");
      return { ok: true };
    }
    default:
      return _err(`type desconhecido: ${type}`);
  }
}
