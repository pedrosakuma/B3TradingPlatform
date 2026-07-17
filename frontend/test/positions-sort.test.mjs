// #342: Positions sort helper — three columns × two directions, with the
// |net| column treating longs and shorts of equal magnitude as adjacent.
import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";
installDomStub({ ids: {} });

const { sortPositionsInPlace, reconcilePositionExpiryFilter } = await import("../js/ui.js");

const rows = () => [
  { symbol: "PETR4", netQuantity:  300, averageEntryPrice: 32.10 },
  { symbol: "VALE3", netQuantity: -800, averageEntryPrice: 65.50 },
  { symbol: "ITUB4", netQuantity:  500, averageEntryPrice:  9.20 },
];

test("absNet desc puts largest exposure first, regardless of side", () => {
  const r = rows();
  sortPositionsInPlace(r, { col: "absNet", dir: "desc" });
  assert.deepEqual(r.map(p => p.symbol), ["VALE3", "ITUB4", "PETR4"]);
});

test("absNet asc puts smallest exposure first", () => {
  const r = rows();
  sortPositionsInPlace(r, { col: "absNet", dir: "asc" });
  assert.deepEqual(r.map(p => p.symbol), ["PETR4", "ITUB4", "VALE3"]);
});

test("symbol asc / desc are lexicographic", () => {
  const a = rows(); sortPositionsInPlace(a, { col: "symbol", dir: "asc"  });
  assert.deepEqual(a.map(p => p.symbol), ["ITUB4", "PETR4", "VALE3"]);
  const d = rows(); sortPositionsInPlace(d, { col: "symbol", dir: "desc" });
  assert.deepEqual(d.map(p => p.symbol), ["VALE3", "PETR4", "ITUB4"]);
});

test("price column sorts by averageEntryPrice", () => {
  const r = rows();
  sortPositionsInPlace(r, { col: "price", dir: "asc" });
  assert.deepEqual(r.map(p => p.symbol), ["ITUB4", "PETR4", "VALE3"]);
});

test("stale option expiry filter resets when matching positions disappear", () => {
  const positions = [
    { symbol: "PETR4", securityType: "Equity", netQuantity: 100 },
    {
      symbol: "PETRA300",
      securityType: "Option",
      optionExpirationDate: "2026-09-18",
      netQuantity: 1,
    },
  ];

  assert.equal(
    reconcilePositionExpiryFilter(positions, "2026-08-21"),
    null,
    "an expiry with no live option position must not hide the equity row",
  );
  assert.equal(
    reconcilePositionExpiryFilter(positions, "2026-09-18"),
    "2026-09-18",
    "a current option expiry remains selected",
  );
});
