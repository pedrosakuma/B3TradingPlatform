// Q2.6 (#273). P&L panel — snapshot/delta reducer + renderer tests.
//
// Coverage:
//   * applyPnlSnapshot populates state.pnl with totals + per-symbol rows.
//   * applyPnlDelta replaces wholesale (same shape as snapshot — backend
//     re-projects on every fill, see PnlRefPriceFanOut / sink).
//   * renderPnl produces the totals + per-symbol rows in the DOM stub.
//   * clearPnl drops the slice.

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
  return await import(`../js/state.js?bust=p${n}`);
}

test("state.pnl starts null", async () => {
  const s = await freshState();
  assert.equal(s.getState().pnl, null);
});

test("applyPnlSnapshot populates totals and arrays", async () => {
  const s = await freshState();
  s.applyPnlSnapshot({
    realized:   [{ symbol: "PETR4", value: 12.5 }],
    unrealized: [{ symbol: "VALE3", value: -3.25, refPrice: 60.0, position: 100, avgPrice: 60.0325 }],
    totalRealized: 12.5,
    totalUnrealized: -3.25,
  });
  const p = s.getState().pnl;
  assert.equal(p.totalRealized, 12.5);
  assert.equal(p.totalUnrealized, -3.25);
  assert.equal(p.realized.length, 1);
  assert.equal(p.unrealized.length, 1);
  assert.equal(p.unrealized[0].symbol, "VALE3");
  assert.equal(p.unrealized[0].position, 100);
});

test("applyPnlDelta replaces wholesale (same shape as snapshot)", async () => {
  const s = await freshState();
  s.applyPnlSnapshot({
    realized: [{ symbol: "PETR4", value: 10 }],
    unrealized: [],
    totalRealized: 10, totalUnrealized: 0,
  });
  // Delta carries the full re-projected DTO — backend re-projects on
  // every fill (see WebSocketExecutionEventSink). Apply replaces.
  s.applyPnlDelta({
    realized:   [{ symbol: "PETR4", value: 25 }, { symbol: "VALE3", value: -1 }],
    unrealized: [{ symbol: "VALE3", value: 0.5, refPrice: 60.5, position: 50, avgPrice: 60.49 }],
    totalRealized: 24,
    totalUnrealized: 0.5,
  });
  const p = s.getState().pnl;
  assert.equal(p.totalRealized, 24);
  assert.equal(p.totalUnrealized, 0.5);
  assert.equal(p.realized.length, 2);
  assert.equal(p.unrealized.length, 1);
});

test("applyPnlSnapshot tolerates partial / nullish payloads", async () => {
  const s = await freshState();
  s.applyPnlSnapshot({});
  const p = s.getState().pnl;
  assert.deepEqual(p.realized, []);
  assert.deepEqual(p.unrealized, []);
  assert.equal(p.totalRealized, 0);
  assert.equal(p.totalUnrealized, 0);

  s.applyPnlSnapshot(null);
  assert.equal(s.getState().pnl, null);
});

test("clearPnl drops the slice", async () => {
  const s = await freshState();
  s.applyPnlSnapshot({ realized: [], unrealized: [], totalRealized: 0, totalUnrealized: 0 });
  assert.ok(s.getState().pnl != null);
  s.clearPnl();
  assert.equal(s.getState().pnl, null);
});

test("renderPnl populates the DOM with totals + per-symbol rows after a snapshot", async () => {
  // Renderer reads via the canonical state module — use it directly so
  // historyUi.js (which imports the same canonical module) sees what
  // we wrote.
  const s = await import("../js/state.js");
  s.applyPnlSnapshot({
    realized:   [{ symbol: "PETR4", value: 100 }],
    unrealized: [{ symbol: "VALE3", value: -25.5, refPrice: 60, position: -200, avgPrice: 59.87 }],
    totalRealized: 100,
    totalUnrealized: -25.5,
  });
  const { renderPnl } = await import("../js/historyUi.js");
  renderPnl();

  const tr = document.getElementById("pnl-total-realized");
  const tu = document.getElementById("pnl-total-unrealized");
  const live = document.getElementById("pnl-live");
  const body = document.getElementById("pnl-rows");

  assert.match(tr.textContent, /100/);
  assert.match(tu.textContent, /25/);
  assert.equal(live.hidden, false, "live badge shown once pnl is populated");
  assert.match(body.innerHTML, /PETR4/);
  assert.match(body.innerHTML, /VALE3/);
});

test("renderPnl shows 'no data' placeholder when pnl is null", async () => {
  const s = await import("../js/state.js");
  s.clearPnl();
  const { renderPnl } = await import("../js/historyUi.js");
  renderPnl();
  const body = document.getElementById("pnl-rows");
  assert.match(body.innerHTML, /no P&amp;L data yet/);
});

test("WS delta after initial REST snapshot updates state.pnl (simulated end-to-end)", async () => {
  const s = await freshState();
  // 1) REST seed (GET /pnl/today wired in app.js via state.applyPnlSnapshot).
  s.applyPnlSnapshot({
    realized: [], unrealized: [], totalRealized: 0, totalUnrealized: 0,
  });
  // 2) WS delta from `pnl.me` channel — backend ships the full
  //    PnlTodayDto, so state.applyPnlDelta replaces.
  s.applyPnlDelta({
    realized:   [{ symbol: "PETR4", value: 250 }],
    unrealized: [{ symbol: "PETR4", value: 10, refPrice: 30.10, position: 100, avgPrice: 30.0 }],
    totalRealized: 250,
    totalUnrealized: 10,
  });
  const p = s.getState().pnl;
  assert.equal(p.totalRealized, 250);
  assert.equal(p.totalUnrealized, 10);
  assert.equal(p.unrealized[0].position, 100);
});
