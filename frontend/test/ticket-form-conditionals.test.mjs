// Q1.4 (#256) — pure-function tests for the ticket conditional
// visibility rules. The bindUi() wiring just calls
// applyTicketConditionalVisibility on every type/TIF change; we test
// the helper directly so we don't have to boot the entire UI.

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({ ids: {} });

const { applyTicketConditionalVisibility } = await import("../js/ui.js");

function makeEls() {
  const make = (over = {}) => ({
    hidden: false, disabled: false, required: false, value: "", ...over,
  });
  return {
    priceEl:        make(),
    priceLabel:     make({ hidden: false }),
    stopPriceEl:    make(),
    stopPriceLabel: make({ hidden: true }),
    gtdEl:          make(),
    gtdLabel:       make({ hidden: true }),
  };
}

test("Limit shows Price, hides StopPrice + GTD", () => {
  const els = makeEls();
  applyTicketConditionalVisibility({ type: "Limit", tif: "Day", ...els });
  assert.equal(els.priceLabel.hidden, false);
  assert.equal(els.priceEl.required, true);
  assert.equal(els.stopPriceLabel.hidden, true);
  assert.equal(els.gtdLabel.hidden, true);
});

test("Market hides Price (no limit price for Market)", () => {
  const els = makeEls();
  els.priceEl.value = "32.50";
  applyTicketConditionalVisibility({ type: "Market", tif: "Day", ...els });
  assert.equal(els.priceLabel.hidden, true);
  assert.equal(els.priceEl.required, false);
  assert.equal(els.priceEl.value, "", "stale price cleared on hide");
});

test("StopLoss hides Price, shows StopPrice", () => {
  const els = makeEls();
  applyTicketConditionalVisibility({ type: "StopLoss", tif: "Day", ...els });
  assert.equal(els.priceLabel.hidden, true);
  assert.equal(els.stopPriceLabel.hidden, false);
  assert.equal(els.stopPriceEl.required, true);
});

test("StopLimit shows both Price and StopPrice", () => {
  const els = makeEls();
  applyTicketConditionalVisibility({ type: "StopLimit", tif: "Day", ...els });
  assert.equal(els.priceLabel.hidden, false);
  assert.equal(els.stopPriceLabel.hidden, false);
});

test("MarketWithLeftover shows Price (carries leftover limit)", () => {
  const els = makeEls();
  applyTicketConditionalVisibility({ type: "MarketWithLeftover", tif: "Day", ...els });
  assert.equal(els.priceLabel.hidden, false);
  assert.equal(els.stopPriceLabel.hidden, true);
});

test("TIF=GTD shows GoodTillDate input", () => {
  const els = makeEls();
  applyTicketConditionalVisibility({ type: "Limit", tif: "GTD", ...els });
  assert.equal(els.gtdLabel.hidden, false);
  assert.equal(els.gtdEl.required, true);
});

test("TIF away from GTD hides + clears GoodTillDate", () => {
  const els = makeEls();
  els.gtdEl.value = "2099-01-01T00:00";
  els.gtdLabel.hidden = false;
  applyTicketConditionalVisibility({ type: "Limit", tif: "Day", ...els });
  assert.equal(els.gtdLabel.hidden, true);
  assert.equal(els.gtdEl.value, "", "stale GTD cleared on hide");
});

test("Hidden conditional inputs become disabled (form-skip + validation-skip)", () => {
  const els = makeEls();
  applyTicketConditionalVisibility({ type: "Limit", tif: "Day", ...els });
  assert.equal(els.stopPriceEl.disabled, true);
  assert.equal(els.gtdEl.disabled, true);
});
