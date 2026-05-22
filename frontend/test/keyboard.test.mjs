// Fase 5 (#401). Tests for the pure keyboard resolver in
// `frontend/js/keyboard.js`. We deliberately exercise resolveAction
// and dispatchKey with plain JS objects so the test runs under Node
// without a DOM.

import test from "node:test";
import assert from "node:assert/strict";

import { resolveAction, dispatchKey, SHORTCUTS } from "../js/keyboard.js";

function ev({ key, alt = false, shift = false, ctrl = false, meta = false }) {
  return { key, altKey: alt, shiftKey: shift, ctrlKey: ctrl, metaKey: meta };
}

test("resolveAction: Alt+digit maps to primary tabs", () => {
  assert.equal(resolveAction(ev({ key: "1", alt: true })), "tab:trader");
  assert.equal(resolveAction(ev({ key: "2", alt: true })), "tab:algos");
  assert.equal(resolveAction(ev({ key: "3", alt: true })), "tab:history");
  assert.equal(resolveAction(ev({ key: "4", alt: true })), "tab:settings");
  assert.equal(resolveAction(ev({ key: "5", alt: true })), "tab:admin");
  assert.equal(resolveAction(ev({ key: "6", alt: true })), "tab:compliance");
});

test("resolveAction: Alt+Shift+digit maps to Trader sub-tabs", () => {
  assert.equal(resolveAction(ev({ key: "1", alt: true, shift: true })), "trader-sub:markets");
  assert.equal(resolveAction(ev({ key: "2", alt: true, shift: true })), "trader-sub:watchlist");
  assert.equal(resolveAction(ev({ key: "3", alt: true, shift: true })), "trader-sub:auctions");
});

test("resolveAction: Alt+B / Alt+E toggle lower band", () => {
  assert.equal(resolveAction(ev({ key: "b", alt: true })), "trader-bottom:blotter");
  assert.equal(resolveAction(ev({ key: "e", alt: true })), "trader-bottom:executions");
});

test("resolveAction: plain letters fall back to upper-case lookup", () => {
  assert.equal(resolveAction(ev({ key: "b" })), "ticket:buy");
  assert.equal(resolveAction(ev({ key: "B", shift: true })), "ticket:buy");
  assert.equal(resolveAction(ev({ key: "s" })), "ticket:sell");
  assert.equal(resolveAction(ev({ key: "/" })), "focus:symbol");
});

test("resolveAction: unknown key returns null", () => {
  assert.equal(resolveAction(ev({ key: "z" })), null);
  assert.equal(resolveAction(ev({ key: "" })), null);
  assert.equal(resolveAction({ key: null }), null);
});

test("resolveAction: Escape always resolves", () => {
  assert.equal(resolveAction(ev({ key: "Escape" })), "modal:close");
});

test("dispatchKey: text-editing surface suppresses letter shortcuts", () => {
  const inputTarget = { tagName: "INPUT", type: "text" };
  assert.equal(dispatchKey(ev({ key: "b" }), inputTarget), null);
  assert.equal(dispatchKey(ev({ key: "/" }), inputTarget), null);
});

test("dispatchKey: Alt-combos pass even from text fields", () => {
  const inputTarget = { tagName: "INPUT", type: "text" };
  assert.equal(dispatchKey(ev({ key: "1", alt: true }), inputTarget), "tab:trader");
  assert.equal(dispatchKey(ev({ key: "b", alt: true }), inputTarget), "trader-bottom:blotter");
});

test("dispatchKey: Escape always passes, even from inputs", () => {
  const inputTarget = { tagName: "INPUT", type: "text" };
  assert.equal(dispatchKey(ev({ key: "Escape" }), inputTarget), "modal:close");
});

test("dispatchKey: non-text inputs (checkbox, button) don't suppress letters", () => {
  const checkbox = { tagName: "INPUT", type: "checkbox" };
  assert.equal(dispatchKey(ev({ key: "b" }), checkbox), "ticket:buy");
  const button = { tagName: "BUTTON" };
  assert.equal(dispatchKey(ev({ key: "s" }), button), "ticket:sell");
});

test("dispatchKey: contenteditable suppresses letter shortcuts", () => {
  const editable = { tagName: "DIV", isContentEditable: true };
  assert.equal(dispatchKey(ev({ key: "b" }), editable), null);
  // Alt still passes.
  assert.equal(dispatchKey(ev({ key: "1", alt: true }), editable), "tab:trader");
});

test("dispatchKey: null target behaves like a non-editing surface", () => {
  assert.equal(dispatchKey(ev({ key: "/" }), null), "focus:symbol");
});

test("SHORTCUTS is frozen and complete", () => {
  assert.equal(Object.isFrozen(SHORTCUTS), true);
  // Spot-check that every documented action is unique.
  const values = Object.values(SHORTCUTS);
  assert.equal(new Set(values).size, values.length);
});
