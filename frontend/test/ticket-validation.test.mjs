// Q1.4 (#256) — pure-function tests for validateTicketState.
//
// validateTicketState is the client-side mirror of the Q1.1 risk
// pipeline subset that produces visibly-known errors before the
// trader round-trips to the backend. Server stays authoritative.

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({
  ids: {
    "ticket-symbol":     { tag: "input"  },
    "ticket-side":       { tag: "select" },
    "ticket-type":       { tag: "select" },
    "ticket-qty":        { tag: "input"  },
    "ticket-price":      { tag: "input"  },
    "ticket-stop-price": { tag: "input"  },
    "ticket-good-till-date": { tag: "input" },
    "ticket-tif":        { tag: "select" },
    "ticket-submit":     { tag: "button" },
    "ticket-tif-hint":   { tag: "p", hidden: true },
    "ticket-validation": { tag: "p", hidden: true },
    "ticket-rules-hint": { tag: "p" },
    "ticket-feedback":   { tag: "p", hidden: true },
    "ticket-inflight":   { tag: "p", hidden: true },
    "ticket-price-label":      { tag: "label" },
    "ticket-stop-price-label": { tag: "label", hidden: true },
    "ticket-good-till-date-label": { tag: "label", hidden: true },
  },
});

const { validateTicketState } = await import("../js/ui.js");

const NOW = Date.UTC(2026, 0, 1, 12, 0, 0); // fixed clock
const ONE_HOUR = 60 * 60 * 1000;
const ONE_DAY  = 24 * ONE_HOUR;

function f(over = {}) {
  return {
    type: "Limit",
    side: "Buy",
    tif:  "Day",
    price: "32.50",
    stopPrice: "",
    goodTillDate: "",
    priceHidden: false,
    stopPriceHidden: true,
    gtdHidden: true,
    now: NOW,
    ...over,
  };
}

// ── Happy paths ──

test("Limit/Day with a valid price → valid", () => {
  const r = validateTicketState(f());
  assert.equal(r.valid, true);
  assert.deepEqual(r.errors, {});
});

test("Market/IOC (no price, no stop, no GTD) → valid", () => {
  const r = validateTicketState(f({ type: "Market", tif: "IOC", price: "", priceHidden: true }));
  assert.equal(r.valid, true);
});

test("StopLoss with stopPrice > 0 → valid", () => {
  const r = validateTicketState(f({
    type: "StopLoss", price: "", priceHidden: true,
    stopPrice: "33.00", stopPriceHidden: false,
  }));
  assert.equal(r.valid, true);
});

test("StopLimit Buy with price >= stopPrice → valid", () => {
  const r = validateTicketState(f({
    type: "StopLimit", side: "Buy",
    price: "33.10", stopPrice: "33.00", stopPriceHidden: false,
  }));
  assert.equal(r.valid, true);
});

test("GTD with goodTillDate inside 30d window → valid", () => {
  const future = new Date(NOW + 5 * ONE_DAY).toISOString();
  const r = validateTicketState(f({ tif: "GTD", goodTillDate: future, gtdHidden: false }));
  assert.equal(r.valid, true);
});

// ── Reject paths ──

test("StopLoss without stopPrice → error on stopPrice", () => {
  const r = validateTicketState(f({
    type: "StopLoss", price: "", priceHidden: true,
    stopPrice: "", stopPriceHidden: false,
  }));
  assert.equal(r.valid, false);
  assert.match(r.errors.stopPrice, /required/);
});

test("StopLimit without limit price → error on price", () => {
  const r = validateTicketState(f({
    type: "StopLimit", side: "Buy",
    price: "", stopPrice: "33.00", stopPriceHidden: false,
  }));
  assert.equal(r.valid, false);
  assert.match(r.errors.price, /limit price required/);
});

