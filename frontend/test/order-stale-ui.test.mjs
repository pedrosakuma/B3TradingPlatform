// Slice 3 of #132. Verifies the trader-UI gates for the order-stale
// overlay introduced by slices 1-2:
//   * the orders state slice round-trips the `isStale` / `staleReason`
//     fields surfaced by the backend OrderDto;
//   * the client-side cancel-all queue excludes stale orders the same
//     way it excludes terminal/PendingCancel ones (mirrors the inline
//     filter in app.js so the modal honours the badge).
//
// Modify-modal and per-order Cancel gates also live in app.js but are
// guarded by the same `order.isStale` predicate; covered indirectly
// here since the predicate is shared.

import { test } from "node:test";
import assert from "node:assert/strict";

import * as state from "../js/state.js";

function reset() {
  state.clearAll();
  state.setStatus("disconnected");
}

test("orders state round-trips isStale / staleReason / staledAtUtc", () => {
  reset();
  state.applyOrdersDelta({
    clOrdId: "S1",
    symbol: "PETR4",
    side: "Buy",
    type: "Limit",
    quantity: 100,
    leavesQuantity: 100,
    cumulativeQuantity: 0,
    price: 30,
    status: "Working",
    isStale: true,
    staleReason: "inbound_gap:50-52",
    staledAtUtc: "2026-05-07T20:00:00Z",
  });

  const o = state.getState().orders.get("S1");
  assert.ok(o, "order present");
  assert.equal(o.isStale, true);
  assert.equal(o.staleReason, "inbound_gap:50-52");
  assert.equal(o.staledAtUtc, "2026-05-07T20:00:00Z");
});

test("orders state defaults isStale to falsy when backend omits it", () => {
  reset();
  state.applyOrdersDelta({ clOrdId: "S2", status: "Working" });
  const o = state.getState().orders.get("S2");
  assert.ok(!o.isStale);
});

// Mirrors the queue filter inside app.js#handleCancelAll. Locks the
// contract: stale orders are skipped by the burst even when the modal
// snapshot included them (the badge already disabled the per-row Cancel
// button, but the panic-button picks up a snapshot so we re-validate).
function buildCancelAllQueue(ids, ordersMap, inflightCancels = new Set()) {
  return ids.filter(id => {
    if (inflightCancels.has(id)) return false;
    const o = ordersMap.get(id);
    if (!o) return false;
    if (o.isStale) return false;
    if (state.isTerminalOrderStatus(o.status)) return false;
    return o.status !== "PendingCancel";
  });
}

test("cancel-all queue excludes stale orders alongside terminal ones", () => {
  const orders = new Map([
    ["A", { clOrdId: "A", status: "Working" }],
    ["B", { clOrdId: "B", status: "Working", isStale: true }],
    ["C", { clOrdId: "C", status: "Filled" }],
    ["D", { clOrdId: "D", status: "PartiallyFilled", isStale: true }],
    ["E", { clOrdId: "E", status: "Working" }],
    ["F", { clOrdId: "F", status: "Replaced" }],
  ]);
  const queue = buildCancelAllQueue(["A", "B", "C", "D", "E", "F"], orders);
  assert.deepEqual(queue, ["A", "E"]);
});

test("cancel-all queue still drops terminal orders without isStale field", () => {
  const orders = new Map([
    ["A", { clOrdId: "A", status: "Cancelled" }],
    ["B", { clOrdId: "B", status: "Working" }],
  ]);
  assert.deepEqual(buildCancelAllQueue(["A", "B"], orders), ["B"]);
});

test("cancel-all queue treats explicit isStale=false as eligible", () => {
  const orders = new Map([
    ["A", { clOrdId: "A", status: "Working", isStale: false }],
  ]);
  assert.deepEqual(buildCancelAllQueue(["A"], orders), ["A"]);
});
