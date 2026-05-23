// Unit tests for frontend/js/mobileDrawer.js (#408).
//
// Pure-DOM unit tests using the same FakeEl pattern as
// order-detail-lifecycle.test.mjs / virtual-list.test.mjs. No jsdom.

import { test } from "node:test";
import assert from "node:assert/strict";

class FakeEl {
  constructor(tag) {
    this.tagName = String(tag || "div").toUpperCase();
    this.hidden = false;
    this.innerHTML = "";
    this.textContent = "";
    this.dataset = {};
    this._attrs = new Map();
    this._classes = new Set();
    this._listeners = new Map();
    this._children = [];
    this._parent = null;
    this._focused = 0;
    this.classList = {
      add: (c) => this._classes.add(c),
      remove: (c) => this._classes.delete(c),
      toggle: (c, on) => { if (on) this._classes.add(c); else this._classes.delete(c); },
      contains: (c) => this._classes.has(c),
    };
  }
  setAttribute(k, v) { this._attrs.set(k, String(v)); }
  removeAttribute(k) { this._attrs.delete(k); }
  getAttribute(k) { return this._attrs.has(k) ? this._attrs.get(k) : null; }
  focus() { this._focused++; }
  addEventListener(type, fn) {
    const list = this._listeners.get(type) ?? [];
    list.push(fn);
    this._listeners.set(type, list);
  }
  removeEventListener(type, fn) {
    const list = this._listeners.get(type);
    if (!list) return;
    const i = list.indexOf(fn);
    if (i >= 0) list.splice(i, 1);
  }
  dispatch(type, evt = {}) {
    const list = this._listeners.get(type) ?? [];
    for (const fn of list) fn(evt);
  }
  appendChild(c) { c._parent = this; this._children.push(c); return c; }
  // Used by mobileDrawer.list.querySelector("button:not([hidden])") for
  // initial focus + by syncFromTablist's `tablist.querySelectorAll`.
  querySelector(sel) {
    if (sel === "button:not([hidden])") {
      return this._children.find((c) => c.tagName === "BUTTON" && !c.hidden) ?? null;
    }
    return null;
  }
  querySelectorAll(sel) {
    if (sel === "button[data-view]") {
      return this._children.filter((c) => c.tagName === "BUTTON" && c.dataset?.view);
    }
    return [];
  }
}

function installFakeDocument() {
  const listeners = new Map();
  globalThis.document = {
    addEventListener: (type, fn) => {
      const list = listeners.get(type) ?? [];
      list.push(fn);
      listeners.set(type, list);
    },
    removeEventListener: (type, fn) => {
      const list = listeners.get(type);
      if (!list) return;
      const i = list.indexOf(fn);
      if (i >= 0) list.splice(i, 1);
    },
    _dispatch: (type, evt) => {
      const list = listeners.get(type) ?? [];
      for (const fn of list) fn(evt);
    },
    _listeners: listeners,
  };
}

installFakeDocument();
const { bindMobileDrawer } = await import("../js/mobileDrawer.js");

function makeTablist({ tabs }) {
  const tl = new FakeEl("div");
  for (const t of tabs) {
    const btn = new FakeEl("button");
    btn.dataset.view = t.view;
    btn.textContent = t.label;
    btn.hidden = !!t.hidden;
    if (t.active) {
      btn.classList.add("active");
      btn.setAttribute("aria-selected", "true");
    } else {
      btn.setAttribute("aria-selected", "false");
    }
    tl.appendChild(btn);
  }
  return tl;
}

function build({ tabs = [{ view: "trader", label: "Trading", active: true }, { view: "algos", label: "Algos" }] } = {}) {
  const trigger  = new FakeEl("button");
  const drawer   = new FakeEl("nav");
  const list     = new FakeEl("div");
  const backdrop = new FakeEl("div");
  const selected = [];
  const tablist  = makeTablist({ tabs });
  const ctrl = bindMobileDrawer({
    trigger, drawer, list, backdrop,
    onSelect: (v) => selected.push(v),
  });
  ctrl.syncFromTablist(tablist);
  // syncFromTablist writes innerHTML; re-attach a child for the
  // focus-first-button path so FakeEl.querySelector can find one.
  for (const t of tabs) {
    const btn = new FakeEl("button");
    btn.dataset.view = t.view;
    btn.hidden = !!t.hidden;
    list.appendChild(btn);
  }
  return { ctrl, trigger, drawer, list, backdrop, selected };
}

