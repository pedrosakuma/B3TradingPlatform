// T4 — lock the rulesFor() contract that syncTicketRules() relies on
// in the order ticket. Defensive smoke tests so a future refactor
// doesn't change the shape and silently break the qty step / hint.

import { test } from "node:test";
import assert from "node:assert/strict";

import { rulesFor, validateOrder } from "../js/validation.js";

test("rulesFor returns lot/tick/threshold defaults for unknown symbol", () => {
  const r = rulesFor("PETR4");
  assert.equal(r.lotSize, 100);
  assert.equal(r.tickSize, 0.01);
  assert.equal(typeof r.fatFingerThreshold, "number");
});

test("rulesFor defaults apply to empty / null symbol", () => {
  for (const s of ["", null, undefined]) {
    const r = rulesFor(s);
    assert.equal(r.lotSize, 100);
    assert.equal(r.tickSize, 0.01);
  }
});

test("rulesFor is case-insensitive on symbol", () => {
  const upper = rulesFor("VALE3");
  const lower = rulesFor("vale3");
  assert.deepEqual(upper, lower);
});

test("validateOrder rejects sub-lot quantities using rulesFor", () => {
  const err = validateOrder({ symbol: "PETR4", quantity: 150, type: "Market" });
  assert.ok(err);
  assert.equal(err.code, "lot_size");
});

test("validateOrder accepts lot-aligned market quantities", () => {
  const err = validateOrder({ symbol: "PETR4", quantity: 100, type: "Market" });
  assert.equal(err, null);
});
