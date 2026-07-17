// P2 regressions for #273.
//
//   1. clearAll() must bump BOTH _pnlEpoch and _historyGeneration.
//      Otherwise a pre-reconnect in-flight REST P&L or history
//      response can land AFTER clearAll() at a logout/session boundary
//      and repopulate stale rows under the now-clean state.
//
//   2. refreshPnl() bumps the pnl epoch BEFORE issuing the fetch, so
//      two concurrent REST refreshes never capture the same epoch.
//      The OLDER (slower) response then sees an epoch mismatch on
//      apply and is dropped — protects against REST-vs-REST races
//      where an older response could otherwise overwrite a newer one.

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({
  ids: {
    "pnl-total-realized":    { tag: "span" },
    "pnl-total-unrealized":  { tag: "span" },
    "pnl-live":              { tag: "span", hidden: true },
    "pnl-rows":              { tag: "tbody" },
    "history-feedback":      { tag: "p", hidden: true },
    "history-orders-body":         { tag: "tbody" },
    "history-orders-more":         { tag: "button", hidden: true },
    "history-executions-body":     { tag: "tbody" },
    "history-executions-more":     { tag: "button", hidden: true },
    "statement-status":            { tag: "p", hidden: true },
    "statement-json-modal":        { tag: "div", hidden: true },
    "statement-json-body":         { tag: "pre" },
  },
});

let n = 0;
async function freshState() {
  n += 1;
  return await import(`../js/state.js?bust=clrrc${n}`);
}

test("clearAll bumps the pnl epoch", async () => {
  const s = await freshState();
  const before = s.getPnlEpoch();
  s.clearAll();
  assert.ok(s.getPnlEpoch() > before, "clearAll must advance the pnl epoch");
});

test("clearAll bumps the history generation", async () => {
  const s = await freshState();
  const before = s.getHistoryGeneration();
  s.clearAll();
  assert.ok(
    s.getHistoryGeneration() > before,
    "clearAll must advance the history generation",
  );
});

test("stale REST pnl snapshot captured before clearAll is dropped on apply", async () => {
  const s = await freshState();
  // 1. refreshPnl() captures epoch N at request-issue time.
  const epochAtRequest = s.getPnlEpoch();
  // 2. A logout/session boundary clears all per-user state.
  s.clearAll();
  assert.equal(s.getState().pnl, null, "state cleared after clearAll");
  // 3. The pre-reconnect REST response now resolves with stale data.
  //    Without the epoch bump in clearAll, this would repopulate the
  //    pnl slice with the previous session's data.
  s.applyPnlSnapshot({
    realized:   [{ symbol: "PETR4", value: 999 }],
    unrealized: [{ symbol: "VALE3", value: 12, refPrice: 60, position: 100, avgPrice: 59.88 }],
    totalRealized: 999,
    totalUnrealized: 12,
  }, { ifEpoch: epochAtRequest });
  assert.equal(s.getState().pnl, null, "stale REST snapshot must not repopulate cleared state");
});

test("stale REST history page captured before clearAll is dropped on apply", async () => {
  const s = await freshState();
  const genAtRequest = s.getHistoryGeneration();
  s.clearAll();
  assert.deepEqual(s.getState().historyOrders.items, []);
  assert.deepEqual(s.getState().historyExecutions.items, []);
  // Pre-reconnect REST page resolves with the previous session's rows.
  s.applyHistoryOrdersPage({
    items: [{ clOrdId: "STALE-1", symbol: "PETR4" }],
    nextCursor: null,
    reset: true,
    ifGeneration: genAtRequest,
  });
  s.applyHistoryExecutionsPage({
    items: [{ execId: "STALE-EX-1", symbol: "PETR4" }],
    nextCursor: null,
    reset: true,
    ifGeneration: genAtRequest,
  });
  assert.deepEqual(
    s.getState().historyOrders.items, [],
    "stale orders page must not repopulate cleared history",
  );
  assert.deepEqual(
    s.getState().historyExecutions.items, [],
    "stale executions page must not repopulate cleared history",
  );
});

