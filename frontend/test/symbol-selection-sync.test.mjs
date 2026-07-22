// #694 — the watchlist's auto-pick-first symbol (state.js setWatchlist)
// used to set state.selectedSymbol directly, bypassing app.js's
// handleSelectSymbol. Only that handler auto-filled #ticket-symbol, and
// ui.js's renderTradingReadiness() derives its "Session phase" signal
// from #ticket-symbol's value (not state.selectedSymbol) — so on login,
// the topbar symbol dropdown showed a symbol while the order ticket and
// the Trading Readiness banner both stayed stuck on "no symbol selected"
// until the trader manually touched the dropdown.
//
// Fix: ui.js's renderForSlice now syncs #ticket-symbol + re-renders
// Trading Readiness on every "selectedSymbol" slice notification,
// regardless of what triggered it — this test drives the exact
// production wiring (state.subscribe(ui.renderForSlice)) through the
// auto-pick-first path, without ever calling the manual-selection
// handler.

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({
  ids: {
    "ticket-symbol":            { tag: "input" },
    "trading-readiness":        { tag: "div" },
    "trading-readiness-title":  { tag: "p" },
    "trading-readiness-message": { tag: "p" },
    "trading-readiness-phase":  { tag: "li" },
  },
});

const state = await import("../js/state.js");
const ui    = await import("../js/ui.js");

test("watchlist auto-pick-first symbol syncs #ticket-symbol without manual selection", () => {
  // Fresh selection: nothing picked yet, ticket-symbol empty.
  state.setSelectedSymbol(null);
  document.getElementById("ticket-symbol").value = "";

  const unsubscribe = state.subscribe(ui.renderForSlice);
  try {
    state.setWatchlist(["PETR4", "VALE3"]);
  } finally {
    unsubscribe();
  }

  assert.equal(state.getState().selectedSymbol, "PETR4");
  assert.equal(document.getElementById("ticket-symbol").value, "PETR4");
});

test("watchlist auto-pick-first symbol clears the Trading Readiness 'select a symbol' state", () => {
  state.setSelectedSymbol(null);
  document.getElementById("ticket-symbol").value = "";
  ui.renderTradingReadiness();
  const phaseEl = document.getElementById("trading-readiness-phase");
  assert.equal(phaseEl.dataset.tone, "neutral", "no symbol selected yet -> neutral 'Select symbol' state");

  const unsubscribe = state.subscribe(ui.renderForSlice);
  try {
    state.setWatchlist(["PETR4", "VALE3"]);
  } finally {
    unsubscribe();
  }

  // "neutral" tone + "Select symbol" copy is exactly the stuck state the
  // bug left behind; once #ticket-symbol carries the auto-picked symbol,
  // phaseSignal() must stop reporting the no-symbol-selected variant.
  assert.notEqual(phaseEl.dataset.tone, "neutral");
});

test("auto-fill never clobbers a symbol the trader is actively editing", () => {
  state.setSelectedSymbol(null);
  const ticketEl = document.getElementById("ticket-symbol");
  ticketEl.value = "ITUB4"; // trader is mid-typing an unrelated symbol

  const unsubscribe = state.subscribe(ui.renderForSlice);
  try {
    state.setWatchlist(["PETR4"]);
  } finally {
    unsubscribe();
  }

  assert.equal(state.getState().selectedSymbol, "PETR4");
  assert.equal(ticketEl.value, "ITUB4", "manual edit must not be overwritten");
});
