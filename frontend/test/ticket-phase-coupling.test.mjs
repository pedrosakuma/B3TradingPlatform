// Q1.6 (#258) — order-ticket coupling to auction phase.
//
// What we lock down:
//   * Phase ∈ {OpeningCall, FinalClosingCall} → TIF default flips to
//     GoodForAuction unless the trader manually picked something.
//   * Manual user pick is preserved across phase changes (no trample).
//   * TIF=Day in an auction phase shows the soft "pending until open"
//     warning text.
//   * Phase=Reserved disables the Submit button and exposes
//     aria-disabled + the "Instrumento halted" tooltip.
//   * Leaving the auction phase reverts the auto-pick back to Day.

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({
  ids: {
    "ticket-symbol":   { tag: "input"  },
    "ticket-tif":      { tag: "select" },
    "ticket-submit":   { tag: "button" },
    "ticket-tif-hint": { tag: "p", hidden: true },
  },
});

const state = await import("../js/state.js");
const ui    = await import("../js/ui.js");

const SYM = "PETR4";

function setupTicket() {
  const symEl = document.getElementById("ticket-symbol");
  const tifEl = document.getElementById("ticket-tif");
  const submitEl = document.getElementById("ticket-submit");
  const hintEl = document.getElementById("ticket-tif-hint");
  symEl.value = SYM;
  tifEl.value = "Day";
  // Reset coupling-affecting dataset between tests.
  delete tifEl.dataset.userPicked;
  delete tifEl.dataset.autoPicked;
  delete submitEl.dataset.haltDisabled;
  submitEl.disabled = false;
  submitEl.removeAttribute("aria-disabled");
  submitEl.removeAttribute("title");
  hintEl.hidden = true;
  hintEl.textContent = "";
  hintEl.className = "field-hint";
  return { symEl, tifEl, submitEl, hintEl };
}

test("OpeningCall flips TIF default to GoodForAuction", () => {
  const { tifEl, hintEl } = setupTicket();
  state.applyPhaseFrame({ symbol: SYM, phase: "OpeningCall" });
  ui.renderTicketPhaseCoupling();
  assert.equal(tifEl.value, "GoodForAuction");
  assert.equal(tifEl.dataset.autoPicked, "1");
  assert.equal(hintEl.hidden, false);
  assert.match(hintEl.textContent, /GoodForAuction recommended/);
  assert.match(hintEl.className, /hint-info/);
});

test("FinalClosingCall flips TIF default to GoodForAuction", () => {
  const { tifEl } = setupTicket();
  state.applyPhaseFrame({ symbol: SYM, phase: "FinalClosingCall" });
  ui.renderTicketPhaseCoupling();
  assert.equal(tifEl.value, "GoodForAuction");
});

test("manual user pick is preserved through phase changes", () => {
  const { tifEl } = setupTicket();
  // Trader picks Day explicitly (mark via dataset like the change handler does).
  tifEl.dataset.userPicked = "1";
  state.applyPhaseFrame({ symbol: SYM, phase: "OpeningCall" });
  ui.renderTicketPhaseCoupling();
  assert.equal(tifEl.value, "Day", "user pick must not be overwritten");
});

test("Day in auction phase surfaces the soft pending warning", () => {
  const { tifEl, hintEl } = setupTicket();
  // Trader explicitly picks Day, so the renderer keeps it but warns.
  tifEl.dataset.userPicked = "1";
  state.applyPhaseFrame({ symbol: SYM, phase: "OpeningCall" });
  ui.renderTicketPhaseCoupling();
  assert.equal(tifEl.value, "Day");
  assert.equal(hintEl.hidden, false);
  assert.match(hintEl.textContent, /pending until the open/);
  assert.match(hintEl.className, /hint-warn/);
});

test("Reserved phase disables Submit with aria-disabled + tooltip", () => {
  const { submitEl } = setupTicket();
  state.applyPhaseFrame({ symbol: SYM, phase: "Reserved" });
  ui.renderTicketPhaseCoupling();
  assert.equal(submitEl.disabled, true);
  assert.equal(submitEl.getAttribute("aria-disabled"), "true");
  assert.equal(submitEl.getAttribute("title"), "Instrumento halted");
  assert.equal(submitEl.dataset.haltDisabled, "1");
});

