// Lifecycle tests for the Order Detail modal (#245 follow-up).
//
// These cover two regressions surfaced in the PR #246 review:
//   * P1 — modal must close when state.clearAll() runs (logout / session
//     expiry / WS "clear" frame), so the previous user's ClOrdID doesn't
//     stay rendered on top of the login screen with the capture-phase
//     keydown listener still live.
//   * P2 — closeOrderDetail() must restore focus to the originating row
//     even after orders.delta re-renders #blotter-body and detaches the
//     stored node reference; it re-resolves the row by ClOrdID.
//
// The rest of frontend/test/ runs without jsdom, so we hand-roll the
// minimum DOM surface ui.js touches (modal element, body containers,
// document.contains, document.querySelector for the row fallback,
// addEventListener/removeEventListener with capture).

import { test } from "node:test";
import assert from "node:assert/strict";

class FakeEl {
  constructor(tag) {
    this.tagName = String(tag || "div").toUpperCase();
    this.hidden = false;
    this.innerHTML = "";
    this.textContent = "";
    this._attrs = new Map();
    this.dataset = {};
    this._parent = null;
    this._children = [];
    this._focused = 0;
  }
  setAttribute(k, v) { this._attrs.set(k, String(v)); }
  removeAttribute(k) { this._attrs.delete(k); }
  getAttribute(k) { return this._attrs.has(k) ? this._attrs.get(k) : null; }
  focus() { this._focused++; }
  appendChild(c) { c._parent = this; this._children.push(c); return c; }
  querySelector() { return null; }
  querySelectorAll() { return []; }
}

function makeDocument() {
  const elements = new Map();
  const doc = {
    _root: new FakeEl("html"),
    body: new FakeEl("body"),
    getElementById: (id) => elements.get(id) ?? null,
    addEventListener: (type, fn /* , opts */) => {
      const key = `${type}`;
      const list = doc._listeners.get(key) ?? [];
      list.push(fn);
      doc._listeners.set(key, list);
    },
    removeEventListener: (type, fn /* , opts */) => {
      const list = doc._listeners.get(type);
      if (!list) return;
      const i = list.indexOf(fn);
      if (i >= 0) list.splice(i, 1);
    },
    contains: (node) => {
      // True if the node is one of the tracked elements OR was attached
      // to the synthetic blotter body (used to simulate detachment by
      // a re-render in the P2 test).
      if (!node) return false;
      if (node === doc.body) return true;
      for (const el of elements.values()) {
        if (el === node) return true;
      }
      const blotter = elements.get("blotter-body");
      if (blotter && blotter._children.includes(node)) return true;
      return false;
    },
    querySelector: (sel) => {
      const m = sel.match(/^#blotter-body tr\[data-clordid="([^"]+)"\]$/);
      if (!m) return null;
      const blotter = elements.get("blotter-body");
      if (!blotter) return null;
      return blotter._children.find(c => c.dataset?.clordid === m[1]) ?? null;
    },
    _listeners: new Map(),
    _elements: elements,
  };
  return doc;
}

function installFakeDom() {
  const doc = makeDocument();
  for (const id of [
    "order-detail-modal",
    "order-detail-body",
    "order-detail-exec-body",
    "order-detail-close",
    "order-detail-title",
    "blotter-body",
  ]) {
    const el = new FakeEl(id === "blotter-body" ? "tbody" : "div");
    if (id === "order-detail-modal") el.hidden = true;
    doc._elements.set(id, el);
  }
  globalThis.document = doc;
  globalThis.window = globalThis.window ?? {};
  globalThis.CSS = { escape: (s) => String(s).replace(/(["\\])/g, "\\$1") };
  // ui.js renderOrderDetail uses setTimeout to focus the close button;
  // we run it sync so we don't have pending timers when tests finish.
  globalThis.setTimeout = (fn) => { try { fn(); } catch { /* ignore */ } return 0; };
  return doc;
}

const doc = installFakeDom();
const ui = await import("../js/ui.js");
const state = await import("../js/state.js");

function makeRow(clOrdId) {
  const row = new FakeEl("tr");
  row.dataset.clordid = clOrdId;
  doc._elements.get("blotter-body").appendChild(row);
  return row;
}

function detachAllRows() {
  // Simulate orders.delta re-rendering #blotter-body — the stored
  // originatingRow reference is still alive but no longer in the DOM.
  doc._elements.get("blotter-body")._children = [];
}

test("closeOrderDetail is idempotent when nothing is open", () => {
  // Should be a no-op even before openOrderDetail has ever been called.
  ui.closeOrderDetail();
  ui.closeOrderDetail();
  const modal = doc.getElementById("order-detail-modal");
  assert.equal(modal.hidden, true);
});

test("clearAll fan-out closes an open order-detail modal (P1)", () => {
  // Wire the production fan-out: state.subscribe is the same hook
  // bindUi() uses; we drive renderForSlice's "all" branch by calling
  // closeOrderDetail directly on the "all" notification, which is
  // exactly what renderForSlice does.
  const unsub = state.subscribe((slice) => {
    if (slice === "all") ui.closeOrderDetail();
  });

  const row = makeRow("CLORD-LOGOUT-1");
  ui.openOrderDetail("CLORD-LOGOUT-1", row);
  const modal = doc.getElementById("order-detail-modal");
  assert.equal(modal.hidden, false, "precondition: modal opened");
  assert.ok(
    (doc._listeners.get("keydown") ?? []).length === 1,
    "precondition: capture-phase keydown listener installed",
  );

  state.clearAll();

  assert.equal(modal.hidden, true, "modal hidden after clearAll");
  assert.equal(
    (doc._listeners.get("keydown") ?? []).length, 0,
    "capture-phase keydown listener removed",
  );

  // Re-open / re-close cycle proves the listener wasn't leaked nor
  // double-registered (else listener count would creep above 1).
  ui.openOrderDetail("CLORD-LOGOUT-2", makeRow("CLORD-LOGOUT-2"));
  assert.equal((doc._listeners.get("keydown") ?? []).length, 1);
  ui.closeOrderDetail();
  assert.equal((doc._listeners.get("keydown") ?? []).length, 0);

  unsub();
});

test("closeOrderDetail re-resolves the originating row by ClOrdID after a re-render (P2)", () => {
  const clOrdId = "CLORD-REFOCUS-1";
  const originalRow = makeRow(clOrdId);
  ui.openOrderDetail(clOrdId, originalRow);

  // Simulate `orders.delta` replacing #blotter-body's children — the
  // node we cached in originatingRow is now detached.
  detachAllRows();
  assert.equal(doc.contains(originalRow), false);

  // The new row representing the same ClOrdID after the re-render.
  const replacementRow = makeRow(clOrdId);

  ui.closeOrderDetail();

  assert.ok(replacementRow._focused >= 1, "replacement row received focus");
  assert.equal(replacementRow.getAttribute("tabindex"), "-1");
});

test("closeOrderDetail falls back to #blotter-body when the row is gone entirely", () => {
  const clOrdId = "CLORD-REFOCUS-2";
  const row = makeRow(clOrdId);
  ui.openOrderDetail(clOrdId, row);

  // Drop the row and don't replace it (terminal + paginated off).
  detachAllRows();

  const blotter = doc.getElementById("blotter-body");
  const focusBefore = blotter._focused;
  ui.closeOrderDetail();
  assert.ok(blotter._focused > focusBefore, "blotter-body received focus as fallback");
});
