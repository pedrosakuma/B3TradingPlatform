// Q2.6 (#273). History tab — state reducer + filter/pagination tests.
//
// Coverage:
//   * applyHistoryOrdersPage replaces on reset, appends without.
//   * nextCursor surfaces correctly; null terminates.
//   * setHistoryFilters normalizes symbol to uppercase and clears empties.
//   * loadMore round-trip via a stubbed fetch threads the cursor.
//
// Pure node:test, no DOM — same harness as state-modify.test.mjs.

import { test } from "node:test";
import assert from "node:assert/strict";

let n = 0;
async function freshState() {
  n += 1;
  return await import(`../js/state.js?bust=h${n}`);
}

test("historyOrders / historyExecutions start empty", async () => {
  const s = await freshState();
  const st = s.getState();
  assert.deepEqual(st.historyOrders,     { items: [], nextCursor: null, loading: false });
  assert.deepEqual(st.historyExecutions, { items: [], nextCursor: null, loading: false });
  assert.deepEqual(st.historyFilters,    { from: "", to: "", symbol: "" });
});

test("applyHistoryOrdersPage replaces on reset and surfaces nextCursor", async () => {
  const s = await freshState();
  s.applyHistoryOrdersPage({
    items: [{ clOrdId: "A" }, { clOrdId: "B" }],
    nextCursor: "cur-1",
    reset: true,
  });
  let h = s.getState().historyOrders;
  assert.equal(h.items.length, 2);
  assert.equal(h.nextCursor, "cur-1");
  assert.equal(h.loading, false);

  // Reset again drops previous items.
  s.applyHistoryOrdersPage({
    items: [{ clOrdId: "X" }],
    nextCursor: null,
    reset: true,
  });
  h = s.getState().historyOrders;
  assert.equal(h.items.length, 1);
  assert.equal(h.items[0].clOrdId, "X");
  assert.equal(h.nextCursor, null);
});

test("applyHistoryOrdersPage appends without reset (pagination)", async () => {
  const s = await freshState();
  s.applyHistoryOrdersPage({ items: [{ clOrdId: "A" }], nextCursor: "c1", reset: true });
  s.applyHistoryOrdersPage({ items: [{ clOrdId: "B" }, { clOrdId: "C" }], nextCursor: "c2" });
  const h = s.getState().historyOrders;
  assert.deepEqual(h.items.map(x => x.clOrdId), ["A", "B", "C"]);
  assert.equal(h.nextCursor, "c2");

  // Final page nulls the cursor — UI hides the "Load more" button.
  s.applyHistoryOrdersPage({ items: [{ clOrdId: "D" }], nextCursor: null });
  assert.equal(s.getState().historyOrders.nextCursor, null);
  assert.equal(s.getState().historyOrders.items.length, 4);
});

test("setHistoryFilters normalises symbol to uppercase, defaults to empty strings", async () => {
  const s = await freshState();
  s.setHistoryFilters({ from: "2025-01-01", to: "2025-01-31", symbol: "petr4" });
  let f = s.getState().historyFilters;
  assert.equal(f.symbol, "PETR4");
  assert.equal(f.from, "2025-01-01");
  assert.equal(f.to, "2025-01-31");

  // Partial / missing → coerced to "".
  s.setHistoryFilters({ symbol: "  vale3  " });
  f = s.getState().historyFilters;
  assert.equal(f.symbol, "VALE3");
  assert.equal(f.from, "");
  assert.equal(f.to, "");

  s.setHistoryFilters(null);
  assert.deepEqual(s.getState().historyFilters, { from: "", to: "", symbol: "" });
});

test("setHistoryOrdersLoading flips the busy flag without losing the buffer", async () => {
  const s = await freshState();
  s.applyHistoryOrdersPage({ items: [{ clOrdId: "A" }], nextCursor: "c1", reset: true });
  s.setHistoryOrdersLoading(true);
  assert.equal(s.getState().historyOrders.loading, true);
  assert.equal(s.getState().historyOrders.items.length, 1);
  s.setHistoryOrdersLoading(false);
  assert.equal(s.getState().historyOrders.loading, false);
});

