// Q1.4 (#256) — risk-policy lifecycle tests.
//
// Three behaviours under test:
//   1. state.clearAll() resets state.riskPolicy to null (a new session
//      must start on the documented 30d FE fallback rather than
//      inheriting the previous backend's cap).
//   2. applyRiskPolicyFetch() resets state.riskPolicy to null when the
//      fetch rejects OR the payload is malformed (and warns once).
//   3. setRiskPolicy() notifies the "riskPolicy" slice, which causes
//      renderForSlice → refreshTicketValidation to flip the submit-
//      disabled state without the trader having to nudge an input.
//
// The state ↔ ui wiring under test mirrors what bindUi() installs in
// production (state.subscribe(renderForSlice)).

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

const state = await import("../js/state.js");
const ui    = await import("../js/ui.js");
const { applyRiskPolicyFetch, _resetRiskPolicyWarnedForTests } =
  await import("../js/riskPolicy.js");

// ── (1) clearAll resets riskPolicy ────────────────────────────────

test("state.clearAll() resets state.riskPolicy to null", () => {
  state.setRiskPolicy({ maxGtdHorizonDays: 60 });
  assert.deepEqual(state.getState().riskPolicy, { maxGtdHorizonDays: 60 });

  state.clearAll();

  assert.equal(state.getState().riskPolicy, null,
    "clearAll must drop the prior backend's cap so the next session starts on FE fallback");
});

// ── (2) applyRiskPolicyFetch error / malformed branches ───────────

test("applyRiskPolicyFetch on rejected fetch sets state.riskPolicy = null", async () => {
  state.setRiskPolicy({ maxGtdHorizonDays: 60 });
  _resetRiskPolicyWarnedForTests();
  const warned = [];
  await applyRiskPolicyFetch({
    fetchPolicy: async () => { throw new Error("boom"); },
    setRiskPolicy: state.setRiskPolicy,
    warn: (...args) => warned.push(args),
  });
  assert.equal(state.getState().riskPolicy, null,
    "rejected fetch must clear the cached policy, not leave the previous value");
  assert.equal(warned.length, 1, "warn fires exactly once on error");
});

test("applyRiskPolicyFetch on malformed payload sets state.riskPolicy = null", async () => {
  state.setRiskPolicy({ maxGtdHorizonDays: 60 });
  _resetRiskPolicyWarnedForTests();
  const warned = [];
  await applyRiskPolicyFetch({
    fetchPolicy: async () => ({ maxGtdHorizonDays: "not-a-number" }),
    setRiskPolicy: state.setRiskPolicy,
    warn: (...args) => warned.push(args),
  });
  assert.equal(state.getState().riskPolicy, null,
    "malformed payload must clear the cached policy");
  assert.equal(warned.length, 1, "warn fires exactly once on malformed payload");
});

test("applyRiskPolicyFetch on malformed payload (missing field) sets state.riskPolicy = null", async () => {
  state.setRiskPolicy({ maxGtdHorizonDays: 60 });
  _resetRiskPolicyWarnedForTests();
  await applyRiskPolicyFetch({
    fetchPolicy: async () => ({}),
    setRiskPolicy: state.setRiskPolicy,
    warn: () => {},
  });
  assert.equal(state.getState().riskPolicy, null);
});

test("applyRiskPolicyFetch clears prior policy up-front (before fetch resolves)", async () => {
  state.setRiskPolicy({ maxGtdHorizonDays: 60 });
  _resetRiskPolicyWarnedForTests();
  let observedDuringFlight = "unset";
  await applyRiskPolicyFetch({
    fetchPolicy: async () => {
      // Snapshot what the validator would see while the fetch is
      // in-flight on a fresh session — must already be the FE fallback,
      // not the prior backend's cap.
      observedDuringFlight = state.getState().riskPolicy;
      return { maxGtdHorizonDays: 90 };
    },
    setRiskPolicy: state.setRiskPolicy,
    warn: () => {},
  });
  assert.equal(observedDuringFlight, null,
    "in-flight readers must see null (→ 30d FE fallback), not the stale value");
  assert.deepEqual(state.getState().riskPolicy, { maxGtdHorizonDays: 90 });
});

// ── (3) setRiskPolicy → ticket revalidation ───────────────────────

