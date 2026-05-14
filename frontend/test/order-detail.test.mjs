// Unit tests for the Order Detail modal helpers (#245).
//
// We don't exercise the DOM rendering (the rest of frontend/test/
// follows the same convention — pure function tests via node:test);
// the helpers covered here are the ones that can produce a wrong
// number that the trader will visibly read off the screen, so they
// must be locked down independently of the layout.

import { test } from "node:test";
import assert from "node:assert/strict";

import { vwapOf, executionsForClOrdId } from "../js/ui.js";

function ex(over = {}) {
  return {
    clOrdId: "X1",
    symbol: "PETR4",
    side: "Buy",
    status: "PartiallyFilled",
    kind: "PartialFill",
    leavesQuantity: 0,
    cumulativeQuantity: 0,
    lastQuantity: 0,
    lastPrice: 0,
    rejectReason: null,
    timestampUtc: "2026-05-07T20:00:00Z",
    isNativeStp: false,
    ...over,
  };
}

test("vwapOf returns null for empty input", () => {
  assert.equal(vwapOf([]), null);
  assert.equal(vwapOf(null), null);
  assert.equal(vwapOf(undefined), null);
});

test("vwapOf returns the price when there is exactly one fill", () => {
  const v = vwapOf([ex({ lastQuantity: 100, lastPrice: 32.5 })]);
  assert.equal(v, 32.5);
});

test("vwapOf computes the weighted average across mixed fills", () => {
  // 60 @ 32.50 + 40 @ 32.60 → (1950 + 1304) / 100 = 32.54
  const v = vwapOf([
    ex({ lastQuantity: 60, lastPrice: 32.5 }),
    ex({ lastQuantity: 40, lastPrice: 32.6 }),
  ]);
  assert.ok(Math.abs(v - 32.54) < 1e-9, `expected 32.54, got ${v}`);
});

test("vwapOf preserves the average when fills are equal qty", () => {
  // 2 fills of 100 @ 32.50 → VWAP 32.50
  const v = vwapOf([
    ex({ lastQuantity: 100, lastPrice: 32.5 }),
    ex({ lastQuantity: 100, lastPrice: 32.5 }),
  ]);
  assert.equal(v, 32.5);
});

test("vwapOf ignores transition ERs (lastQuantity == 0)", () => {
  const v = vwapOf([
    ex({ kind: "New", status: "Working", lastQuantity: 0, lastPrice: 0 }),
    ex({ lastQuantity: 100, lastPrice: 32.5 }),
    ex({ kind: "Replaced", status: "Replaced", lastQuantity: 0, lastPrice: 0 }),
  ]);
  assert.equal(v, 32.5);
});

test("vwapOf returns null when only zero-qty rows exist", () => {
  const v = vwapOf([
    ex({ kind: "New", lastQuantity: 0 }),
    ex({ kind: "Cancelled", lastQuantity: 0 }),
  ]);
  assert.equal(v, null);
});

test("vwapOf handles non-equal quantities correctly", () => {
  // 30 @ 10.00 + 70 @ 20.00 → (300 + 1400) / 100 = 17.00
  const v = vwapOf([
    ex({ lastQuantity: 30, lastPrice: 10 }),
    ex({ lastQuantity: 70, lastPrice: 20 }),
  ]);
  assert.equal(v, 17);
});

test("executionsForClOrdId filters by ClOrdID and preserves order", () => {
  const all = [
    ex({ clOrdId: "A", lastQuantity: 1, lastPrice: 10, timestampUtc: "2026-05-07T20:00:00Z" }),
    ex({ clOrdId: "B", lastQuantity: 1, lastPrice: 11, timestampUtc: "2026-05-07T20:00:01Z" }),
    ex({ clOrdId: "A", lastQuantity: 1, lastPrice: 12, timestampUtc: "2026-05-07T20:00:02Z" }),
    ex({ clOrdId: "C", lastQuantity: 1, lastPrice: 13, timestampUtc: "2026-05-07T20:00:03Z" }),
    ex({ clOrdId: "A", lastQuantity: 0, lastPrice: 0, kind: "New", timestampUtc: "2026-05-07T20:00:04Z" }),
  ];
  const onlyA = executionsForClOrdId(all, "A");
  assert.equal(onlyA.length, 3);
  assert.deepEqual(onlyA.map(e => e.timestampUtc), [
    "2026-05-07T20:00:00Z",
    "2026-05-07T20:00:02Z",
    "2026-05-07T20:00:04Z",
  ]);
});

test("executionsForClOrdId compares as strings (numeric ClOrdId from server)", () => {
  const all = [
    ex({ clOrdId: "12345" }),
    ex({ clOrdId: "12346" }),
  ];
  // Even if a caller passed a number (it shouldn't, but DTO surface
  // is loose), the filter must still match without coercion bugs.
  assert.equal(executionsForClOrdId(all, 12345).length, 1);
  assert.equal(executionsForClOrdId(all, "12345").length, 1);
  assert.equal(executionsForClOrdId(all, "missing").length, 0);
});

test("executionsForClOrdId returns empty for invalid input", () => {
  assert.deepEqual(executionsForClOrdId(null, "A"), []);
  assert.deepEqual(executionsForClOrdId([ex({ clOrdId: "A" })], null), []);
  assert.deepEqual(executionsForClOrdId([], "A"), []);
});
