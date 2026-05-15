// Q1.6 (#258) — phase-badge HTML rendering. Locks the per-phase label,
// CSS class, aria-label and the "no badge" rule for unknown phases so
// a refactor of PHASE_LABELS or the wire-side enum can't silently
// change what the trader sees on the watchlist.

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({ ids: {} });

const ui    = await import("../js/ui.js");
const state = await import("../js/state.js");

test("phase badge renders nothing for Unknown / no snapshot", () => {
  const html = ui.phaseBadgeHtml("UNK4");
  assert.equal(html, "");
});

const cases = [
  ["Reserved",         "RESERVED"],
  ["OpeningCall",      "PRE-OPEN"],
  ["Open",             "OPEN"],
  ["FinalClosingCall", "CLOSING"],
  ["Close",            "CLOSED"],
];

for (const [phase, label] of cases) {
  test(`phase badge renders ${label} for ${phase}`, () => {
    state.applyPhaseFrame({ symbol: "PETR4", phase, at: "2026-01-01T00:00:00Z" });
    const html = ui.phaseBadgeHtml("PETR4");
    assert.match(html, new RegExp(`class="phase-badge ${label}"`),
      `expected class fragment for ${phase}`);
    assert.match(html, new RegExp(`>${label}</span>$`),
      `expected label text ${label}`);
    assert.match(html, /aria-label="PETR4 phase: /,
      "aria-label must mention symbol and phase");
    assert.match(html, new RegExp(`aria-label="PETR4 phase: ${label}"`),
      "aria-label must include the trader-facing phase label");
    assert.match(html, /data-symbol="PETR4"/, "data-symbol attr present");
  });
}

test("getPhase returns Unknown when no snapshot is loaded", () => {
  // Use a unique symbol so previous tests don't pollute.
  assert.equal(state.getPhase("NEVERSEEN"), "Unknown");
});

test("isAuctionPhase is true only for OpeningCall and FinalClosingCall", () => {
  assert.equal(state.isAuctionPhase("OpeningCall"),      true);
  assert.equal(state.isAuctionPhase("FinalClosingCall"), true);
  assert.equal(state.isAuctionPhase("Open"),             false);
  assert.equal(state.isAuctionPhase("Reserved"),         false);
  assert.equal(state.isAuctionPhase("Close"),            false);
  assert.equal(state.isAuctionPhase("Unknown"),          false);
});
