import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

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
const validation = await import("../js/validation.js");
const contracts = JSON.parse(await readFile(
  new URL("./fixtures/operations-contracts.json", import.meta.url),
  "utf8",
));

const modal = document.getElementById("chain-picker-modal");
modal.open = false;
modal.showModal = () => { modal.open = true; };
modal.close = () => { modal.open = false; };

test("buildChainGrid groups options by expiry and carries selection metadata", () => {
  const html = ui.buildChainGrid([
    { symbol: "PETRC35", securityId: "101", tickSize: 0.01, lotSize: 1, strikePrice: 35, expirationDate: "2026-08-21", putOrCall: "Call" },
    { symbol: "PETRP35", securityId: "102", tickSize: 0.01, lotSize: 1, strikePrice: 35, expirationDate: "2026-08-21", putOrCall: "Put" },
    { symbol: "PETRC36", securityId: "103", tickSize: 0.01, lotSize: 1, strikePrice: 36, expirationDate: "2026-09-18", putOrCall: "Call" },
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
    lotSize: undefined,
    tickSize: undefined,
    contractMultiplier: undefined,
    securityType: undefined,
  });
  assert.equal(modal.open, false);
});

test("serialized option metadata drives SecurityId, lot, tick and multiplier", () => {
  const instrument = contracts.instrument;
  const html = ui.buildChainGrid([instrument]);
  assert.match(html, /data-security-id="902001"/);
  assert.match(html, /data-lot-size="1"/);
  assert.match(html, /data-tick-size="0.05"/);
  assert.match(html, /data-contract-multiplier="100"/);

  ui.populateTicketFromChainSelection(instrument);
  assert.equal(document.getElementById("ticket-symbol").dataset.securityId, "902001");
  assert.equal(ui.selectedTicketInstrument().securityId, 902001);
  assert.equal(validation.rulesFor(instrument.symbol).lotSize, 1);
  assert.equal(validation.rulesFor(instrument.symbol).tickSize, 0.05);
  assert.equal(validation.rulesFor(instrument.symbol).contractMultiplier, 100);
  assert.equal(validation.validateOrder({
    symbol: instrument.symbol,
    type: "Limit",
    quantity: 1,
    price: 0,
  }), null, "backend-supported zero option price remains valid");
});

test("option cells with missing order metadata are explicitly unavailable", () => {
  const html = ui.buildChainGrid([{
    ...contracts.instrument,
    securityId: 0,
    tickSize: null,
  }]);
  assert.match(html, /chain-cell-unavailable/);
  assert.match(html, /metadata unavailable/);
  assert.doesNotMatch(html, /data-security-id/);
});

test("option-chain contract fields are escaped before DOM insertion", () => {
  const html = ui.buildChainGrid([{
    ...contracts.instrument,
    symbol: `PETR"><img src=x onerror=alert(1)>`,
    expirationDate: `2026-08-21<script>alert(1)</script>`,
  }]);
  assert.doesNotMatch(html, /<img|<script/);
  assert.match(html, /&lt;img/);
  assert.match(html, /&lt;script/);
});

test("selected option and subaccount are serialized into the order payload", () => {
  const result = ui.addTicketRouting(
    { symbol: contracts.instrument.symbol, quantity: 1, price: 1.5 },
    {
      instrument: { securityId: contracts.instrument.securityId, stale: false },
      subAccountId: "BOOK-A",
    },
  );
  assert.deepEqual(result, {
    error: null,
    payload: {
      symbol: contracts.instrument.symbol,
      quantity: 1,
      price: 1.5,
      securityId: 902001,
      subAccountId: "BOOK-A",
    },
  });
});

test("stale option metadata blocks payload creation", () => {
  const result = ui.addTicketRouting(
    { symbol: contracts.instrument.symbol },
    { instrument: { securityId: 902001, stale: true }, subAccountId: "BOOK-A" },
  );
  assert.match(result.error, /metadata is stale/i);
  assert.equal(result.payload, undefined);
});

test("stale subaccount data blocks routed order payloads", () => {
  const result = ui.addTicketRouting(
    { symbol: "PETR4" },
    { subAccountId: "BOOK-A", subAccountAvailable: false },
  );
  assert.match(result.error, /subaccount data is unavailable or stale/i);
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

test("populateTicketFromChainSelection clears stop price before validation-driving events fire", () => {
  const symEl = document.getElementById("ticket-symbol");
  const sideEl = document.getElementById("ticket-side");
  const priceEl = document.getElementById("ticket-price");
  const stopEl = document.getElementById("ticket-stop-price");

  const observedStopValues = [];
  const recordStopValue = () => { observedStopValues.push(stopEl.value); };
  symEl.addEventListener("change", recordStopValue);
  sideEl.addEventListener("change", recordStopValue);
  priceEl.addEventListener("input", recordStopValue);
  priceEl.addEventListener("change", recordStopValue);

  symEl.value = "VALE3";
  sideEl.value = "Sell";
  priceEl.value = "12.34";
  stopEl.value = "99.99";

  ui.populateTicketFromChainSelection({ symbol: "petrc35", securityId: "101", putOrCall: "Call" });

  assert.equal(stopEl.value, "");
  assert.deepEqual(observedStopValues, ["", "", "", ""]);
});
