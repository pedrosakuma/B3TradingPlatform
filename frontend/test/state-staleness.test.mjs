// T2 — verify the notify hook stamps lastWsActivity / lastMdActivity
// for the right slices, so the stale overlay can rely on it.

import { test } from "node:test";
import assert from "node:assert/strict";

import * as state from "../js/state.js";

function reset() {
  state.clearAll();
  state.setStatus("disconnected");
}

test("WS data setters stamp lastWsActivity and not lastMdActivity", () => {
  reset();
  const before = state.getState().lastWsActivity;
  state.applyOrdersDelta({ clOrdId: "X1", symbol: "PETR4", status: "New" });
  const ws = state.getState().lastWsActivity;
  const md = state.getState().lastMdActivity;
  assert.notEqual(ws, before);
  assert.equal(typeof ws, "number");
  assert.equal(md, null);
});

test("MD setters stamp lastMdActivity and not lastWsActivity", () => {
  reset();
  state.applyMdTrade({ symbol: "PETR4", price: 32.5, qty: 100, tradeId: 1 });
  const ws = state.getState().lastWsActivity;
  const md = state.getState().lastMdActivity;
  assert.equal(typeof md, "number");
  assert.equal(ws, null);
});

test("setStatus and setMarketDataStatus do NOT stamp activity timestamps", () => {
  reset();
  state.setStatus("connected");
  state.setMarketDataStatus("connected");
  assert.equal(state.getState().lastWsActivity, null);
  assert.equal(state.getState().lastMdActivity, null);
});

test("clearAll resets both activity timestamps", () => {
  reset();
  state.applyOrdersDelta({ clOrdId: "X2", symbol: "VALE3", status: "New" });
  state.applyMdTrade({ symbol: "VALE3", price: 65, qty: 100, tradeId: 1 });
  assert.notEqual(state.getState().lastWsActivity, null);
  assert.notEqual(state.getState().lastMdActivity, null);
  state.clearAll();
  assert.equal(state.getState().lastWsActivity, null);
  assert.equal(state.getState().lastMdActivity, null);
});

test("empty executions snapshot still stamps WS activity (proof-of-life)", () => {
  reset();
  state.applyExecutionsSnapshot([]);
  assert.equal(typeof state.getState().lastWsActivity, "number");
});