// P2 — REST-vs-REST race. refreshPnl() bumps the pnl epoch BEFORE
// issuing its fetch and captures the NEW epoch immediately, so two
// concurrent calls own distinct epochs. If the OLDER call's response
// races back AFTER the newer one has applied, its apply sees an epoch
// mismatch and is dropped.
test("REST-vs-REST race: older refreshPnl response is dropped when it resolves last", async () => {
  const s = await freshState();

  // Simulate refreshPnl() call #1 (the older one).
  s.bumpPnlEpoch();
  const epoch1 = s.getPnlEpoch();

  // Simulate refreshPnl() call #2 issued before #1 resolves.
  s.bumpPnlEpoch();
  const epoch2 = s.getPnlEpoch();
  assert.ok(epoch2 > epoch1, "second refreshPnl must capture a strictly newer epoch");

  // Resolve OUT OF ORDER: call #2's response lands FIRST with the
  // newer backend state.
  s.applyPnlSnapshot({
    realized:   [{ symbol: "PETR4", value: 200 }],
    unrealized: [{ symbol: "PETR4", value: 5, refPrice: 30.05, position: 100, avgPrice: 30.0 }],
    totalRealized: 200,
    totalUnrealized: 5,
  }, { ifEpoch: epoch2 });

  // Then call #1's response lands LAST with older backend state.
  // Without the per-call epoch bump it would clobber #2's newer data.
  s.applyPnlSnapshot({
    realized:   [{ symbol: "PETR4", value: 50 }],
    unrealized: [],
    totalRealized: 50,
    totalUnrealized: 0,
  }, { ifEpoch: epoch1 });

  const p = s.getState().pnl;
  assert.equal(p.totalRealized, 200, "newer response's totals must survive");
  assert.equal(p.totalUnrealized, 5);
  assert.equal(p.realized.length, 1);
  assert.equal(p.realized[0].value, 200);
  assert.equal(p.unrealized.length, 1);
  assert.equal(p.unrealized[0].position, 100);
});

// P2 — REST-vs-REST race on history. refreshHistoryAll() bumps the
// history generation BEFORE issuing its fetches and the inner loadMore
// captures the NEW generation immediately, so two concurrent refreshes
// own distinct generations. If the OLDER refresh's response races back
// AFTER the newer one has applied, its apply sees a generation mismatch
// and is dropped — for BOTH orders and executions, on reset pages.
test("REST-vs-REST race: older refreshHistoryAll orders page is dropped when it resolves last", async () => {
  const s = await freshState();

  // Simulate refreshHistoryAll() call #1 (the older one): bump, capture.
  s.bumpHistoryGeneration();
  const gen1 = s.getHistoryGeneration();

  // Simulate refreshHistoryAll() call #2 issued before #1 resolves.
  s.bumpHistoryGeneration();
  const gen2 = s.getHistoryGeneration();
  assert.ok(gen2 > gen1, "second refreshHistoryAll must capture a strictly newer generation");

  // Resolve OUT OF ORDER: call #2's response lands FIRST with the
  // newer backend rows.
  s.applyHistoryOrdersPage({
    items: [{ clOrdId: "NEW-1", symbol: "PETR4" }],
    nextCursor: null,
    reset: true,
    ifGeneration: gen2,
  });
  assert.equal(s.getState().historyOrders.items.length, 1);
  assert.equal(s.getState().historyOrders.items[0].clOrdId, "NEW-1");

  // Then call #1's response lands LAST with older backend rows.
  // Without the per-refresh generation bump it would clobber #2's
  // newer reset page.
  s.applyHistoryOrdersPage({
    items: [{ clOrdId: "OLD-1", symbol: "PETR4" }, { clOrdId: "OLD-2", symbol: "VALE3" }],
    nextCursor: "cursor-old",
    reset: true,
    ifGeneration: gen1,
  });

  const orders = s.getState().historyOrders;
  assert.equal(orders.items.length, 1, "older response must not overwrite newer reset page");
  assert.equal(orders.items[0].clOrdId, "NEW-1");
  assert.equal(orders.nextCursor, null);
});

test("REST-vs-REST race: older refreshHistoryAll executions page is dropped when it resolves last", async () => {
  const s = await freshState();

  s.bumpHistoryGeneration();
  const gen1 = s.getHistoryGeneration();

  s.bumpHistoryGeneration();
  const gen2 = s.getHistoryGeneration();
  assert.ok(gen2 > gen1);

  s.applyHistoryExecutionsPage({
    items: [{ execId: "NEW-EX-1", symbol: "PETR4" }],
    nextCursor: null,
    reset: true,
    ifGeneration: gen2,
  });
  assert.equal(s.getState().historyExecutions.items.length, 1);
  assert.equal(s.getState().historyExecutions.items[0].execId, "NEW-EX-1");

  s.applyHistoryExecutionsPage({
    items: [{ execId: "OLD-EX-1", symbol: "PETR4" }, { execId: "OLD-EX-2", symbol: "VALE3" }],
    nextCursor: "cursor-old",
    reset: true,
    ifGeneration: gen1,
  });

  const execs = s.getState().historyExecutions;
  assert.equal(execs.items.length, 1, "older response must not overwrite newer reset page");
  assert.equal(execs.items[0].execId, "NEW-EX-1");
  assert.equal(execs.nextCursor, null);
});