test("bindMobileDrawer: rejects missing required args", () => {
  assert.throws(() => bindMobileDrawer({}), /required/);
  assert.throws(
    () => bindMobileDrawer({ trigger: new FakeEl("button") }),
    /required/,
  );
});

test("bindMobileDrawer: starts closed with coherent aria attrs", () => {
  const { ctrl, trigger, drawer, backdrop } = build();
  assert.equal(ctrl.isOpen(), false);
  assert.equal(drawer.hidden, true);
  assert.equal(backdrop.hidden, true);
  assert.equal(trigger.getAttribute("aria-expanded"), "false");
  assert.match(trigger.getAttribute("aria-label"), /Open/);
});

test("bindMobileDrawer: trigger click opens, second click closes", () => {
  const { ctrl, trigger, drawer, backdrop } = build();
  trigger.dispatch("click", {});
  assert.equal(ctrl.isOpen(), true);
  assert.equal(drawer.hidden, false);
  assert.equal(backdrop.hidden, false);
  assert.equal(trigger.getAttribute("aria-expanded"), "true");
  assert.ok(drawer._classes.has("mobile-drawer-open"));

  trigger.dispatch("click", {});
  assert.equal(ctrl.isOpen(), false);
  assert.equal(drawer.hidden, true);
  assert.equal(trigger.getAttribute("aria-expanded"), "false");
});

test("bindMobileDrawer: selecting an item closes drawer and calls onSelect", () => {
  const { ctrl, trigger, list, selected, drawer } = build();
  ctrl.open();
  // Synthesize a click on a drawer item — list.addEventListener handler
  // walks `e.target.closest("button[data-view]")`. Provide a closest()
  // that returns the matching button.
  const btn = list._children.find((c) => c.dataset.view === "algos");
  list.dispatch("click", {
    target: { closest: (sel) => (sel === "button[data-view]" ? btn : null) },
  });
  assert.deepEqual(selected, ["algos"]);
  assert.equal(ctrl.isOpen(), false);
  assert.equal(drawer.hidden, true);
});

test("bindMobileDrawer: backdrop click closes the drawer", () => {
  const { ctrl, backdrop, drawer } = build();
  ctrl.open();
  backdrop.dispatch("click", {});
  assert.equal(ctrl.isOpen(), false);
  assert.equal(drawer.hidden, true);
});

test("bindMobileDrawer: Esc closes only when open", () => {
  const { ctrl } = build();
  let prevented = 0;
  // Closed: Esc must be a no-op (don't fight other modal handlers).
  document._dispatch("keydown", { key: "Escape", preventDefault: () => prevented++ });
  assert.equal(ctrl.isOpen(), false);
  assert.equal(prevented, 0);

  ctrl.open();
  document._dispatch("keydown", { key: "Escape", preventDefault: () => prevented++ });
  assert.equal(ctrl.isOpen(), false);
  assert.equal(prevented, 1);
});

test("bindMobileDrawer: syncFromTablist mirrors visibility + active state", () => {
  const tabs = [
    { view: "trader", label: "Trading", active: true },
    { view: "algos",  label: "Algos" },
    { view: "admin",  label: "Admin", hidden: true },
  ];
  const { list } = build({ tabs });
  // syncFromTablist already ran; assert the rendered innerHTML.
  assert.match(list.innerHTML, /data-view="trader"/);
  assert.match(list.innerHTML, /data-view="algos"/);
  assert.match(list.innerHTML, /data-view="admin"[^>]* hidden/);
  assert.match(list.innerHTML, /class="mobile-drawer-item active"[^>]*aria-selected="true"/);
});

test("bindMobileDrawer: dispose detaches all listeners", () => {
  const { ctrl, trigger, list, backdrop } = build();
  assert.equal(trigger._listeners.get("click").length, 1);
  assert.equal(list._listeners.get("click").length, 1);
  assert.equal(backdrop._listeners.get("click").length, 1);
  assert.equal(document._listeners.get("keydown").length >= 1, true);
  ctrl.dispose();
  assert.equal(trigger._listeners.get("click").length, 0);
  assert.equal(list._listeners.get("click").length, 0);
  assert.equal(backdrop._listeners.get("click").length, 0);
});
