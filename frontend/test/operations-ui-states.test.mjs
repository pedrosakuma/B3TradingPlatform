import { test } from "node:test";
import assert from "node:assert/strict";
import { installDomStub } from "./dom-stub.mjs";

installDomStub({
  ids: {
    "subaccount-rows": { tag: "tbody" },
    "session-phase-output": { tag: "pre" },
    "risk-output": { tag: "pre" },
    "reference-price-rows": { tag: "tbody" },
    "operations-feedback": { tag: "p", hidden: true },
    "operation-button": { tag: "button" },
  },
});

const ui = await import("../js/operationsUi.js");

test("administrative resources distinguish loading, empty, stale and error", () => {
  const rows = document.getElementById("reference-price-rows");

  ui.setReferenceResource({ status: "loading", data: null, error: null });
  assert.match(rows.innerHTML, /data-state="loading"/);

  ui.setReferenceResource({
    status: "ready",
    data: { symbols: [] },
    fetchedAt: Date.now(),
    error: null,
  });
  assert.match(rows.innerHTML, /data-state="empty"/);

  ui.setReferenceResource({
    status: "ready",
    data: { symbols: [{ symbol: "PETR4" }] },
    fetchedAt: Date.now() - 120_000,
    error: null,
  });
  assert.match(rows.innerHTML, /data-state="stale"/);

  ui.setReferenceResource({ status: "error", data: null, error: "backend unavailable" });
  assert.match(rows.innerHTML, /data-state="error"/);
  assert.match(rows.innerHTML, /backend unavailable/);
});

test("session phase and risk errors remain observable", () => {
  ui.setPhaseResource({ status: "error", data: null, error: "phase failed" });
  ui.setRiskResource({ status: "error", data: null, error: "risk failed" });
  assert.match(document.getElementById("session-phase-output").textContent, /phase failed/);
  assert.match(document.getElementById("risk-output").textContent, /risk failed/);
});

test("mutations expose pending, success and error states", async () => {
  const button = document.getElementById("operation-button");
  const feedback = document.getElementById("operations-feedback");
  let finish;
  const pending = ui.runMutation(button, () => new Promise((resolve) => { finish = resolve; }));
  assert.equal(button.disabled, true);
  assert.match(feedback.textContent, /pending/i);
  finish("Completed.");
  await pending;
  assert.equal(button.disabled, false);
  assert.equal(feedback.textContent, "Completed.");
  assert.match(feedback.className, /ok/);

  await ui.runMutation(button, async () => { throw new Error("Denied by backend"); });
  assert.equal(button.disabled, false);
  assert.match(feedback.textContent, /Denied by backend/);
  assert.match(feedback.className, /error/);
});
