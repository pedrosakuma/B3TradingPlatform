// Q1.6 (#258) — auction reducer + render tests.
//
// Covers the state-side reducer (snapshot apply, top trend tracking,
// print-history append + cap, panel open/close) and the DOM render
// path that turns the cached state into the panel UI.

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({
  ids: {
    "auction-panel":       { tag: "section", hidden: true },
    "auction-toggle":      { tag: "button" },
    "auction-body":        { tag: "div", hidden: true },
    "auction-symbol-tag":  { tag: "span" },
    "auction-top":         { tag: "span" },
    "auction-top-arrow":   { tag: "span" },
    "auction-top-price":   { tag: "span" },
    "auction-match-qty":   { tag: "span" },
    "auction-imbalance":   { tag: "span" },
    "auction-ttu":         { tag: "span" },
    "auction-prints":      { tag: "ol" },
  },
});

const state = await import("../js/state.js");
const ui    = await import("../js/ui.js");

const SYM = "AUCT1";

function clearAuction() {
  // Reset by zeroing the per-symbol entry — clearAll touches too much.
  state.getState().auctionBySymbol.delete(SYM);
  state.setAuctionPanelSymbol(null);
}

test("setAuctionPanelSymbol toggles the open state", () => {
  clearAuction();
  assert.equal(state.getState().auctionPanelSymbol, null);
  state.setAuctionPanelSymbol(SYM);
  assert.equal(state.getState().auctionPanelSymbol, SYM);
  state.setAuctionPanelSymbol(null);
  assert.equal(state.getState().auctionPanelSymbol, null);
});

test("applyAuctionFrame top frame populates the cache", () => {
  clearAuction();
  state.applyAuctionFrame({
    symbol: SYM, top: 32.50, indicativeMatchQty: 1000,
    imbalance: 200, imbalanceSide: "Buy", at: "2026-01-01T13:00:00Z",
  });
  const aux = state.getAuctionState(SYM);
  assert.equal(aux.top, 32.50);
  assert.equal(aux.indicativeMatchQty, 1000);
  assert.equal(aux.imbalance, 200);
  assert.equal(aux.imbalanceSide, "Buy");
  assert.equal(aux.prevTop, null);
  assert.deepEqual(aux.lastPrints, []);
});

test("applyAuctionFrame tracks prevTop across top changes", () => {
  clearAuction();
  state.applyAuctionFrame({ symbol: SYM, top: 32.50, imbalanceSide: "Buy" });
  state.applyAuctionFrame({ symbol: SYM, top: 32.60, imbalanceSide: "Buy" });
  let aux = state.getAuctionState(SYM);
  assert.equal(aux.top,     32.60);
  assert.equal(aux.prevTop, 32.50);
  state.applyAuctionFrame({ symbol: SYM, top: 32.55, imbalanceSide: "Buy" });
  aux = state.getAuctionState(SYM);
  assert.equal(aux.top,     32.55);
  assert.equal(aux.prevTop, 32.60);
});

test("applyAuctionFrame ignores prevTop when current top is null (cold snapshot)", () => {
  clearAuction();
  // Cold snapshot — server sends nulls when nothing observed yet.
  state.applyAuctionFrame({ symbol: SYM, top: null });
  state.applyAuctionFrame({ symbol: SYM, top: 30.00 });
  // First non-null top should NOT have a prevTop (no prior real value).
  assert.equal(state.getAuctionState(SYM).prevTop, null);
});

test("applyAuctionFrame appends prints newest-first and caps at 5", () => {
  clearAuction();
  for (let i = 0; i < 8; i++) {
    state.applyAuctionFrame({
      symbol: SYM, kind: "Opening",
      price: 30 + i, qty: 100 + i, at: `2026-01-01T13:00:0${i}Z`,
    });
  }
  const prints = state.getAuctionState(SYM).lastPrints;
  assert.equal(prints.length, 5);
  // Newest first → last-pushed (i=7) is at index 0.
  assert.equal(prints[0].price, 37);
  assert.equal(prints[4].price, 33);
});

test("renderAuctionPanel hides the panel when closed", () => {
  clearAuction();
  ui.renderAuctionPanel();
  const panel = document.getElementById("auction-panel");
  assert.equal(panel.hidden, true);
  assert.ok(panel.classList.contains("collapsed"));
});

test("renderAuctionPanel shows TOP, match qty, imbalance and arrow direction", () => {
  clearAuction();
  state.applyAuctionFrame({ symbol: SYM, top: 30.00, imbalanceSide: "Buy" });
  state.applyAuctionFrame({
    symbol: SYM, top: 30.50, indicativeMatchQty: 5000,
    imbalance: 800, imbalanceSide: "Buy",
  });
  state.setAuctionPanelSymbol(SYM);
  ui.renderAuctionPanel();

  const panel = document.getElementById("auction-panel");
  assert.equal(panel.hidden, false);
  assert.equal(document.getElementById("auction-symbol-tag").textContent, SYM);
  // Top price formatted via fmtPx (pt-BR comma decimal).
  assert.equal(document.getElementById("auction-top-price").textContent, "30,50");
  // Up arrow because 30.50 > 30.00.
  const arrow = document.getElementById("auction-top-arrow");
  assert.equal(arrow.textContent, "▲");
  assert.match(arrow.className, /\bup\b/);
  // Imbalance side colors the cell green for Buy.
  const imb = document.getElementById("auction-imbalance");
  assert.match(imb.textContent, /Buy/);
  assert.match(imb.className,  /imb-buy/);
  // TTU placeholder remains an em-dash until upstream emits it.
  assert.equal(document.getElementById("auction-ttu").textContent, "—");
});

test("renderAuctionPanel renders Sell-side imbalance with the sell color class", () => {
  clearAuction();
  state.applyAuctionFrame({
    symbol: SYM, top: 30.00, imbalance: 1500, imbalanceSide: "Sell",
  });
  state.setAuctionPanelSymbol(SYM);
  ui.renderAuctionPanel();
  const imb = document.getElementById("auction-imbalance");
  assert.match(imb.className, /imb-sell/);
  assert.match(imb.textContent, /Sell/);
});

test("renderAuctionPanel renders the print history newest-first", () => {
  clearAuction();
  state.applyAuctionFrame({ symbol: SYM, kind: "Opening", price: 30, qty: 100, at: "2026-01-01T13:00:00Z" });
  state.applyAuctionFrame({ symbol: SYM, kind: "Opening", price: 31, qty: 200, at: "2026-01-01T13:00:01Z" });
  state.setAuctionPanelSymbol(SYM);
  ui.renderAuctionPanel();
  const html = document.getElementById("auction-prints").innerHTML;
  // Most recent (price=31) should appear before the older one in the
  // rendered list.
  assert.ok(html.indexOf("31,00") < html.indexOf("30,00"),
    "newest print must appear first in the list");
});
