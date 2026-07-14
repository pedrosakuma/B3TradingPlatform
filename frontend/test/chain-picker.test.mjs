import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({
  ids: {
    "chain-picker-modal": { tag: "dialog" },
    "chain-picker-grid": { tag: "div" },
    "ticket-symbol": { tag: "input" },
    "ticket-side": { tag: "select" },
    "ticket-price": { tag: "input" },
    "ticket-stop-price": { tag: "input" },
    "ticket-feedback": { tag: "p", hidden: true },
  },
});

const ui = await import("../js/ui.js");

const modal = document.getElementById("chain-picker-modal");
modal.open = false;
modal.showModal = () => { modal.open = true; };
modal.close = () => { modal.open = false; };

test("buildChainGrid groups options by expiry and carries selection metadata", () => {
  const html = ui.buildChainGrid([
    { symbol: "PETRC35", securityId: "101", strikePrice: 35, expirationDate: "2026-08-21", putOrCall: "Call" },
    { symbol: "PETRP35", securityId: "102", strikePrice: 35, expirationDate: "2026-08-21", putOrCall: "Put" },
    { symbol: "PETRC36", securityId: "103", strikePrice: 36, expirationDate: "2026-09-18", putOrCall: "Call" },
  ]);

  assert.match(html, /2026-08-21/);
  assert.match(html, /data-symbol="PETRC35"/);
  assert.match(html, /data-put-or-call="Call"/);
  assert.match(html, /data-symbol="PETRP35"/);
  assert.match(html, /data-put-or-call="Put"/);
  assert.match(html, /chain-cell-empty/u);
});

test("setChainPickerStatus renders escaped error feedback", () => {
  ui.setChainPickerStatus("Unknown <underlying>", "error");
  const html = document.getElementById("chain-picker-grid").innerHTML;
  assert.match(html, /chain-placeholder-error/);
  assert.doesNotMatch(html, /<underlying>/);
  assert.match(html, /Unknown &lt;underlying&gt;/);
});

test("handleChainCellClick forwards the selected instrument and closes the modal", () => {
  let selected = null;
  ui.openChainPicker((selection) => { selected = selection; });
  assert.equal(modal.open, true);

  ui.handleChainCellClick({
    target: {
      closest(selector) {
        if (selector !== ".chain-cell") return null;
        return {
          dataset: {
            symbol: "PETRC35",
            securityId: "101",
            putOrCall: "Call",
          },
        };
      },
    },
  });

  assert.deepEqual(selected, {
    symbol: "PETRC35",
    securityId: "101",
    putOrCall: "Call",
  });
  assert.equal(modal.open, false);
});

test("populateTicketFromChainSelection resets side/price defaults for the new option", () => {
  const symEl = document.getElementById("ticket-symbol");
  const sideEl = document.getElementById("ticket-side");
  const priceEl = document.getElementById("ticket-price");
  const stopEl = document.getElementById("ticket-stop-price");
  const feedbackEl = document.getElementById("ticket-feedback");

  let symbolChanges = 0;
  let sideChanges = 0;
  let priceInputs = 0;
  symEl.addEventListener("change", () => { symbolChanges += 1; });
  sideEl.addEventListener("change", () => { sideChanges += 1; });
  priceEl.addEventListener("input", () => { priceInputs += 1; });

  symEl.value = "VALE3";
  sideEl.value = "Sell";
  priceEl.value = "12.34";
  stopEl.value = "12.10";
  feedbackEl.hidden = false;
  feedbackEl.textContent = "stale warning";

  ui.populateTicketFromChainSelection({ symbol: "petrc35", securityId: "101", putOrCall: "Call" });

  assert.equal(symEl.value, "PETRC35");
  assert.equal(sideEl.value, "Buy");
  assert.equal(priceEl.value, "");
  assert.equal(stopEl.value, "");
  assert.equal(feedbackEl.hidden, true);
  assert.equal(feedbackEl.textContent, "");
  assert.equal(symbolChanges, 1);
  assert.equal(sideChanges, 1);
  assert.equal(priceInputs, 1);
});
