// Modify-modal UX-vs-wire conversion (#421 follow-up).
//
// The modal exposes "new remaining quantity" to the trader (much
// more intuitive after a partial fill than "new total"), but the
// wire OrderCancelReplaceRequest still carries the FIX-conformant
// OrderQty (38) = cumQty + newLeaves with the invariant
// OrderQty ≥ CumQty. These pure helpers own that translation; the
// modal UI just shells out to them, so locking them down here
// guards both "what shows up in the input" and "what hits the wire"
// without needing a DOM harness.

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  modifyModalDefaultLeaves,
  computeWireOrderQty,
} from "../js/ui.js";

function order(over = {}) {
  return {
    clOrdId: "1",
    symbol: "PETR4",
    side: "Buy",
    type: "Limit",
    status: "New",
    quantity: 200,
    leavesQuantity: 200,
    cumulativeQuantity: 0,
    price: 32.5,
    ...over,
  };
}

test("modifyModalDefaultLeaves: fresh order ⇒ leaves == quantity", () => {
  assert.equal(modifyModalDefaultLeaves(order()), 200);
});

test("modifyModalDefaultLeaves: partially-filled order ⇒ leaves (not total)", () => {
  // This is the #421 case in the screenshot: order qty=200 with
  // cum=100 should pre-fill the input with 100, not 200.
  const o = order({ status: "PartiallyFilled", leavesQuantity: 100, cumulativeQuantity: 100 });
  assert.equal(modifyModalDefaultLeaves(o), 100);
});

test("modifyModalDefaultLeaves: falls back to quantity when leaves is missing/zero", () => {
  assert.equal(modifyModalDefaultLeaves(order({ leavesQuantity: 0 })), 200);
  assert.equal(modifyModalDefaultLeaves(order({ leavesQuantity: null })), 200);
  assert.equal(modifyModalDefaultLeaves(order({ leavesQuantity: undefined })), 200);
});

test("modifyModalDefaultLeaves: returns empty string for missing input", () => {
  assert.equal(modifyModalDefaultLeaves(null), "");
  assert.equal(modifyModalDefaultLeaves(undefined), "");
  assert.equal(modifyModalDefaultLeaves({}), "");
});

test("computeWireOrderQty: fresh order, cum=0 ⇒ wire == new leaves", () => {
  assert.equal(computeWireOrderQty(150, 0), 150);
});

test("computeWireOrderQty: partial fill ⇒ wire = cum + new leaves (FIX invariant)", () => {
  // cum=100 already filled; trader wants 80 still working ⇒ wire OrderQty = 180.
  assert.equal(computeWireOrderQty(80, 100), 180);
  // Growing the remaining ⇒ wire grows too.
  assert.equal(computeWireOrderQty(500, 100), 600);
});

test("computeWireOrderQty: returns NaN on invalid new-leaves input", () => {
  assert.ok(Number.isNaN(computeWireOrderQty(0, 100)));     // not positive
  assert.ok(Number.isNaN(computeWireOrderQty(-5, 100)));    // negative
  assert.ok(Number.isNaN(computeWireOrderQty(1.5, 100)));   // non-integer
  assert.ok(Number.isNaN(computeWireOrderQty("abc", 100))); // not a number
  assert.ok(Number.isNaN(computeWireOrderQty(null, 100)));
  assert.ok(Number.isNaN(computeWireOrderQty(undefined, 100)));
});

test("computeWireOrderQty: returns NaN on invalid cum input", () => {
  assert.ok(Number.isNaN(computeWireOrderQty(50, -1)));
  assert.ok(Number.isNaN(computeWireOrderQty(50, "abc")));
  // null/undefined cum is permissive: a missing cum from state
  // safely defaults to 0 at the call site (form.dataset.cumqty),
  // so the pure helper only rejects actively-bad numbers.
});

test("computeWireOrderQty: wire qty always ≥ cum (FIX invariant by construction)", () => {
  for (const [lv, cum] of [[1, 0], [1, 100], [100, 50], [200, 200]]) {
    const wire = computeWireOrderQty(lv, cum);
    assert.ok(wire >= cum, `wire ${wire} must be ≥ cum ${cum}`);
    assert.equal(wire, cum + lv);
  }
});
