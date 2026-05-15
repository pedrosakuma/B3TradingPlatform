// Q1.4 (#256) review pass-1 — `Expired` is not a backend `OrderStatus`.
// The GTD-expiry pipeline (#255) emits an `ExecKind.Expired` execution
// event but the order's terminal `Status` is `Cancelled` (the GTD
// scheduler routes through the cancel pipeline). This test pins the
// FE surfaces that depend on that fact:
//   1. `TERMINAL_ORDER_STATUSES` does NOT include "Expired".
//   2. The blotter status-filter `<select>` does NOT offer Expired.
//   3. A row whose GTD fired then cancelled is treated as terminal
//      via its actual `Cancelled` status.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

import { isTerminalOrderStatus } from "../js/state.js";

test("isTerminalOrderStatus: Cancelled is terminal", () => {
  assert.equal(isTerminalOrderStatus("Cancelled"), true);
});

test("isTerminalOrderStatus: Expired is NOT terminal (no such backend OrderStatus)", () => {
  assert.equal(isTerminalOrderStatus("Expired"), false);
});

test("isTerminalOrderStatus: real terminal set matches backend OrderStatus", () => {
  for (const s of ["Filled", "Cancelled", "Rejected", "Replaced"]) {
    assert.equal(isTerminalOrderStatus(s), true, `${s} should be terminal`);
  }
  for (const s of ["New", "PartiallyFilled", "PendingNew", "PendingCancel"]) {
    assert.equal(isTerminalOrderStatus(s), false, `${s} should NOT be terminal`);
  }
});

test("blotter status filter <select> no longer offers Expired", () => {
  const html = readFileSync(new URL("../index.html", import.meta.url), "utf8");
  // Locate the blotter status filter select. It's the only <select>
  // wrapping the canonical OrderStatus options New/PartiallyFilled/Filled.
  const m = html.match(/<select[^>]*id="blotter-filter-status"[\s\S]*?<\/select>/);
  assert.ok(m, "expected a blotter-filter-status <select> in index.html");
  assert.doesNotMatch(m[0], /value="Expired"/);
  // Sanity: the legitimate options are still there.
  assert.match(m[0], /value="Cancelled"/);
});

test("GTD-expiry → Cancelled order is treated as terminal", () => {
  // Simulates the post-pipeline shape the FE sees after the GTD
  // scheduler cancels the order: status=Cancelled (no fictitious
  // "Expired" status). The blotter renderer keys "freeze the row"
  // off isTerminalOrderStatus.
  const order = { clOrdId: "X1", status: "Cancelled", tif: "GTD" };
  assert.equal(isTerminalOrderStatus(order.status), true);
});
