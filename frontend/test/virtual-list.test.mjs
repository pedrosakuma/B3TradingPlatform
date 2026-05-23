// Unit tests for frontend/js/virtualList.js (#409).
//
// `computeVisibleRange` is pure math and gets exhaustive coverage here.
// The DOM-facing `createVirtualList` factory is exercised via a tiny
// hand-rolled FakeEl, following the pattern in
// order-detail-lifecycle.test.mjs (the rest of frontend/test/ runs
// without jsdom). We only assert observable contract: scroll listener
// is attached, setItems renders the visible slice into the inner
// window, the spacer grows with item count, scrollToIndex repositions
// the window, and dispose detaches the listener.

import { test } from "node:test";
import assert from "node:assert/strict";

import { computeVisibleRange, createVirtualList } from "../js/virtualList.js";

// ── computeVisibleRange (pure) ───────────────────────────────────────

test("computeVisibleRange: empty list returns [0, 0)", () => {
  const r = computeVisibleRange({
    scrollTop: 0, viewportHeight: 200, rowHeight: 20, itemCount: 0,
  });
  assert.deepEqual(r, { start: 0, end: 0 });
});

test("computeVisibleRange: scroll=0 returns first window + overscan clamp", () => {
  // viewport=200px / row=20px → 10 visible rows; overscan=5 above (clamped to 0).
  const r = computeVisibleRange({
    scrollTop: 0, viewportHeight: 200, rowHeight: 20, itemCount: 100, overscan: 5,
  });
  assert.equal(r.start, 0);
  assert.equal(r.end, 15);  // 10 visible + 5 overscan below
});

test("computeVisibleRange: mid-scroll returns balanced overscan window", () => {
  // scrollTop=400 → firstVisible=20, lastVisible=30, ±5 overscan → [15, 35).
  const r = computeVisibleRange({
    scrollTop: 400, viewportHeight: 200, rowHeight: 20, itemCount: 100, overscan: 5,
  });
  assert.equal(r.start, 15);
  assert.equal(r.end, 35);
});

test("computeVisibleRange: end of list clamps end to itemCount", () => {
  const r = computeVisibleRange({
    scrollTop: 1800, viewportHeight: 200, rowHeight: 20, itemCount: 100, overscan: 5,
  });
  // firstVisible=90, lastVisible=100 → +5 = 105 clamped to 100.
  assert.equal(r.end, 100);
  assert.equal(r.start, 85);
});

test("computeVisibleRange: negative scrollTop is treated as 0", () => {
  const r = computeVisibleRange({
    scrollTop: -50, viewportHeight: 200, rowHeight: 20, itemCount: 100, overscan: 0,
  });
  assert.equal(r.start, 0);
  assert.equal(r.end, 10);
});

test("computeVisibleRange: rowHeight must be > 0", () => {
  assert.throws(
    () => computeVisibleRange({ scrollTop: 0, viewportHeight: 100, rowHeight: 0, itemCount: 10 }),
    /rowHeight/,
  );
});

test("computeVisibleRange: overscan defaults to 5", () => {
  const r = computeVisibleRange({
    scrollTop: 100, viewportHeight: 100, rowHeight: 20, itemCount: 50,
  });
  // firstVisible=5, lastVisible=10 → ±5 → [0, 15).
  assert.equal(r.start, 0);
  assert.equal(r.end, 15);
});

// ── createVirtualList (DOM) ──────────────────────────────────────────

class FakeEl {
  constructor(tag) {
    this.tagName = String(tag || "div").toUpperCase();
    this.innerHTML = "";
    this.style = {};
    this.clientHeight = 0;
    this.scrollTop = 0;
    this._listeners = new Map();
    this._children = [];
  }
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
  // Minimal querySelector — the helper looks up `.vlist-spacer` and
  // `.vlist-window`. We parse the innerHTML the helper just wrote and
  // hand back FakeEl stubs that mirror those nodes' style writes.
  querySelector(sel) {
    if (sel === ".vlist-spacer") return this._spacer ?? null;
    if (sel === ".vlist-window") return this._window ?? null;
    return null;
  }
}

