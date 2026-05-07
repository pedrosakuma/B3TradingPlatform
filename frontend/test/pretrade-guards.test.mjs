// Pre-trade advisory guards — quantity soft-cap and market-order
// notional confirmation. Pairs with the existing fat-finger check;
// all three are funnelled through `pretradeWarnings()` so the order
// ticket can render a single combined message and arm one override.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  rulesFor,
  quantityGuardCheck,
  marketNotionalCheck,
  fatFingerCheck,
  pretradeWarnings,
} from "../js/validation.js";

test("rulesFor exposes maxQuantityLotMultiple and marketNotionalConfirm", () => {
  const r = rulesFor("PETR4");
  assert.equal(r.maxQuantityLotMultiple, 100);
  assert.equal(r.marketNotionalConfirm, 500_000);
});

test("quantityGuardCheck: under cap → null", () => {
  // 100 lot × 100 multiple = 10_000 cap. 9_900 is below.
  const out = quantityGuardCheck({ symbol: "PETR4", quantity: 9_900 });
  assert.equal(out, null);
});

test("quantityGuardCheck: equal to cap → null (cap is inclusive)", () => {
  const out = quantityGuardCheck({ symbol: "PETR4", quantity: 10_000 });
  assert.equal(out, null);
});

test("quantityGuardCheck: above cap → warn with cap details", () => {
  const out = quantityGuardCheck({ symbol: "PETR4", quantity: 100_000 });
  assert.ok(out, "expected a warning for 100k vs 10k cap");
  assert.equal(out.qty, 100_000);
  assert.equal(out.multiple, 100);
  assert.equal(out.threshold, 10_000);
});

test("quantityGuardCheck: ignores non-positive / NaN quantities", () => {
  for (const q of [0, -1, NaN, "x"]) {
    assert.equal(quantityGuardCheck({ symbol: "PETR4", quantity: q }), null);
  }
});

test("marketNotionalCheck: only fires for Market orders", () => {
  const limit = marketNotionalCheck({ symbol: "PETR4", type: "Limit", quantity: 1_000_000 }, 100);
  assert.equal(limit, null);
});

test("marketNotionalCheck: requires a positive lastPrice", () => {
  for (const lp of [undefined, null, 0, -1, NaN]) {
    assert.equal(
      marketNotionalCheck({ symbol: "PETR4", type: "Market", quantity: 1_000_000 }, lp),
      null,
    );
  }
});

test("marketNotionalCheck: under threshold → null", () => {
  // 100 × 32.50 = 3_250 — well below the 500k threshold
  const out = marketNotionalCheck({ symbol: "PETR4", type: "Market", quantity: 100 }, 32.5);
  assert.equal(out, null);
});

test("marketNotionalCheck: at/above threshold → warn", () => {
  // 20_000 × 32.50 = 650_000 → above 500k
  const out = marketNotionalCheck({ symbol: "PETR4", type: "Market", quantity: 20_000 }, 32.5);
  assert.ok(out);
  assert.equal(out.notional, 650_000);
  assert.equal(out.threshold, 500_000);
});

test("pretradeWarnings: combines all triggered checks in stable order", () => {
  // Limit, qty=100k (over cap), price 9999 vs lastPrice 32 (huge fat-finger)
  const warns = pretradeWarnings(
    { symbol: "PETR4", side: "Buy", type: "Limit", quantity: 100_000, price: 9999 },
    32,
  );
  assert.equal(warns.length, 2);
  assert.equal(warns[0].kind, "qty");        // qty first
  assert.equal(warns[1].kind, "fat_finger"); // then fat-finger
});

test("pretradeWarnings: market+huge notional triggers only market_notional", () => {
  const warns = pretradeWarnings(
    { symbol: "PETR4", side: "Buy", type: "Market", quantity: 100 },
    100_000,
  );
  // 100 * 100_000 = 10MM ≥ 500k → market_notional. qty=100 is below cap.
  assert.equal(warns.length, 1);
  assert.equal(warns[0].kind, "market_notional");
});

test("pretradeWarnings: clean payload returns empty array", () => {
  const warns = pretradeWarnings(
    { symbol: "PETR4", side: "Buy", type: "Limit", quantity: 100, price: 32.5 },
    32.5,
  );
  assert.deepEqual(warns, []);
});

test("backwards-compat: fatFingerCheck still callable directly", () => {
  // Legacy callers that haven't migrated to pretradeWarnings should
  // keep working — the helper is still exported with the old shape.
  const ff = fatFingerCheck(
    { symbol: "PETR4", type: "Limit", quantity: 100, price: 100 },
    32.5,
  );
  assert.ok(ff);
  assert.equal(ff.warn, true);
});