test("StopLimit Buy with price < stopPrice → error on price", () => {
  const r = validateTicketState(f({
    type: "StopLimit", side: "Buy",
    price: "32.50", stopPrice: "33.00", stopPriceHidden: false,
  }));
  assert.equal(r.valid, false);
  assert.match(r.errors.price, /Buy StopLimit/);
});

test("StopLimit Sell with price > stopPrice → error on price", () => {
  const r = validateTicketState(f({
    type: "StopLimit", side: "Sell",
    price: "33.50", stopPrice: "33.00", stopPriceHidden: false,
  }));
  assert.equal(r.valid, false);
  assert.match(r.errors.price, /Sell StopLimit/);
});

test("GTD without goodTillDate → error on goodTillDate", () => {
  const r = validateTicketState(f({ tif: "GTD", goodTillDate: "", gtdHidden: false }));
  assert.equal(r.valid, false);
  assert.match(r.errors.goodTillDate, /required/);
});

test("GTD with goodTillDate in the past → error", () => {
  const past = new Date(NOW - ONE_HOUR).toISOString();
  const r = validateTicketState(f({ tif: "GTD", goodTillDate: past, gtdHidden: false }));
  assert.equal(r.valid, false);
  assert.match(r.errors.goodTillDate, /future/);
});

test("GTD beyond 30 days → error", () => {
  const tooFar = new Date(NOW + 31 * ONE_DAY).toISOString();
  const r = validateTicketState(f({ tif: "GTD", goodTillDate: tooFar, gtdHidden: false }));
  assert.equal(r.valid, false);
  assert.match(r.errors.goodTillDate, /30 days/);
});

// Q1.4 (#256). The GTD horizon mirrors the backend
// `Trading:Risk:MaxGtdHorizon` exposed via /policy/risk. When the FE
// stashes a different (e.g. 7-day) policy, the validator must honor it.
test("GTD honors maxGtdHorizonDays from risk policy (7d cap)", () => {
  const okAt5 = new Date(NOW + 5 * ONE_DAY).toISOString();
  const tooFarAt8 = new Date(NOW + 8 * ONE_DAY).toISOString();
  const ok = validateTicketState(f({
    tif: "GTD", goodTillDate: okAt5, gtdHidden: false, maxGtdHorizonDays: 7,
  }));
  assert.equal(ok.valid, true);
  const bad = validateTicketState(f({
    tif: "GTD", goodTillDate: tooFarAt8, gtdHidden: false, maxGtdHorizonDays: 7,
  }));
  assert.equal(bad.valid, false);
  assert.match(bad.errors.goodTillDate, /7 days/);
});

test("GTD falls back to 30-day cap when policy missing/malformed", () => {
  const tooFar = new Date(NOW + 31 * ONE_DAY).toISOString();
  for (const bad of [undefined, null, 0, -1, NaN, "x"]) {
    const r = validateTicketState(f({
      tif: "GTD", goodTillDate: tooFar, gtdHidden: false, maxGtdHorizonDays: bad,
    }));
    assert.equal(r.valid, false, `expected fallback to reject for policy=${bad}`);
    assert.match(r.errors.goodTillDate, /30 days/);
  }
});

test("MarketWithLeftover + IOC → incompatible TIF error", () => {
  const r = validateTicketState(f({
    type: "MarketWithLeftover", price: "32.5", tif: "IOC",
  }));
  assert.equal(r.valid, false);
  assert.match(r.errors.tif, /incompatible/);
});

test("MarketWithLeftover + FOK → incompatible TIF error", () => {
  const r = validateTicketState(f({
    type: "MarketWithLeftover", price: "32.5", tif: "FOK",
  }));
  assert.equal(r.valid, false);
});

test("Hidden conditional fields suppress their own errors", () => {
  // StopLoss-style state but stopPriceHidden=true (e.g. mid-toggle).
  // Errors for hidden fields should not surface.
  const r = validateTicketState(f({
    type: "StopLoss", price: "", priceHidden: true,
    stopPrice: "", stopPriceHidden: true,
  }));
  assert.equal(r.valid, true);
});
