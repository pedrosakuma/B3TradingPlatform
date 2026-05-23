// Tiny fixed-row-height list virtualizer for static-shell ES modules.
// Renders only the rows visible inside a scrolling viewport (plus an
// overscan buffer) so the lower-band log doesn't bloat the DOM when
// the executions stream grows past a few hundred entries (#409).
//
// The helper is intentionally framework-free and split into two layers:
//   * `computeVisibleRange` — pure math, unit-tested in
//     frontend/test/virtual-list.test.mjs.
//   * `createVirtualList`   — DOM glue around an existing viewport
//     element, exercised live by renderExecutions().
//
// Usage:
//   const vl = createVirtualList(viewportEl, {
//     rowHeight: 24,
//     overscan: 6,
//     renderRow: (item, i) => `<div class="exec-row">${...}</div>`,
//   });
//   vl.setItems(items);                // replace / refresh
//   vl.scrollToIndex(0);               // jump to a row
//   vl.dispose();                      // detach scroll listener
//
// Viewport requirements:
//   * `position: relative` (the helper injects two absolute children).
//   * `overflow: auto` and an explicit / flex height so the browser
//     produces a usable scroll dimension.

const SPACER_CLASS = "vlist-spacer";
const WINDOW_CLASS = "vlist-window";

/**
 * Pure visible-range calculation. Returns the [start, end) item indices
 * that should be present in the DOM given the viewport's current scroll
 * state. End is exclusive (slice-friendly). Always clamped to
 * `[0, itemCount]`.
 *
 * @param {object} args
 * @param {number} args.scrollTop      Current scrollTop in px.
 * @param {number} args.viewportHeight Visible height in px.
 * @param {number} args.rowHeight      Fixed row height in px (> 0).
 * @param {number} args.itemCount      Total number of items in the
 *                                     virtual list.
 * @param {number} [args.overscan]     Extra rows rendered above and
 *                                     below the visible window for
 *                                     smoother scrolling. Defaults to 5.
 * @returns {{ start: number, end: number }}
 */
export function computeVisibleRange({
  scrollTop,
  viewportHeight,
  rowHeight,
  itemCount,
  overscan = 5,
}) {
  if (!Number.isFinite(rowHeight) || rowHeight <= 0) {
    throw new Error("computeVisibleRange: rowHeight must be > 0");
  }
  if (!Number.isFinite(itemCount) || itemCount <= 0) {
    return { start: 0, end: 0 };
  }
  const safeScrollTop = Math.max(0, scrollTop || 0);
  const safeViewport  = Math.max(0, viewportHeight || 0);
  const safeOverscan  = Math.max(0, overscan | 0);

  const firstVisible = Math.floor(safeScrollTop / rowHeight);
  const lastVisible  = Math.ceil((safeScrollTop + safeViewport) / rowHeight);

  const start = Math.max(0, firstVisible - safeOverscan);
  const end   = Math.min(itemCount, Math.max(start, lastVisible + safeOverscan));
  return { start, end };
}

/**
 * DOM virtualizer factory. Returns a controller object exposing
 * `setItems`, `scrollToIndex`, `getVisibleRange` and `dispose`.
 *
 * Calling `setItems` replaces the underlying array and re-renders the
 * visible window. Mutations on the original array are not observed —
 * call `setItems` again after any change. This mirrors how the
 * existing renderExecutions / renderBlotter helpers already operate
 * (full re-render on each delta), so wiring it in is mechanical.
 */
export function createVirtualList(viewport, opts) {
  if (!viewport || typeof viewport !== "object") {
    throw new Error("createVirtualList: viewport element is required");
  }
  const { rowHeight, renderRow, overscan = 5 } = opts || {};
  if (!Number.isFinite(rowHeight) || rowHeight <= 0) {
    throw new Error("createVirtualList: rowHeight must be > 0");
  }
  if (typeof renderRow !== "function") {
    throw new Error("createVirtualList: renderRow must be a function");
  }

  // Inject (or reuse) the spacer + window children. We accept that the
  // viewport is otherwise empty — callers using this helper hand
  // control of the inner DOM over to the virtual list.
  let spacer = viewport.querySelector?.(`.${SPACER_CLASS}`);
  let win    = viewport.querySelector?.(`.${WINDOW_CLASS}`);
  if (!spacer || !win) {
    viewport.innerHTML =
      `<div class="${SPACER_CLASS}" aria-hidden="true"></div>` +
      `<div class="${WINDOW_CLASS}"></div>`;
    spacer = viewport.querySelector(`.${SPACER_CLASS}`);
    win    = viewport.querySelector(`.${WINDOW_CLASS}`);
  }

  let items = [];
  let rafToken = 0;
  let lastRange = { start: 0, end: 0 };

  function viewportHeight() {
    // clientHeight may be 0 if the viewport is detached; fall back to
    // a generous default so tests that don't attach to a real DOM
    // still produce a sensible window.
    return viewport.clientHeight || 0;
  }

  function render() {
    rafToken = 0;
    const range = computeVisibleRange({
      scrollTop:      viewport.scrollTop || 0,
      viewportHeight: viewportHeight(),
      rowHeight,
      itemCount:      items.length,
      overscan,
    });
    lastRange = range;

    spacer.style.height = `${items.length * rowHeight}px`;
    win.style.transform = `translateY(${range.start * rowHeight}px)`;

    const slice = items.slice(range.start, range.end);
    let html = "";
    for (let i = 0; i < slice.length; i++) {
      html += renderRow(slice[i], range.start + i);
    }
    win.innerHTML = html;
  }

  function schedule() {
    if (rafToken) return;
    const raf = typeof requestAnimationFrame === "function"
      ? requestAnimationFrame
      : (fn) => setTimeout(fn, 16);
    rafToken = raf(render);
  }

  function onScroll() { schedule(); }
  viewport.addEventListener?.("scroll", onScroll, { passive: true });

  function setItems(next) {
    items = Array.isArray(next) ? next : [];
    render();
  }

  function scrollToIndex(index) {
    const i = Math.max(0, Math.min(items.length - 1, index | 0));
    viewport.scrollTop = i * rowHeight;
    render();
  }

  function getVisibleRange() {
    return { ...lastRange };
  }

  function dispose() {
    viewport.removeEventListener?.("scroll", onScroll);
    if (rafToken && typeof cancelAnimationFrame === "function") {
      cancelAnimationFrame(rafToken);
    }
    rafToken = 0;
  }

  return { setItems, scrollToIndex, getVisibleRange, dispose };
}