// Helper: arrange the ticket DOM into a known GTD/Limit configuration
// and wire state → renderForSlice the way bindUi() does in production.
function arrangeGtdTicket({ daysAhead }) {
  const get = (id) => document.getElementById(id);
  const sym  = get("ticket-symbol");
  const side = get("ticket-side");
  const type = get("ticket-type");
  const qty  = get("ticket-qty");
  const price = get("ticket-price");
  const tif  = get("ticket-tif");
  const gtd  = get("ticket-good-till-date");
  const gtdLabel = get("ticket-good-till-date-label");
  const priceLabel = get("ticket-price-label");
  const stopLabel = get("ticket-stop-price-label");
  const submit = get("ticket-submit");
  const errEl = get("ticket-validation");

  sym.value  = "PETR4";
  side.value = "Buy";
  type.value = "Limit";
  qty.value  = "100";
  price.value = "32.50";
  tif.value  = "GTD";
  // Limit shows price, hides stop. GTD shows the date input.
  priceLabel.hidden = false;
  stopLabel.hidden  = true;
  gtdLabel.hidden   = false;

  const ms = Date.now() + daysAhead * 24 * 60 * 60 * 1000;
  // <input type="date"> serialises as YYYY-MM-DD; the validator only
  // needs Date.parse to succeed, so the ISO date prefix is enough.
  gtd.value = new Date(ms).toISOString().slice(0, 10);

  // Reset submit dataset flags so prior tests don't bleed in.
  delete submit.dataset.validationFailed;
  delete submit.dataset.haltDisabled;
  delete submit.dataset.submitInflight;
  submit.disabled = false;
  errEl.hidden = true;
  errEl.textContent = "";

  return { submit, errEl };
}

test("setRiskPolicy({60}) re-enables submit for a 45d GTD that the 30d fallback rejected", () => {
  // Arrange: ticket with GTD = today+45d, no policy loaded yet → FE
  // falls back to a 30d cap → validation fails → submit disabled.
  state.setRiskPolicy(null);
  const { submit, errEl } = arrangeGtdTicket({ daysAhead: 45 });

  // Subscribe renderForSlice the way bindUi does in production. We
  // unsubscribe at the end so other tests aren't affected.
  const unsub = state.subscribe((slice) => ui.renderForSlice(slice));
  try {
    // Force an initial validation pass so the submit-disabled state
    // reflects the current (null policy → 30d) cap.
    ui.refreshTicketValidation();
    assert.equal(submit.disabled, true,
      "precondition: 45d GTD against the 30d FE fallback must disable submit");
    assert.equal(submit.dataset.validationFailed, "1");
    assert.equal(errEl.hidden, false);

    // Act: a late-arriving policy bump to 60d should trigger
    // revalidation via the riskPolicy slice — without the trader
    // re-entering anything.
    state.setRiskPolicy({ maxGtdHorizonDays: 60 });

    // Assert: submit re-enabled, error cleared, dataset flag cleared.
    assert.equal(submit.disabled, false,
      "riskPolicy slice update must re-run validation and re-enable submit");
    assert.equal(submit.dataset.validationFailed, undefined);
    assert.equal(errEl.hidden, true);
  } finally {
    unsub();
  }
});

test("setRiskPolicy({30}) disables submit for a 45d GTD that {60} previously allowed", () => {
  // Arrange: policy already at 60d, 45d GTD → valid → submit enabled.
  state.setRiskPolicy({ maxGtdHorizonDays: 60 });
  const { submit, errEl } = arrangeGtdTicket({ daysAhead: 45 });

  const unsub = state.subscribe((slice) => ui.renderForSlice(slice));
  try {
    ui.refreshTicketValidation();
    assert.equal(submit.disabled, false, "precondition: 45d under 60d cap is valid");
    assert.equal(errEl.hidden, true);

    // Act: cap drops to 30d.
    state.setRiskPolicy({ maxGtdHorizonDays: 30 });

    // Assert: validation re-runs, submit blocked, error surfaced —
    // without the trader nudging the GTD input.
    assert.equal(submit.disabled, true,
      "riskPolicy tightening must re-disable submit without manual re-input");
    assert.equal(submit.dataset.validationFailed, "1");
    assert.equal(errEl.hidden, false);
  } finally {
    unsub();
    state.setRiskPolicy(null);
  }
});