function makeViewport({ clientHeight = 200 } = {}) {
  const v = new FakeEl("div");
  v.clientHeight = clientHeight;
  // The helper writes the spacer + window markup into innerHTML and
  // then queries them back. Intercept the first set to wire stubs.
  let attached = false;
  const innerHTMLDescriptor = {
    get() { return this._innerHTML ?? ""; },
    set(html) {
      this._innerHTML = html;
      if (!attached && html.includes("vlist-spacer")) {
        v._spacer = new FakeEl("div");
        v._window = new FakeEl("div");
        attached = true;
      }
    },
  };
  Object.defineProperty(v, "innerHTML", innerHTMLDescriptor);
  return v;
}

function makeItems(n) {
  return Array.from({ length: n }, (_, i) => ({ id: i, label: `row ${i}` }));
}

test("createVirtualList: rejects bad config", () => {
  const v = makeViewport();
  assert.throws(() => createVirtualList(null, { rowHeight: 20, renderRow: () => "" }),
    /viewport/);
  assert.throws(() => createVirtualList(v, { rowHeight: 0, renderRow: () => "" }),
    /rowHeight/);
  assert.throws(() => createVirtualList(v, { rowHeight: 20 }),
    /renderRow/);
});

test("createVirtualList: setItems renders only the visible slice", () => {
  const v = makeViewport({ clientHeight: 100 });  // 100/20 = 5 visible
  const vl = createVirtualList(v, {
    rowHeight: 20, overscan: 2,
    renderRow: (it) => `<div class="row">${it.label}</div>`,
  });

  vl.setItems(makeItems(1000));

  const range = vl.getVisibleRange();
  // scrollTop=0, viewport=100, row=20, overscan=2 → [0, 5+2=7).
  assert.deepEqual(range, { start: 0, end: 7 });
  // Spacer grows to total content height.
  assert.equal(v._spacer.style.height, `${1000 * 20}px`);
  // Inner window holds exactly the visible slice, no more.
  const rowMatches = v._window.innerHTML.match(/class="row"/g) ?? [];
  assert.equal(rowMatches.length, 7);
  assert.match(v._window.innerHTML, /row 0</);
  assert.match(v._window.innerHTML, /row 6</);
  assert.doesNotMatch(v._window.innerHTML, /row 7</);
});

test("createVirtualList: scrollToIndex repositions the window", () => {
  const v = makeViewport({ clientHeight: 100 });
  const vl = createVirtualList(v, {
    rowHeight: 20, overscan: 1,
    renderRow: (it) => `<div class="row">${it.label}</div>`,
  });
  vl.setItems(makeItems(200));

  vl.scrollToIndex(100);
  assert.equal(v.scrollTop, 100 * 20);
  const range = vl.getVisibleRange();
  // scrollTop=2000, viewport=100, row=20 → firstVisible=100, lastVisible=105,
  // overscan=1 → [99, 106).
  assert.deepEqual(range, { start: 99, end: 106 });
  // Window translateY follows the start index.
  assert.equal(v._window.style.transform, `translateY(${99 * 20}px)`);
});

test("createVirtualList: setItems with a prepended item shifts indices", () => {
  // Mirrors the executions stream: new fills land at index 0 because
  // renderExecutions reverses the array (newest first).
  const v = makeViewport({ clientHeight: 100 });
  const vl = createVirtualList(v, {
    rowHeight: 20, overscan: 0,
    renderRow: (it) => `<div class="row">${it.label}</div>`,
  });
  const items = makeItems(10);
  vl.setItems(items);
  assert.match(v._window.innerHTML, /row 0</);

  const withNew = [{ id: -1, label: "row NEW" }, ...items];
  vl.setItems(withNew);
  // Spacer reflects the new count; window still shows the head (newest).
  assert.equal(v._spacer.style.height, `${11 * 20}px`);
  assert.match(v._window.innerHTML, /row NEW</);
});

test("createVirtualList: dispose detaches the scroll listener", () => {
  const v = makeViewport();
  const vl = createVirtualList(v, {
    rowHeight: 20, renderRow: (it) => `<div>${it.label}</div>`,
  });
  vl.setItems(makeItems(50));
  assert.equal(v._listeners.get("scroll").length, 1);
  vl.dispose();
  assert.equal(v._listeners.get("scroll").length, 0);
});