test("getOrdersHistory threads from/to/symbol/cursor/limit into the URL", async () => {
  const { getOrdersHistory } = await import("../js/protocol.js");
  const calls = [];
  globalThis.fetch = async (url) => {
    calls.push(String(url));
    return {
      ok: true,
      status: 200,
      text: async () => JSON.stringify({ items: [{ clOrdId: "A" }], nextCursor: "next-1" }),
    };
  };
  const page = await getOrdersHistory("http://host", "tok", {
    from: "2025-01-01T00:00:00Z",
    to:   "2025-01-31T23:59:59Z",
    symbol: "PETR4",
    cursor: "abc",
    limit: 50,
  });
  assert.equal(calls.length, 1);
  const u = new URL(calls[0]);
  assert.equal(u.pathname, "/api/orders/history");
  assert.equal(u.searchParams.get("from"),   "2025-01-01T00:00:00Z");
  assert.equal(u.searchParams.get("to"),     "2025-01-31T23:59:59Z");
  assert.equal(u.searchParams.get("symbol"), "PETR4");
  assert.equal(u.searchParams.get("cursor"), "abc");
  assert.equal(u.searchParams.get("limit"),  "50");
  assert.deepEqual(page.items, [{ clOrdId: "A" }]);
  assert.equal(page.nextCursor, "next-1");
});

test("getOrdersHistory omits unset query params", async () => {
  const { getOrdersHistory } = await import("../js/protocol.js");
  let captured = null;
  globalThis.fetch = async (url) => {
    captured = String(url);
    return { ok: true, status: 200, text: async () => "{}" };
  };
  await getOrdersHistory("http://host", "tok", {});
  const u = new URL(captured);
  // No query params at all when filters/cursor/limit are unset.
  assert.equal([...u.searchParams.keys()].length, 0);
});

test("resetHistory clears both buffers", async () => {
  const s = await freshState();
  s.applyHistoryOrdersPage({ items: [{ clOrdId: "A" }], nextCursor: "c1", reset: true });
  s.applyHistoryExecutionsPage({ items: [{ clOrdId: "X" }], nextCursor: "c2", reset: true });
  s.resetHistory();
  assert.equal(s.getState().historyOrders.items.length, 0);
  assert.equal(s.getState().historyExecutions.items.length, 0);
  assert.equal(s.getState().historyOrders.nextCursor, null);
  assert.equal(s.getState().historyExecutions.nextCursor, null);
});

// P2 regression. An in-flight history-list request must NOT land in
// the buffer of a freshly-changed filter (or a resetHistory). The
// caller snapshots the historyGeneration at issue time and passes it
// as `ifGeneration`; setHistoryFilters and resetHistory both bump the
// counter, so a stale response is dropped silently.
test("stale history page (filter changed mid-flight) is dropped via ifGeneration", async () => {
  const s = await freshState();
  // Caller captures the generation at request issue time.
  const gen = s.getHistoryGeneration();
  // Filter changes while the request is in-flight — bumps the generation.
  s.setHistoryFilters({ from: "2025-01-01", to: "2025-01-31", symbol: "PETR4" });
  // Stale response (old filter) tries to land — must be a no-op.
  s.applyHistoryOrdersPage({
    items: [{ clOrdId: "STALE-A" }, { clOrdId: "STALE-B" }],
    nextCursor: "stale-cursor",
    reset: true,
    ifGeneration: gen,
  });
  const h = s.getState().historyOrders;
  assert.equal(h.items.length, 0, "stale page must not populate the buffer");
  assert.equal(h.nextCursor, null, "stale page must not seed the cursor");

  // A fresh request under the new generation lands normally.
  const newGen = s.getHistoryGeneration();
  s.applyHistoryOrdersPage({
    items: [{ clOrdId: "FRESH" }],
    nextCursor: "next",
    reset: true,
    ifGeneration: newGen,
  });
  assert.equal(s.getState().historyOrders.items.length, 1);
  assert.equal(s.getState().historyOrders.items[0].clOrdId, "FRESH");
});

test("stale history executions page is dropped after resetHistory bumps the generation", async () => {
  const s = await freshState();
  const gen = s.getHistoryGeneration();
  s.resetHistory();
  s.applyHistoryExecutionsPage({
    items: [{ clOrdId: "STALE" }],
    nextCursor: "x",
    reset: true,
    ifGeneration: gen,
  });
  assert.equal(s.getState().historyExecutions.items.length, 0);
});

test("setHistoryFilters and resetHistory bump the history generation", async () => {
  const s = await freshState();
  const g0 = s.getHistoryGeneration();
  s.setHistoryFilters({ symbol: "PETR4" });
  const g1 = s.getHistoryGeneration();
  assert.ok(g1 > g0);
  s.resetHistory();
  const g2 = s.getHistoryGeneration();
  assert.ok(g2 > g1);
});