test("leaving Reserved re-enables Submit and clears the tooltip", () => {
  const { submitEl } = setupTicket();
  state.applyPhaseFrame({ symbol: SYM, phase: "Reserved" });
  ui.renderTicketPhaseCoupling();
  assert.equal(submitEl.disabled, true);
  state.applyPhaseFrame({ symbol: SYM, phase: "Open" });
  ui.renderTicketPhaseCoupling();
  assert.equal(submitEl.disabled, false);
  assert.equal(submitEl.getAttribute("aria-disabled"), null);
  assert.equal(submitEl.getAttribute("title"), null);
});

test("leaving the auction phase reverts the auto-picked TIF back to Day", () => {
  const { tifEl } = setupTicket();
  state.applyPhaseFrame({ symbol: SYM, phase: "OpeningCall" });
  ui.renderTicketPhaseCoupling();
  assert.equal(tifEl.value, "GoodForAuction");
  state.applyPhaseFrame({ symbol: SYM, phase: "Open" });
  ui.renderTicketPhaseCoupling();
  assert.equal(tifEl.value, "Day", "auto-pick must revert when phase leaves auction");
});

test("non-auction, non-Reserved phase shows no hint and leaves submit enabled", () => {
  const { tifEl, hintEl, submitEl } = setupTicket();
  state.applyPhaseFrame({ symbol: SYM, phase: "Open" });
  ui.renderTicketPhaseCoupling();
  assert.equal(tifEl.value, "Day");
  assert.equal(hintEl.hidden, true);
  assert.equal(submitEl.disabled, false);
});

// ── Submit disabled-state OR semantics (Pass-1 review fix) ─────────
//
// The Submit button has two independent disable conditions tracked on
// dataset flags: dataset.submitInflight (set by setTicketSubmitting) and
// dataset.haltDisabled (set by renderTicketPhaseCoupling for Reserved).
// The disabled bit must be the OR of both — neither writer is allowed
// to clear the other's intent.

test("Reserved + setTicketSubmitting(false) keeps Submit disabled (halt wins)", () => {
  const { submitEl } = setupTicket();
  state.applyPhaseFrame({ symbol: SYM, phase: "Reserved" });
  ui.renderTicketPhaseCoupling();
  ui.setTicketSubmitting(true);
  assert.equal(submitEl.disabled, true, "in-flight + halted both disable");
  ui.setTicketSubmitting(false);
  assert.equal(submitEl.disabled, true,
    "clearing in-flight must NOT re-enable while halted");
  assert.equal(submitEl.dataset.haltDisabled, "1");
  assert.equal(submitEl.getAttribute("aria-disabled"), "true");
});

test("In-flight + phase Reserved→Open keeps Submit disabled (in-flight wins)", () => {
  const { submitEl } = setupTicket();
  state.applyPhaseFrame({ symbol: SYM, phase: "Reserved" });
  ui.renderTicketPhaseCoupling();
  ui.setTicketSubmitting(true);
  assert.equal(submitEl.disabled, true);
  // Phase exits Reserved while submit is still in flight.
  state.applyPhaseFrame({ symbol: SYM, phase: "Open" });
  ui.renderTicketPhaseCoupling();
  assert.equal(submitEl.disabled, true,
    "leaving Reserved must NOT re-enable while a submit is in flight");
  assert.equal(submitEl.dataset.submitInflight, "1");
  assert.equal(submitEl.getAttribute("aria-disabled"), "true");
  // Now clear in-flight: both conditions gone → enabled.
  ui.setTicketSubmitting(false);
  assert.equal(submitEl.disabled, false);
  assert.equal(submitEl.getAttribute("aria-disabled"), null);
});

test("Both flags clear → Submit enabled, aria-disabled removed", () => {
  const { submitEl } = setupTicket();
  state.applyPhaseFrame({ symbol: SYM, phase: "Open" });
  ui.renderTicketPhaseCoupling();
  ui.setTicketSubmitting(true);
  ui.setTicketSubmitting(false);
  assert.equal(submitEl.disabled, false);
  assert.equal(submitEl.dataset.submitInflight, undefined);
  assert.equal(submitEl.dataset.haltDisabled,   undefined);
  assert.equal(submitEl.getAttribute("aria-disabled"), null);
});
