import { test } from "node:test";
import assert from "node:assert/strict";

import {
  formatCurrency,
  formatDecimal,
  formatPercent,
  formatPrice,
  formatQuantity,
  formatSignedCurrency,
  formatUtcDateTime,
  formatUtcTime,
} from "../js/formatters.js";

test("Brazilian trading numbers use decimal comma and grouped thousands", () => {
  assert.equal(formatQuantity(1234567), "1.234.567");
  assert.equal(formatPrice(1234.5), "1.234,50");
  assert.equal(formatDecimal(0.0125), "0,0125");
  assert.equal(formatPercent(0.125), "12,5%");
});

test("BRL values carry an explicit currency marker and signed P&L", () => {
  assert.equal(formatCurrency(1234.5), "R$ 1.234,50");
  assert.equal(formatSignedCurrency(1234.5), "+R$ 1.234,50");
  assert.equal(formatSignedCurrency(-25.5), "-R$ 25,50");
});

test("UTC timestamps use Brazilian date order without losing the timezone", () => {
  assert.equal(
    formatUtcDateTime("2026-07-18T04:30:02.318Z"),
    "18/07/2026, 04:30:02 UTC",
  );
  assert.equal(
    formatUtcTime("2026-07-18T04:30:02.318Z", { fractionalSecondDigits: 3 }),
    "04:30:02,318",
  );
});

test("invalid and absent values use deliberate fallbacks", () => {
  assert.equal(formatPrice(null), "—");
  assert.equal(formatCurrency(undefined), "R$ —");
  assert.equal(formatUtcDateTime("not-a-date", { fallback: "unknown" }), "unknown");
});
