// Render-only DOM layer. State lives in state.js; this module wires
// updates to elements and exposes onAction hooks for user gestures.

import {
  getState, subscribe, isTerminalOrderStatus,
  getPhase, getAuctionState, isAuctionPhase, setAuctionPanelSymbol,
  isStopOrderType, isGtdTif, ORDER_TYPE_CHIP,
  computeHeatmapVolumes, HEATMAP_WINDOW_MS,
} from "./state.js";
import { rulesFor } from "./validation.js";
import { tabsForRole } from "./complianceUi.js";
import { createVirtualList } from "./virtualList.js";
import { bindMobileDrawer } from "./mobileDrawer.js";

const $ = (id) => document.getElementById(id);

// ── Number formatting (en-US thousands separators / decimal point).
// #340 quick-wins: unified locale so quantities and prices render with
// 1,000.00 separators across the trader UI regardless of OS locale.
// B3 traders expect Brazilian locale (`100.000,00`) for quantities,
// prices and notionals. Centralised here so every panel stays in sync
// and we have a single place to flip the locale if the product call
// changes later.
const _qtyFmt = new Intl.NumberFormat("en-US", { maximumFractionDigits: 0 });
const _pxFmt  = new Intl.NumberFormat("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
function fmtQty(n) {
  if (n == null || n === "" || Number.isNaN(Number(n))) return "—";
  return _qtyFmt.format(Number(n));
}
function fmtPx(n) {
  if (n == null || n === "" || Number.isNaN(Number(n))) return "—";
  return _pxFmt.format(Number(n));
}
export { fmtQty, fmtPx };

// #342: Modal focus restoration. When a modal opens we snapshot the
// element that had focus so closing it can return the user to where
// they were. Keyed by modal id so concurrent / stacked modals don't
// stomp each other's saved target.
const _modalReturnFocus = new Map();

// #342: Positions sort + executions log symbol filter — purely view-local
// UI state with sessionStorage persistence. Not in state.js because no
// other module depends on it; keeping it in the renderer avoids cross-
// module churn for what is a per-tab cosmetic preference.
const POSITIONS_SORT_KEY = "b3tp.positions.sort";
const EXEC_FILTER_KEY    = "b3tp.executions.symbolFilter";
// FE-OPT-3 (#499). Group-by-underlying toggle for positions panel.
const POSITIONS_GROUP_KEY = "b3tp.positions.grouped";

// Default: largest absolute net first — the position that needs the
// most attention. Click cycles ascending → descending → none (back to
// default). String columns omit the "none" leg since alpha-asc is a
// useful baseline.
const POSITIONS_SORT_DEFAULT = { col: "absNet", dir: "desc" };
let _positionsSort = POSITIONS_SORT_DEFAULT;
let _execSymbolFilter = "";
// FE-OPT-3 (#499). Grouping state.
let _positionsGrouped = false;

function readPositionsSort() {
  try {
    const raw = sessionStorage.getItem(POSITIONS_SORT_KEY);
    if (!raw) return { ...POSITIONS_SORT_DEFAULT };
    const parsed = JSON.parse(raw);
    const col = ["symbol", "absNet", "price"].includes(parsed?.col) ? parsed.col : "absNet";
    const dir = parsed?.dir === "asc" ? "asc" : "desc";
    return { col, dir };
  } catch { return { ...POSITIONS_SORT_DEFAULT }; }
}
function writePositionsSort(s) {
  try { sessionStorage.setItem(POSITIONS_SORT_KEY, JSON.stringify(s)); } catch { /* swallow */ }
}
function readExecSymbolFilter() {
  try { return sessionStorage.getItem(EXEC_FILTER_KEY) || ""; }
  catch { return ""; }
}
function writeExecSymbolFilter(v) {
  try {
    if (v) sessionStorage.setItem(EXEC_FILTER_KEY, v);
    else   sessionStorage.removeItem(EXEC_FILTER_KEY);
  } catch { /* swallow */ }
}
// FE-OPT-3 (#499). Grouped positions persistence.
function readPositionsGrouped() {
  try { return sessionStorage.getItem(POSITIONS_GROUP_KEY) === "1"; }
  catch { return false; }
}
function writePositionsGrouped(v) {
  try {
    if (v) sessionStorage.setItem(POSITIONS_GROUP_KEY, "1");
    else   sessionStorage.removeItem(POSITIONS_GROUP_KEY);
  } catch { /* swallow */ }
}

function rememberFocusForModal(modalId) {
  const el = document.activeElement;
  // Skip <body> / null / the dialog itself — restoring those is a no-op
  // and tends to mask real focus bugs.
  if (!el || el === document.body || el.tagName === "BODY") return;
  _modalReturnFocus.set(modalId, el);
}

function restoreFocusForModal(modalId) {
  const el = _modalReturnFocus.get(modalId);
  _modalReturnFocus.delete(modalId);
  if (!el || !document.contains(el)) return;
  // If the saved target was hidden or removed (e.g. session-modal +
  // logout flow that swapped to the login view), focusing throws or
  // silently fails. Either way we don't want to crash on close.
  try { el.focus({ preventScroll: true }); } catch { /* ignore */ }
}

let onSubmitOrder = () => {};
let onCancelOrder = () => {};
let onCancelAll   = () => {};
let onModifyOrder = () => {};
let onLogout      = () => {};
let onApplyMd     = () => {};
let onSwitchView  = () => {};
let onBlotterFilter  = () => {};
let onBlotterPage    = () => {};
let onSelectOrder    = () => {};
let onKeyboardCancel = () => {};
let onSelectChartResolution = () => {};
let onSelectSymbol = () => {};
let onToggleTapeShowAll = () => {};

export function setHandlers(handlers) {
  onSubmitOrder    = handlers.onSubmitOrder    ?? onSubmitOrder;
  onCancelOrder    = handlers.onCancelOrder    ?? onCancelOrder;
  onCancelAll      = handlers.onCancelAll      ?? onCancelAll;
  onModifyOrder    = handlers.onModifyOrder    ?? onModifyOrder;
  onLogout         = handlers.onLogout         ?? onLogout;
  onApplyMd        = handlers.onApplyMd        ?? onApplyMd;
  onSwitchView     = handlers.onSwitchView     ?? onSwitchView;
  onBlotterFilter  = handlers.onBlotterFilter  ?? onBlotterFilter;
  onBlotterPage    = handlers.onBlotterPage    ?? onBlotterPage;
  onSelectOrder    = handlers.onSelectOrder    ?? onSelectOrder;
  onKeyboardCancel = handlers.onKeyboardCancel ?? onKeyboardCancel;
  onSelectChartResolution = handlers.onSelectChartResolution ?? onSelectChartResolution;
  onSelectSymbol      = handlers.onSelectSymbol      ?? onSelectSymbol;
  onToggleTapeShowAll = handlers.onToggleTapeShowAll ?? onToggleTapeShowAll;
}

export function showLogin() {
  $("login-view").hidden = false;
  const shell = $("app-shell");
  if (shell) shell.hidden = true;
  $("trader-view").hidden = true;
  $("admin-view").hidden = true;
  const hist = $("history-view");
  if (hist) hist.hidden = true;
  const compliance = $("compliance-view");
  if (compliance) compliance.hidden = true;
  const settings = $("settings-view");
  if (settings) settings.hidden = true;
  const algos = $("algos-view");
  if (algos) algos.hidden = true;
  // Default to the login card; a user that previously toggled to the
  // signup card and was bumped back to login (e.g. logout, expiry)
  // shouldn't land staring at the signup form.
  const loginCard = document.getElementById("login-form");
  const signupCard = document.getElementById("signup-form");
  if (loginCard) loginCard.hidden = false;
  if (signupCard) signupCard.hidden = true;
  setViewToggleVisible(false, "trader");
}

export function showTrader() {
  $("login-view").hidden = true;
  const shell = $("app-shell");
  if (shell) shell.hidden = false;
  $("trader-view").hidden = false;
  $("admin-view").hidden = true;
  const hist = $("history-view");
  if (hist) hist.hidden = true;
  const compliance = $("compliance-view");
  if (compliance) compliance.hidden = true;
  const settings = $("settings-view");
  if (settings) settings.hidden = true;
  const algos = $("algos-view");
  if (algos) algos.hidden = true;
}

function setViewToggleVisible(visible, current) {
  const wrap = $("view-toggle");
  if (!wrap) return;
  // Fase 1 (#397). The tablist itself is always visible while logged
  // in — only the per-role gating decides which tab buttons render.
  // `visible=false` is now the logged-out signal coming from showLogin.
  wrap.hidden = !visible;
  const role = getState().user?.role;
  const allowed = visible ? new Set(tabsForRole(role)) : new Set();
  for (const btn of wrap.querySelectorAll("button[data-view]")) {
    const view = btn.dataset.view;
    btn.hidden = !allowed.has(view);
    btn.classList.toggle("active", view === current);
    btn.setAttribute("aria-selected", view === current ? "true" : "false");
  }
  // #408. Mirror per-role visibility + active state into the mobile
  // drawer so the two stay in sync (canonical source = tablist).
  const trigger = $("mobile-nav-trigger");
  if (trigger) trigger.hidden = !visible;
  const drawer = $("mobile-nav-drawer");
  if (drawer && _mobileDrawer) _mobileDrawer.syncFromTablist(wrap);
}

export function setLoginError(message) {
  const el = $("login-error");
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false; el.textContent = message;
}

export function setLoginSubmitting(submitting) {
  const btn = $("login-submit");
  if (!btn) return;
  btn.disabled = !!submitting;
  btn.textContent = submitting ? "Signing in…" : "Sign in";
}

// ── Session-expiry modal ───────────────────────────────────────────
let sessionModalSubmit = null;
let sessionModalLogout = null;
let sessionModalBackdrop = null;
let sessionModalKey = null;

export function openSessionModal({ onRenew, onLogout }) {
  const modal = $("session-modal");
  const form  = $("session-modal-form");
  const pwd   = $("session-modal-password");
  const logoutBtn = $("session-modal-logout");
  if (!modal || !form || !pwd) return;
  rememberFocusForModal("session-modal");
  setSessionModalError(null);
  pwd.value = "";
  modal.hidden = false;
  // Replace handlers (idempotent across multiple opens).
  if (sessionModalSubmit) form.removeEventListener("submit", sessionModalSubmit);
  if (sessionModalLogout) logoutBtn?.removeEventListener("click", sessionModalLogout);
  if (sessionModalBackdrop) modal.removeEventListener("click", sessionModalBackdrop);
  if (sessionModalKey) document.removeEventListener("keydown", sessionModalKey);
  sessionModalSubmit = (e) => {
    e.preventDefault();
    const value = pwd.value;
    if (!value) { setSessionModalError("password required"); return; }
    onRenew?.(value);
  };
  sessionModalLogout = () => onLogout?.();
  // Click on the backdrop (anywhere outside the modal-card) = logout.
  // Defensive escape hatch so a user staring at the modal after a key
  // rotation or stale-token boot doesn't get trapped if the inner
  // Logout button somehow fails (or they're confused by it).
  sessionModalBackdrop = (e) => {
    if (e.target === modal) onLogout?.();
  };
  // Esc on the session modal = logout (consistent with backdrop click).
  sessionModalKey = (e) => {
    if (e.key === "Escape" && !modal.hidden) {
      e.preventDefault();
      onLogout?.();
    }
  };
  form.addEventListener("submit", sessionModalSubmit);
  logoutBtn?.addEventListener("click", sessionModalLogout);
  modal.addEventListener("click", sessionModalBackdrop);
  document.addEventListener("keydown", sessionModalKey);
  // Defer focus to the next frame so backdrop transitions don't steal it.
  requestAnimationFrame(() => pwd.focus());
}

export function closeSessionModal() {
  const modal = $("session-modal");
  if (!modal) return;
  modal.hidden = true;
  setSessionModalError(null);
  const pwd = $("session-modal-password");
  if (pwd) pwd.value = "";
  if (sessionModalKey) {
    document.removeEventListener("keydown", sessionModalKey);
    sessionModalKey = null;
  }
  restoreFocusForModal("session-modal");
}

export function setSessionModalError(message) {
  const el = $("session-modal-error");
  if (!el) return;
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false; el.textContent = message;
}

// ── Modify-order modal (slice 5 of #122) ───────────────────────────
//
// UX-vs-wire contract (#421 follow-up):
//
//   The "New quantity" field shown to the trader represents the
//   **new remaining (leaves)** quantity they want to keep working.
//   After a partial fill, "leaves" is the number the trader
//   actually thinks about ("I have 100 still working — change it
//   to 80"); pre-filling the field with the original total qty
//   would force the trader to mentally re-add the cum, which is
//   easy to get wrong (esp. on a busy book).
//
//   The wire, however, MUST stay 100% FIX-conformant:
//   OrderCancelReplaceRequest.OrderQty (38) is always the
//   **new total** = cumQty + newLeaves, with the invariant
//   OrderQty ≥ CumQty. The conversion happens at submit time
//   inside `computeWireOrderQty`, and a live hint under the input
//   ("→ total wire = cum 100 + remaining 80 = 180") makes the
//   translation visible to the trader so the UX abstraction never
//   hides what we are actually sending.
//
//   See `modifyModalDefaultLeaves` / `computeWireOrderQty` for the
//   pure helpers exercised by frontend/test/modify-modal-leaves.

// Pure helper: choose the input default for the modify modal.
// Falls back to the order's total quantity for orders that never
// touched a fill (cum=0 ⇒ leaves==quantity anyway).
export function modifyModalDefaultLeaves(order) {
  if (!order) return "";
  const leaves = Number(order.leavesQuantity);
  if (Number.isFinite(leaves) && leaves > 0) return leaves;
  const qty = Number(order.quantity);
  if (Number.isFinite(qty) && qty > 0) return qty;
  return "";
}

// Pure helper: translate the UX-level "new leaves" back into the
// wire-level OrderQty. Returns NaN on bad input so callers can
// short-circuit with a validation error.
export function computeWireOrderQty(newLeaves, cumQty) {
  const lv = Number(newLeaves);
  const cum = Number(cumQty);
  if (!Number.isFinite(lv) || !Number.isInteger(lv) || lv <= 0) return NaN;
  if (!Number.isFinite(cum) || cum < 0) return NaN;
  return cum + lv;
}

// The modal stores its "current target" on the form element via
// dataset so the submit handler doesn't depend on UI state churn
// while the modal is open (e.g. background ER updates the row but
// the trader's intent is still keyed to the originally-clicked
// ClOrdID).
function openModifyModal(clOrdId) {
  const st = getState();
  const order = st.orders?.get(clOrdId);
  if (!order) return;
  if (isTerminalOrderStatus(order.status)) return;
  if (st.inflightModifies?.has(clOrdId)) return;
  if (st.inflightCancels?.has(clOrdId)) return;

  const modal = $("modify-modal");
  const form  = $("modify-modal-form");
  const qty   = $("modify-modal-qty");
  const price = $("modify-modal-price");
  const summary = $("modify-modal-summary");
  const hint  = $("modify-modal-wire-hint");
  const error = $("modify-modal-error");
  if (!modal || !form || !qty) return;

  // Snapshot cumQty on the form so the submit handler converts
  // leaves→wire against the exact value the trader saw when the
  // modal opened, even if a background ER bumps cum mid-edit.
  form.dataset.clordid = clOrdId;
  const cumSnapshot = Number(order.cumulativeQuantity) || 0;
  form.dataset.cumqty = String(cumSnapshot);

  qty.value = modifyModalDefaultLeaves(order);
  if (price) {
    if (order.price == null) {
      price.value = "";
      price.disabled = (order.type === "Market");
    } else {
      price.value = order.price;
      price.disabled = false;
    }
  }
  if (summary) {
    const px = order.price == null ? "MKT" : order.price;
    summary.textContent =
      `Order ${order.clOrdId} — ${order.symbol} ${order.side} ${order.type} ` +
      `(qty ${order.quantity}, leaves ${order.leavesQuantity}, cum ${order.cumulativeQuantity}, px ${px})`;
  }
  refreshModifyWireHint();
  if (error) { error.hidden = true; error.textContent = ""; }
  rememberFocusForModal("modify-modal");
  modal.hidden = false;
  // Focus the qty field so keyboard-only operators don't need a
  // round-trip through the mouse to change the size.
  setTimeout(() => qty.focus(), 0);
}

// Recompute the "→ wire total = cum + remaining = N" hint shown
// under the qty input. Called on open and on every input change so
// the trader always sees the OrderQty that will hit the wire.
function refreshModifyWireHint() {
  const form  = $("modify-modal-form");
  const qty   = $("modify-modal-qty");
  const hint  = $("modify-modal-wire-hint");
  if (!form || !qty || !hint) return;
  const cum = Number(form.dataset.cumqty) || 0;
  const raw = qty.value.trim();
  if (raw === "") {
    hint.textContent = `Wire OrderQty = cum ${cum} + remaining ?`;
    hint.classList.remove("error");
    return;
  }
  const wire = computeWireOrderQty(raw, cum);
  if (!Number.isFinite(wire)) {
    hint.textContent = "Remaining must be a positive integer.";
    hint.classList.add("error");
    return;
  }
  hint.textContent = `Wire OrderQty = cum ${cum} + remaining ${Number(raw)} = ${wire}`;
  hint.classList.remove("error");
}

export function closeModifyModal() {
  const modal = $("modify-modal");
  const form  = $("modify-modal-form");
  const hint  = $("modify-modal-wire-hint");
  if (!modal) return;
  modal.hidden = true;
  if (form) {
    delete form.dataset.clordid;
    delete form.dataset.cumqty;
  }
  if (hint) { hint.textContent = ""; hint.classList.remove("error"); }
  setModifyModalError(null);
  setModifyModalSubmitting(false);
  restoreFocusForModal("modify-modal");
}

export function setModifyModalError(message) {
  const el = $("modify-modal-error");
  if (!el) return;
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false; el.textContent = message;
}

export function setModifyModalSubmitting(busy) {
  const submit = $("modify-modal-submit");
  if (!submit) return;
  submit.disabled = !!busy;
  submit.textContent = busy ? "Sending…" : "Send modify";
}

// ── Order detail modal (#245) ──────────────────────────────────────
//
// Read-only drill-down per ClOrdID. Surfaces the executions that
// belong to a single client order (no Replace-chain following, no
// algo slice expansion — those are deliberately out of scope per the
// issue). Re-renders idempotently when new ERs arrive while open.

let openClOrdId = null;
let originatingRow = null;
let orderDetailKeyHandler = null;

/**
 * Volume-Weighted Average Price across the executions array, restricted
 * to ones that actually moved size (lastQuantity > 0). Returns `null`
 * when no qualifying fills exist so callers can render `—` instead of
 * a misleading `0.00`.
 */
export function vwapOf(executions) {
  if (!Array.isArray(executions) || executions.length === 0) return null;
  let notional = 0;
  let qty = 0;
  for (const e of executions) {
    const lq = Number(e?.lastQuantity);
    const lp = Number(e?.lastPrice);
    if (!Number.isFinite(lq) || lq <= 0) continue;
    if (!Number.isFinite(lp)) continue;
    notional += lq * lp;
    qty += lq;
  }
  if (qty === 0) return null;
  return notional / qty;
}

/**
 * Filters the executions ring to only those whose ClOrdID matches.
 * Comparison is string-based (DtoMappings already `.toString()`s the
 * server-side ClOrdId so equality on numbers is unsafe).
 */
export function executionsForClOrdId(executions, clOrdId) {
  if (!Array.isArray(executions) || clOrdId == null) return [];
  const key = String(clOrdId);
  return executions.filter(e => e != null && String(e.clOrdId) === key);
}

function fmtExecTime(ts) {
  if (ts == null) return "—";
  const d = new Date(ts);
  if (Number.isNaN(d.getTime())) return "—";
  return d.toISOString().slice(11, 23);
}

function focusableInDialog(dialog) {
  const sel = [
    "a[href]", "button:not([disabled])", "input:not([disabled])",
    "select:not([disabled])", "textarea:not([disabled])",
    "[tabindex]:not([tabindex='-1'])",
  ].join(",");
  return Array.from(dialog.querySelectorAll(sel))
    .filter(el => !el.hidden && el.offsetParent !== null);
}

export function openOrderDetail(clOrdId, row) {
  if (clOrdId == null) return;
  const modal = $("order-detail-modal");
  if (!modal) return;
  openClOrdId = String(clOrdId);
  originatingRow = row ?? null;
  modal.hidden = false;
  renderOrderDetail();
  if (!orderDetailKeyHandler) {
    orderDetailKeyHandler = (e) => onOrderDetailKeydown(e);
    document.addEventListener("keydown", orderDetailKeyHandler, true);
  }
  // Focus the close button so keyboard-only users land inside the
  // dialog immediately; focus trap takes care of the rest.
  setTimeout(() => $("order-detail-close")?.focus(), 0);
}

export function closeOrderDetail() {
  const modal = $("order-detail-modal");
  if (!modal) return;
  // Idempotent: nothing to do when the modal isn't currently open.
  // This matters because state.clearAll() fan-outs to "all" and we
  // call closeOrderDetail() unconditionally from renderForSlice — it
  // must be safe regardless of whether the user actually had it open.
  if (openClOrdId == null && modal.hidden && !orderDetailKeyHandler) return;
  modal.hidden = true;
  const body = $("order-detail-body");
  if (body) body.innerHTML = "";
  const execBody = $("order-detail-exec-body");
  if (execBody) execBody.innerHTML = "";
  if (orderDetailKeyHandler) {
    document.removeEventListener("keydown", orderDetailKeyHandler, true);
    orderDetailKeyHandler = null;
  }
  const row = originatingRow;
  const clOrdIdToRestore = openClOrdId;
  openClOrdId = null;
  originatingRow = null;
  // Return focus to the originating row. Live `orders.delta` updates
  // re-render #blotter-body and detach the stored node, so when the
  // original reference is gone we re-resolve the row by ClOrdID. If
  // even that is missing (terminalized + paginated off), fall back to
  // the blotter body so focus stays in the trader pane instead of
  // silently landing on <body>.
  let target = (row && document.contains(row)) ? row : null;
  if (!target && clOrdIdToRestore != null && document.querySelector) {
    const escaped = (typeof CSS !== "undefined" && CSS.escape)
      ? CSS.escape(clOrdIdToRestore)
      : String(clOrdIdToRestore).replace(/\\/g, "\\\\").replace(/"/g, '\\"');
    try {
      target = document.querySelector(`#blotter-body tr[data-clordid="${escaped}"]`);
    } catch { target = null; }
  }
  if (!target) target = $("blotter-body") || null;
  if (target) {
    try { target.setAttribute("tabindex", "-1"); } catch { /* ignore */ }
    try { target.focus({ preventScroll: true }); } catch { /* ignore */ }
  }
}

function refreshOpenOrderDetail() {
  if (openClOrdId == null) return;
  renderOrderDetail();
}

function renderOrderDetail() {
  if (openClOrdId == null) return;
  const st = getState();
  const order = st.orders?.get(openClOrdId);
  const execs = executionsForClOrdId(st.executions, openClOrdId);
  renderOrderDetailHeader(order, execs);
  renderOrderDetailExecutions(execs);
}

function renderOrderDetailHeader(order, execs) {
  const body = $("order-detail-body");
  const title = $("order-detail-title");
  if (!body) return;
  if (!order) {
    if (title) title.textContent = `Order ${openClOrdId}`;
    body.innerHTML = `<div class="field field-wide"><span class="value">Order not found in current cache.</span></div>`;
    return;
  }
  if (title) title.textContent = `Order ${order.clOrdId}`;
  const vwap = vwapOf(execs);
  const qty = Number(order.quantity) || 0;
  const cum = Number(order.cumulativeQuantity) || 0;
  const leaves = Number(order.leavesQuantity) || 0;
  const pct = qty > 0 ? Math.max(0, Math.min(100, (cum / qty) * 100)) : 0;
  const price = order.price == null ? "MKT" : fmtPx(order.price);
  const staleBadge = order.isStale
    ? ` <span class="order-stale-badge" title="${escapeHtml(order.staleReason || "stale")}">stale</span>`
    : "";
  const algoBlock = order.parentAlgoId
    ? `<div class="field"><span class="label">Parent algo</span><span class="value"><code>${escapeHtml(order.parentAlgoId)}</code>${order.algoSliceSeq != null ? ` · slice ${escapeHtml(order.algoSliceSeq)}` : ""}</span></div>`
    : "";
  body.innerHTML = `
    <div class="field"><span class="label">ClOrdID</span><span class="value"><code>${escapeHtml(order.clOrdId)}</code></span></div>
    <div class="field"><span class="label">Symbol</span><span class="value">${escapeHtml(order.symbol)}${order.securityId != null ? ` <span class="muted-line">· ${escapeHtml(order.securityId)}</span>` : ""}</span></div>
    <div class="field"><span class="label">Side</span><span class="value">${escapeHtml(order.side)}</span></div>
    <div class="field"><span class="label">Type</span><span class="value">${escapeHtml(order.type)}</span></div>
    <div class="field"><span class="label">TIF</span><span class="value">${escapeHtml(order.timeInForce ?? "—")}</span></div>
    <div class="field"><span class="label">Status</span><span class="value status-cell-${escapeHtml(order.status)}">${escapeHtml(order.status)}${staleBadge}</span></div>
    <div class="field"><span class="label">Price</span><span class="value">${price}</span></div>
    ${order.stopPrice != null ? `<div class="field"><span class="label">Stop price</span><span class="value">${fmtPx(order.stopPrice)}</span></div>` : ""}
    ${order.goodTillDate ? `<div class="field"><span class="label">Good-till-date</span><span class="value">${escapeHtml(fmtGtd(order.goodTillDate))}</span></div>` : ""}
    <div class="field"><span class="label">VWAP fills</span><span class="value">${vwap == null ? "—" : fmtPx(vwap)}</span></div>
    <div class="field field-wide">
      <span class="label">Qty / Cum / Leaves</span>
      <span class="value">${fmtQty(qty)} · ${fmtQty(cum)} · ${fmtQty(leaves)}</span>
      <div class="modal-progress" role="progressbar" aria-valuenow="${cum}" aria-valuemin="0" aria-valuemax="${qty}">
        <div class="modal-progress-fill" style="width: ${pct}%"></div>
      </div>
    </div>
    ${algoBlock}
  `;
}

function renderOrderDetailExecutions(execs) {
  const tbody = $("order-detail-exec-body");
  if (!tbody) return;
  if (execs.length === 0) {
    tbody.innerHTML = `<tr class="empty"><td colspan="8">No executions for this ClOrdID yet.</td></tr>`;
    return;
  }
  // Newest first.
  const sorted = execs.slice().sort((a, b) => {
    const ta = Date.parse(a.timestampUtc) || 0;
    const tb = Date.parse(b.timestampUtc) || 0;
    return tb - ta;
  });
  tbody.innerHTML = sorted.map(execDetailRow).join("");
}

function execDetailRow(e) {
  const ts = fmtExecTime(e.timestampUtc);
  const hasFill = Number(e.lastQuantity) > 0;
  const lastQty = hasFill ? fmtQty(e.lastQuantity) : "";
  const lastPx = hasFill ? fmtPx(e.lastPrice) : "";
  const stp = stpBadgeFor(e);
  const reason = e.rejectReason ? escapeHtml(e.rejectReason) : "";
  const notes = [reason, stp].filter(Boolean).join(" ");
  return `<tr>
    <td>${ts}</td>
    <td class="kind ${escapeHtml(e.kind)}">${escapeHtml(execKindLabel(e.kind))}</td>
    <td class="num">${lastQty}</td>
    <td class="num">${lastPx}</td>
    <td class="num">${fmtQty(e.cumulativeQuantity)}</td>
    <td class="num">${fmtQty(e.leavesQuantity)}</td>
    <td class="status-cell-${escapeHtml(e.status)}">${escapeHtml(e.status)}</td>
    <td>${notes}</td>
  </tr>`;
}

function onOrderDetailKeydown(e) {
  const modal = $("order-detail-modal");
  if (!modal || modal.hidden) return;
  if (e.key === "Escape") {
    e.stopPropagation();
    e.preventDefault();
    closeOrderDetail();
    return;
  }
  if (e.key !== "Tab") return;
  // Focus trap. Loop the focusable set inside the dialog.
  const dialog = modal.querySelector(".order-detail-card");
  if (!dialog) return;
  const focusables = focusableInDialog(dialog);
  if (focusables.length === 0) {
    e.preventDefault();
    return;
  }
  const first = focusables[0];
  const last = focusables[focusables.length - 1];
  const active = document.activeElement;
  if (!dialog.contains(active)) {
    e.preventDefault();
    first.focus();
    return;
  }
  if (e.shiftKey && active === first) {
    e.preventDefault();
    last.focus();
  } else if (!e.shiftKey && active === last) {
    e.preventDefault();
    first.focus();
  }
}

function submitModifyForm() {
  const form  = $("modify-modal-form");
  const qtyEl = $("modify-modal-qty");
  const pxEl  = $("modify-modal-price");
  if (!form || !qtyEl) return;
  const clOrdId = form.dataset.clordid;
  if (!clOrdId) return;

  const qtyRaw = qtyEl.value.trim();
  const newLeaves = Number(qtyRaw);
  if (!Number.isFinite(newLeaves) || newLeaves <= 0 || !Number.isInteger(newLeaves)) {
    setModifyModalError("Remaining quantity must be a positive integer.");
    return;
  }
  const cumSnapshot = Number(form.dataset.cumqty) || 0;
  const wireQty = computeWireOrderQty(newLeaves, cumSnapshot);
  if (!Number.isFinite(wireQty)) {
    setModifyModalError("Remaining quantity must be a positive integer.");
    return;
  }
  let price = null;
  if (pxEl && !pxEl.disabled) {
    const pxRaw = pxEl.value.trim();
    if (pxRaw !== "") {
      const px = Number(pxRaw);
      if (!Number.isFinite(px) || px <= 0) {
        setModifyModalError("Price must be a positive number.");
        return;
      }
      price = px;
    }
  }
  setModifyModalError(null);
  // Wire-level: OrderCancelReplaceRequest.OrderQty = cum + newLeaves
  // (FIX invariant OrderQty ≥ CumQty); see the "UX-vs-wire" comment
  // block above openModifyModal.
  onModifyOrder(clOrdId, { quantity: wireQty, price });
}

// ── Cancel-all modal (T3) ──────────────────────────────────────────
// Snapshot the working ClOrdID set when the modal opens so concurrent
// fills/cancels don't change the list mid-burst. The trader has to
// type CANCEL to arm the submit; submission is one-shot per modal
// open. While the burst is in flight, the form input is disabled and
// only the Close button is interactive — closing won't abort already-
// dispatched HTTP calls but suppresses further progress UI.
const CANCEL_ALL_MAGIC_WORD = "CANCEL";
let cancelAllInflight = false;

function workingOrderIds() {
  const orders = getState().orders;
  const ids = [];
  for (const o of orders.values()) {
    if (!o || !o.clOrdId) continue;
    if (isTerminalOrderStatus(o.status)) continue;
    if (o.status === "PendingCancel") continue;
    ids.push(o.clOrdId);
  }
  return ids;
}

function openCancelAllModal() {
  const ids = workingOrderIds();
  if (ids.length === 0) return;
  const modal   = $("cancel-all-modal");
  const form    = $("cancel-all-form");
  const summary = $("cancel-all-summary");
  const input   = $("cancel-all-confirm");
  const submit  = $("cancel-all-submit");
  const close   = $("cancel-all-close");
  const progress = $("cancel-all-progress");
  if (!modal || !form || !input || !submit) return;
  cancelAllInflight = false;
  form.dataset.ids = JSON.stringify(ids);
  if (summary) summary.textContent = `${ids.length} working ${ids.length === 1 ? "order" : "orders"} will be cancelled. This cannot be undone.`;
  input.value = ""; input.disabled = false;
  submit.disabled = true; submit.textContent = "Cancel orders";
  if (close) { close.disabled = false; close.textContent = "Close"; }
  if (progress) { progress.hidden = true; progress.textContent = ""; }
  setCancelAllError(null);
  rememberFocusForModal("cancel-all-modal");
  modal.hidden = false;
  // Defer focus so the autofocus doesn't fight the show transition.
  queueMicrotask(() => input.focus());
}

export function closeCancelAllModal() {
  const modal = $("cancel-all-modal");
  const form  = $("cancel-all-form");
  if (!modal) return;
  // Don't let Close yank the modal mid-burst — the burst keeps running
  // but the trader needs to see the final tally to know what to retry.
  if (cancelAllInflight) return;
  modal.hidden = true;
  if (form) delete form.dataset.ids;
  setCancelAllError(null);
  restoreFocusForModal("cancel-all-modal");
}

export function setCancelAllError(message) {
  const el = $("cancel-all-error");
  if (!el) return;
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false; el.textContent = message;
}

export function setCancelAllProgress({ done, failed, total, finished }) {
  const progress = $("cancel-all-progress");
  const submit   = $("cancel-all-submit");
  const close    = $("cancel-all-close");
  if (progress) {
    progress.hidden = false;
    const failTxt = failed > 0 ? ` (${failed} failed)` : "";
    progress.textContent = finished
      ? `Done: ${done}/${total} cancelled${failTxt}.`
      : `${done}/${total} cancelled${failTxt}…`;
  }
  if (finished) {
    cancelAllInflight = false;
    if (submit) { submit.disabled = true; submit.textContent = "Cancel orders"; }
    if (close)  { close.disabled = false; close.textContent = "Done"; }
  }
}

function syncCancelAllSubmitArmed() {
  const input  = $("cancel-all-confirm");
  const submit = $("cancel-all-submit");
  if (!input || !submit) return;
  if (cancelAllInflight) { submit.disabled = true; return; }
  submit.disabled = input.value.trim().toUpperCase() !== CANCEL_ALL_MAGIC_WORD;
}

function submitCancelAllForm() {
  const form   = $("cancel-all-form");
  const input  = $("cancel-all-confirm");
  const submit = $("cancel-all-submit");
  const close  = $("cancel-all-close");
  if (!form || !input) return;
  if (input.value.trim().toUpperCase() !== CANCEL_ALL_MAGIC_WORD) return;
  let ids;
  try { ids = JSON.parse(form.dataset.ids ?? "[]"); }
  catch { ids = []; }
  if (!Array.isArray(ids) || ids.length === 0) {
    setCancelAllError("No working orders to cancel.");
    return;
  }
  cancelAllInflight = true;
  setCancelAllError(null);
  input.disabled = true;
  if (submit) { submit.disabled = true; submit.textContent = "Cancelling…"; }
  if (close)  { close.disabled = true; }
  onCancelAll(ids);
}

function renderCancelAllButton() {
  const btn = $("cancel-all-btn");
  if (!btn) return;
  const ids = workingOrderIds();
  const n = ids.length;
  if (n === 0) {
    btn.hidden = true;
    btn.textContent = "Cancel all";
    btn.disabled = true;
    return;
  }
  btn.hidden = false;
  btn.disabled = false;
  btn.textContent = `Cancel all (${n})`;
}

// ═══════════════════════════════════════════════════════════════════
// FE-OPT-2 (#498). Option chain picker
// ═══════════════════════════════════════════════════════════════════

let chainPickerOnSelect = null; // callback when user clicks a cell

function openChainPicker(onSelect) {
  chainPickerOnSelect = onSelect;
  const modal = $("chain-picker-modal");
  if (modal && typeof modal.showModal === "function") {
   modal.showModal();
  }
}

function closeChainPicker() {
  const modal = $("chain-picker-modal");
  if (modal && typeof modal.close === "function") {
   modal.close();
  }
  chainPickerOnSelect = null;
}

function buildChainGrid(instruments) {
  // Group by expiry (columns) and strike (rows)
  const expiries = [...new Set(instruments.map(i => i.expirationDate))].sort();
  const strikes = [...new Set(instruments.map(i => i.strikePrice))].sort((a, b) => a - b);
  
  // Build lookup: { "strike|expiry|putOrCall" => instrument }
  const lookup = new Map();
  for (const inst of instruments) {
   lookup.set(`${inst.strikePrice}|${inst.expirationDate}|${inst.putOrCall}`, inst);
  }
  
  // Build table HTML
  let html = '<table class="chain-table"><thead><tr><th class="strike-col">Strike</th>';
  for (const exp of expiries) {
   // Show both C and P columns per expiry
   html += `<th colspan="2">${exp}</th>`;
  }
  html += '</tr><tr><th></th>';
  for (const exp of expiries) {
   html += '<th>Call</th><th>Put</th>';
  }
  html += '</tr></thead><tbody>';
  
  for (const strike of strikes) {
   html += `<tr><td class="strike-col">${strike.toFixed(2)}</td>`;
   for (const exp of expiries) {
     const call = lookup.get(`${strike}|${exp}|Call`);
     const put = lookup.get(`${strike}|${exp}|Put`);
     html += call 
       ? `<td class="chain-cell chain-cell-call" data-symbol="${call.symbol}" data-security-id="${call.securityId}">C</td>`
       : '<td class="chain-cell-empty">—</td>';
     html += put
       ? `<td class="chain-cell chain-cell-put" data-symbol="${put.symbol}" data-security-id="${put.securityId}">P</td>`
       : '<td class="chain-cell-empty">—</td>';
   }
   html += '</tr>';
  }
  html += '</tbody></table>';
  return html;
}

function handleChainCellClick(e) {
  const cell = e.target.closest(".chain-cell");
  if (!cell) return;
  const symbol = cell.dataset.symbol;
  const securityId = cell.dataset.securityId;
  if (symbol && chainPickerOnSelect) {
   chainPickerOnSelect(symbol, securityId);
   closeChainPicker();
  }
}

export function bindUi() {
  // Order ticket: enable/disable price field + show/hide stop-price +
  // good-till-date inputs based on type / TIF (Q1.4 #256).
  const typeEl = $("ticket-type");
  const priceEl = $("ticket-price");
  const priceLabel = $("ticket-price-label");
  const stopPriceEl = $("ticket-stop-price");
  const stopPriceLabel = $("ticket-stop-price-label");
  const gtdEl = $("ticket-good-till-date");
  const gtdLabel = $("ticket-good-till-date-label");
  const sideEl = $("ticket-side");
  const qtyEl = $("ticket-qty");

  // Visibility rules:
  //   • Price: shown for Limit / StopLimit / MarketWithLeftover.
  //   • StopPrice: shown for StopLoss / StopLimit.
  //   • GoodTillDate: shown for TIF == GTD.
  // Conditional fields use the `hidden` attribute (not display:none) so
  // screen readers also skip them, mirroring the rest of the app.
  function syncTicketConditionals() {
    applyTicketConditionalVisibility({
      type: typeEl.value,
      tif:  $("ticket-tif")?.value ?? "Day",
      priceEl, priceLabel,
      stopPriceEl, stopPriceLabel,
      gtdEl, gtdLabel,
    });
    refreshTicketValidation();
  }
  typeEl.addEventListener("change", syncTicketConditionals);
  syncTicketConditionals();

  // Re-validate on input for live feedback. Validation feeds the
  // submit-disabled OR via dataset.validationFailed (see applySubmitDisabled).
  for (const el of [priceEl, stopPriceEl, gtdEl, qtyEl, sideEl, typeEl].filter(Boolean)) {
    el.addEventListener("input",  refreshTicketValidation);
    el.addEventListener("change", refreshTicketValidation);
  }

  // Update notional preview when qty, price, or symbol changes.
  // This provides live feedback of the estimated notional value accounting
  // for option contract multipliers.
  const symEl = $("ticket-symbol");
  if (symEl) symEl.addEventListener("change", updateNotionalPreview);
  if (qtyEl) qtyEl.addEventListener("input", updateNotionalPreview);
  if (priceEl) priceEl.addEventListener("input", updateNotionalPreview);

  $("ticket-form").addEventListener("submit", (e) => {
    e.preventDefault();
    // Q1.4 (#256). Re-validate against the live policy snapshot just
    // before submit. The submit-disabled state is driven by the last
    // input/change event + the riskPolicy slice notify, but a policy
    // update that lands between the trader's last keystroke and the
    // click could otherwise (a) silently block a now-valid GTD by
    // leaving validationFailed set, or (b) let a now-over-cap GTD
    // through. Running validation here closes that window.
    const liveResult = refreshTicketValidation();
    if (liveResult && liveResult.valid === false) return;
    const tifEl = $("ticket-tif");
    const type  = typeEl.value;
    const tif   = tifEl ? tifEl.value : "Day";
    const stopHidden = !isStopOrderType(type);
    const gtdHidden  = !isGtdTif(tif);
    const payload = {
      symbol: $("ticket-symbol").value.trim().toUpperCase(),
      side:   sideEl.value,
      type,
      quantity: Number(qtyEl.value),
      price: priceEl.disabled || priceEl.value === "" ? null : Number(priceEl.value),
      // Q1.6 (#258). Backend defaults to Day when omitted, but we
      // always pass the visible value so what the trader sees on the
      // ticket is exactly what hits the venue.
      timeInForce: tif,
      // Q1.4 (#256). Hidden conditional inputs are submitted as null
      // so the server canonicalises; a populated input that becomes
      // hidden is also nulled here (syncTicketConditionals clears the
      // value first, but we belt-and-braces here).
      stopPrice:    stopHidden || !stopPriceEl || stopPriceEl.value === "" ? null : Number(stopPriceEl.value),
      goodTillDate: gtdHidden  || !gtdEl       || gtdEl.value       === "" ? null : new Date(gtdEl.value).toISOString(),
    };
    // Q3.4 (#284). Native iceberg / reserve display-qty. An empty
    // Display qty input means full disclosure (no reserve) — we
    // omit both display fields from the payload so the backend
    // defaults kick in (null = no reserve). When the trader fills
    // it in, send the policy too; the backend defaults to Always
    // when the policy is omitted on a display-qty submit.
    const displayQtyEl = $("ticket-display-qty");
    const displayPolicyEl = $("ticket-display-reset-policy");
    if (displayQtyEl && displayQtyEl.value !== "") {
      payload.displayQty = Number(displayQtyEl.value);
      payload.displayResetPolicy = displayPolicyEl ? displayPolicyEl.value : "Always";
    }
    onSubmitOrder(payload);
  });

  // Q1.6 (#258). Track the trader's manual TIF picks so the auction
  // auto-pick (renderTicketPhaseCoupling) doesn't trample them.
  const tifEl = $("ticket-tif");
  if (tifEl) {
    tifEl.addEventListener("change", () => {
      tifEl.dataset.userPicked = "1";
      delete tifEl.dataset.autoPicked;
      // Q1.4 (#256). TIF change toggles the GTD input visibility.
      syncTicketConditionals();
      renderTicketPhaseCoupling();
    });
  }

  // Auto-uppercase the symbol field as the trader types so the visual
  // matches what we actually submit (we already toUpperCase on submit).
  if (symEl) {
    symEl.addEventListener("input", (e) => {
      // Don't fight IME composition; uppercase on compositionend instead.
      if (e.isComposing) return;
      const pos = e.target.selectionStart;
      const upper = e.target.value.toUpperCase();
      if (e.target.value !== upper) {
        e.target.value = upper;
        try { e.target.setSelectionRange(pos, pos); } catch {}
      }
      // Q1.6 (#258). Symbol changes reset the user's TIF affinity so a
      // new ticket starts from defaults — otherwise a manual pick on
      // ticket A locks the auto-pick on ticket B for a different symbol.
      const tifEl2 = $("ticket-tif");
      if (tifEl2) { delete tifEl2.dataset.userPicked; delete tifEl2.dataset.autoPicked; }
      syncTicketRules();
      renderTicketPhaseCoupling();
    });
    symEl.addEventListener("compositionend", (e) => {
      const upper = e.target.value.toUpperCase();
      if (e.target.value !== upper) e.target.value = upper;
      syncTicketRules();
      renderTicketPhaseCoupling();
    });
    symEl.addEventListener("change", () => { syncTicketRules(); renderTicketPhaseCoupling(); });
    // Initial paint so the hint reflects defaults before any input.
    syncTicketRules();
  }

  $("logout").addEventListener("click", () => onLogout());

  // Event delegation for per-row Cancel + Modify buttons in the
  // blotter, plus row selection (clicking anywhere outside the
  // action buttons) and the order-detail drill-down (#245).
  $("blotter-body").addEventListener("click", (e) => {
    const cancelBtn = e.target.closest(".cancel-btn");
    if (cancelBtn) {
      const clOrdId = cancelBtn.dataset.clordid;
      if (clOrdId) onCancelOrder(clOrdId);
      return;
    }
    const modifyBtn = e.target.closest(".modify-btn");
    if (modifyBtn) {
      const clOrdId = modifyBtn.dataset.clordid;
      if (clOrdId) openModifyModal(clOrdId);
      return;
    }
    const row = e.target.closest("tr[data-clordid]");
    if (!row) return;
    onSelectOrder(row.dataset.clordid);
    // Order-detail modal opens for any non-button cell click. Skip
    // links / form controls just in case future renders embed any.
    if (e.target.closest("button, a, input, select, textarea")) return;
    openOrderDetail(row.dataset.clordid, row);
  });

  // Order-detail modal wiring (#245). Backdrop click and × button both
  // dismiss; Esc + focus trap handled by the keydown listener installed
  // when the modal opens.
  const orderDetailModal = $("order-detail-modal");
  if (orderDetailModal) {
    orderDetailModal.addEventListener("click", (e) => {
      if (e.target === orderDetailModal) closeOrderDetail();
    });
  }
  const orderDetailCloseBtn = $("order-detail-close");
  if (orderDetailCloseBtn) {
    orderDetailCloseBtn.addEventListener("click", () => closeOrderDetail());
  }

  // Modify modal wiring (slice 5 of #122).
  const modifyForm = $("modify-modal-form");
  const modifyCancelBtn = $("modify-modal-cancel");
  if (modifyForm) {
    modifyForm.addEventListener("submit", (e) => {
      e.preventDefault();
      submitModifyForm();
    });
  }
  // Live wire-OrderQty hint: refresh on every qty edit so the trader
  // always sees the FIX OrderQty = cum + remaining that will hit the
  // wire (UX-vs-wire contract above openModifyModal).
  const modifyQtyInput = $("modify-modal-qty");
  if (modifyQtyInput) {
    modifyQtyInput.addEventListener("input", () => refreshModifyWireHint());
  }
  if (modifyCancelBtn) {
    modifyCancelBtn.addEventListener("click", () => closeModifyModal());
  }
  const modifyModal = $("modify-modal");
  if (modifyModal) {
    // Backdrop click closes the modal — same UX precedent as the
    // session-expiry modal but with closeModifyModal() (no logout
    // side-effect). Esc handled in the global keydown below.
    modifyModal.addEventListener("click", (e) => {
      if (e.target === modifyModal) closeModifyModal();
    });
  }

  // Cancel-all wiring (T3). Button lives in the blotter header and
  // appears whenever there are working orders to cancel.
  const cancelAllBtn   = $("cancel-all-btn");
  const cancelAllForm  = $("cancel-all-form");
  const cancelAllInput = $("cancel-all-confirm");
  const cancelAllClose = $("cancel-all-close");
  const cancelAllModal = $("cancel-all-modal");
  if (cancelAllBtn) cancelAllBtn.addEventListener("click", () => openCancelAllModal());
  if (cancelAllForm) {
    cancelAllForm.addEventListener("submit", (e) => {
      e.preventDefault();
      submitCancelAllForm();
    });
  }
  if (cancelAllInput) cancelAllInput.addEventListener("input", syncCancelAllSubmitArmed);
  if (cancelAllClose) cancelAllClose.addEventListener("click", () => closeCancelAllModal());
  if (cancelAllModal) {
    cancelAllModal.addEventListener("click", (e) => {
      if (e.target === cancelAllModal) closeCancelAllModal();
    });
  }

  // Blotter filter: text + status select + working-only toggle (#342).
  // Persisted via app.js.
  const filterText = $("blotter-filter-text");
  const filterStatus = $("blotter-filter-status");
  const filterHideTerm = $("blotter-hide-terminal");
  const fireFilter = () => onBlotterFilter({
    text:   filterText.value,
    status: filterStatus.value,
    hideTerminal: filterHideTerm ? filterHideTerm.checked : true,
  });
  if (filterText)     filterText.addEventListener("input",  fireFilter);
  if (filterStatus)   filterStatus.addEventListener("change", fireFilter);
  if (filterHideTerm) filterHideTerm.addEventListener("change", fireFilter);

  // #342: Positions click-to-sort. Click cycles asc → desc → default
  // (|net| desc). Keyboard activation mirrors mouse for accessibility
  // since the headers are role="button".
  _positionsSort = readPositionsSort();
  _positionsGrouped = readPositionsGrouped();
  document.querySelectorAll(".panel.positions th.sortable").forEach(th => {
    const cycle = () => {
      const key = th.getAttribute("data-sort-key");
      if (_positionsSort.col === key) {
        _positionsSort = _positionsSort.dir === "desc"
          ? { col: key, dir: "asc" }
          : { ...POSITIONS_SORT_DEFAULT };
      } else {
        _positionsSort = { col: key, dir: "desc" };
      }
      writePositionsSort(_positionsSort);
      renderPositions();
    };
    th.addEventListener("click", cycle);
    th.addEventListener("keydown", (e) => {
      if (e.key === "Enter" || e.key === " ") { e.preventDefault(); cycle(); }
    });
  });

  // FE-OPT-3 (#499). Positions group-by-underlying toggle.
  const posGroupToggle = $("positions-group-toggle");
  if (posGroupToggle) {
    posGroupToggle.addEventListener("click", () => {
      _positionsGrouped = !_positionsGrouped;
      writePositionsGrouped(_positionsGrouped);
      renderPositions();
    });
  }

  // FE-OPT-3 (#499). Expiry strip click handler (event delegation).
  const expiryStrip = $("expiry-strip-items");
  if (expiryStrip) {
    expiryStrip.addEventListener("click", (e) => {
      const chip = e.target.closest(".expiry-chip");
      if (!chip) return;
      const exp = chip.dataset.expiry;
      _expiryFilter = exp || null; // empty string = "All" = null
      renderPositions();
    });
  }

  // #342: Executions log symbol filter. Substring match, case-insensitive,
  // persisted per tab so the trader's last filter survives a page reload.
  const execFilter = $("exec-filter-symbol");
  if (execFilter) {
    _execSymbolFilter = readExecSymbolFilter();
    execFilter.value = _execSymbolFilter;
    execFilter.addEventListener("input", () => {
      _execSymbolFilter = execFilter.value;
      writeExecSymbolFilter(_execSymbolFilter);
      renderExecutions();
    });
  }

  // Blotter pagination controls. Renderer hides the bar when there's
  // only one page; clicks here only request a page change — clamping
  // and bounds-checking happen in the state setter / renderer.
  const prevBtn = $("blotter-prev");
  const nextBtn = $("blotter-next");
  if (prevBtn) prevBtn.addEventListener("click", () => onBlotterPage(-1));
  if (nextBtn) nextBtn.addEventListener("click", () => onBlotterPage(+1));

  // Market data form: apply WS URL + watchlist atomically.
  $("md-form").addEventListener("submit", (e) => {
    e.preventDefault();
    const url = $("md-url").value.trim();
    const symbols = $("md-symbols").value
      .split(/[,\s]+/)
      .map(s => s.trim().toUpperCase())
      .filter(Boolean);
    onApplyMd({ url, symbols });
  });

  // Market data settings now live inline as a sub-tab of Settings
  // (Fase 3 / #399). The ⚙ trader-panel popover was removed; the form
  // submit handler (md-form above) is the only md-* wiring that
  // remains in this view.

  // #71: Volume heatmap toggle (🔥 button in MD panel header). Opt-in
  // per the decision gate; persisted per tab via sessionStorage.
  const heatmapBtn = $("heatmap-toggle");
  if (heatmapBtn) {
    setHeatmapEnabled(readHeatmapEnabled());
    heatmapBtn.addEventListener("click", () => setHeatmapEnabled(!_heatmapEnabled));
  }

  // Global symbol selector (drives DOB / chart / tape).
  const symSelect = $("selected-symbol");
  if (symSelect) {
    symSelect.addEventListener("change", (e) => {
      onSelectSymbol(e.target.value || null);
    });
  }

  // Chart resolution selector (T3).
  const chartRes = $("chart-resolution");
  if (chartRes) {
    chartRes.addEventListener("change", (e) => {
      onSelectChartResolution(Number(e.target.value));
    });
  }

  // Tape (T4): "All symbols" toggle + pause-on-hover.
  const tapeAll = $("tape-show-all");
  if (tapeAll) {
    tapeAll.addEventListener("change", (e) => {
      onToggleTapeShowAll(e.target.checked);
    });
  }
  const tapeList = $("tape-list");
  if (tapeList) {
    tapeList.addEventListener("mouseenter", () => {
      tapePaused = true;
      const badge = $("tape-paused");
      if (badge) badge.hidden = false;
    });
    tapeList.addEventListener("mouseleave", () => {
      tapePaused = false;
      const badge = $("tape-paused");
      if (badge) badge.hidden = true;
      // Flush any updates that arrived while hovered.
      if (tapeDirty) scheduleTapeRender();
    });
  }

  // View toggle (trader / admin) — only wired here, visibility is
  // gated in app.js based on the JWT role claim.
  const toggle = $("view-toggle");
  if (toggle) {
    toggle.addEventListener("click", (e) => {
      const btn = e.target.closest("button[data-view]");
      if (!btn) return;
      onSwitchView(btn.dataset.view);
    });
  }

  // #408. Mobile hamburger drawer mirrors the inline tablist. CSS hides
  // the trigger / drawer on >=768px viewports so desktop is byte-
  // identical to before.
  const drawerTrigger = $("mobile-nav-trigger");
  const drawerEl      = $("mobile-nav-drawer");
  const drawerList    = $("mobile-nav-list");
  const drawerBackdrop = $("mobile-nav-backdrop");
  if (drawerTrigger && drawerEl && drawerList) {
    _mobileDrawer = bindMobileDrawer({
      trigger: drawerTrigger,
      drawer: drawerEl,
      list: drawerList,
      backdrop: drawerBackdrop,
      onSelect: (view) => onSwitchView(view),
    });
    if (toggle) _mobileDrawer.syncFromTablist(toggle);
  }

  // Global keyboard shortcuts:
  //   F2  → focus the order-ticket symbol input
  //   Esc → clear the ticket form (when focus is inside it)
  //   Del → cancel the currently-selected blotter row
  // We deliberately ignore key events when the user is typing into a
  // text input to avoid stealing keystrokes. Backspace is intentionally
  // NOT a cancel shortcut — too easy to fire accidentally.
  document.addEventListener("keydown", onGlobalKeydown);

  // Q1.6 (#258). Auction-panel manual toggle. The panel auto-opens
  // when the selected symbol enters an auction phase, but the trader
  // can also collapse it manually — clicking the header collapses to
  // the symbol-only state without unsubscribing (re-open replays the
  // current state).
  const auctionToggle = $("auction-toggle");
  if (auctionToggle) {
    auctionToggle.addEventListener("click", () => {
      const st = getState();
      if (st.auctionPanelSymbol) {
        setAuctionPanelSymbol(null);
      } else if (st.selectedSymbol) {
        setAuctionPanelSymbol(st.selectedSymbol);
      }
    });
  }

  // ═══════════════════════════════════════════════════════════════════
  // FE-OPT-2 (#498). Option chain picker modal wiring
  // ═══════════════════════════════════════════════════════════════════
  const chainModal = $("chain-picker-modal");
  const chainGrid = $("chain-picker-grid");
  const chainUnderlyingInput = $("chain-underlying");
  const chainLoadBtn = $("chain-load-btn");
  const openChainBtn = $("open-chain-picker");
  
  if (chainModal) {
    // Close on backdrop click (click on the dialog but not its content)
    chainModal.addEventListener("click", (e) => {
      if (e.target === chainModal) closeChainPicker();
    });
    // Close on X button (class or data attr varies, common patterns)
    chainModal.querySelector(".modal-close")?.addEventListener("click", closeChainPicker);
    chainModal.querySelector("[data-dismiss='modal']")?.addEventListener("click", closeChainPicker);
    // Cell clicks inside the grid
    if (chainGrid) {
      chainGrid.addEventListener("click", handleChainCellClick);
    }
  }
  
  if (openChainBtn) {
    openChainBtn.addEventListener("click", () => {
      openChainPicker((symbol, securityId) => {
        // Populate ticket with selected option
        const symInput = $("ticket-symbol");
        if (symInput) {
          symInput.value = symbol;
          symInput.dispatchEvent(new Event("change", { bubbles: true }));
        }
      });
    });
  }

  // Ctrl+O opens chain picker (Mac: Cmd+O)
  document.addEventListener("keydown", (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key === "o") {
      e.preventDefault();
      openChainBtn?.click();
    }
  });

  subscribe(renderForSlice);
  renderAll();
}

// #342: Esc inside the ticket arms a brief "press Esc again" window
// before wiping the form so a reflex Esc can't blow away a typed-in
// order. State lives at module scope (single global keydown handler).
const TICKET_CLEAR_ARM_MS = 1500;
let ticketClearArmedUntil = 0;

function ticketHasContent() {
  const ids = ["ticket-symbol", "ticket-qty", "ticket-price", "ticket-stop-price", "ticket-good-till-date", "ticket-display-qty"];
  return ids.some(id => {
    const el = $(id);
    return el && typeof el.value === "string" && el.value.trim() !== "";
  });
}

function updateNotionalPreview() {
  const preview = $("ticket-notional-preview");
  if (!preview) return;
  
  const qty = parseFloat($("ticket-qty")?.value) || 0;
  const price = parseFloat($("ticket-price")?.value) || 0;
  const symbol = $("ticket-symbol")?.value?.trim().toUpperCase();
  
  // Look up multiplier from state (positions or a symbol cache)
  // For now, default to 1 (equity) unless we can detect it's an option
  let multiplier = 1;
  
  // If we have a symbol, try to find it in positions to check if it's an option
  if (symbol && getState().positions.has(symbol)) {
    const position = getState().positions.get(symbol);
    // optionContractMultiplier is present if it's an option (securityType === "Option")
    if (position.optionContractMultiplier) {
      multiplier = position.optionContractMultiplier;
    }
  }
  
  const notional = qty * price * multiplier;
  
  // Format with Brazilian locale (1000,00 format) but since we want R$ 1,000.00 format
  // Let's use Intl for proper formatting
  if (notional > 0) {
    const formatted = new Intl.NumberFormat("pt-BR", {
      style: "currency",
      currency: "BRL",
      minimumFractionDigits: 2,
      maximumFractionDigits: 2
    }).format(notional);
    preview.textContent = `≈ ${formatted}`;
  } else {
    preview.textContent = "";
  }
}

function onGlobalKeydown(e) {
  if (getState().currentView !== "trader") return;
  const target = e.target;
  const inEditable = target && (
    target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.tagName === "SELECT" ||
    target.isContentEditable
  );

  if (e.key === "F2" && !e.ctrlKey && !e.metaKey) {
    e.preventDefault();
    const sym = $("ticket-symbol");
    if (sym) sym.focus();
    return;
  }
  if (e.key === "Escape") {
    // Esc closes the cancel-all modal first (most disruptive, highest
    // priority dismiss), then the modify modal; inside the ticket
    // form clears it; outside, clear blotter selection.
    const cancelAllModal = $("cancel-all-modal");
    if (cancelAllModal && !cancelAllModal.hidden) {
      closeCancelAllModal();
      return;
    }
    const modifyModal = $("modify-modal");
    if (modifyModal && !modifyModal.hidden) {
      closeModifyModal();
      return;
    }
    if (target?.closest && target.closest("#ticket-form")) {
      // #342: Esc inside the ticket form used to wipe everything in one
      // keystroke, which made a reflex Esc (to dismiss a datalist /
      // popup) destroy a typed-in ticket. Now: if any of the
      // user-visible fields hold content, the first Esc arms a brief
      // "press Esc again to clear" feedback; a second Esc within the
      // window completes the clear. An empty ticket still clears in
      // one keystroke (no risk).
      if (ticketHasContent()) {
        if (ticketClearArmedUntil > Date.now()) {
          ticketClearArmedUntil = 0;
          clearTicket();
          setTicketFeedback("ticket cleared", "warn");
        } else {
          ticketClearArmedUntil = Date.now() + TICKET_CLEAR_ARM_MS;
          setTicketFeedback("press Esc again to clear ticket", "warn");
        }
      } else {
        clearTicket();
      }
    } else {
      onSelectOrder(null);
    }
    return;
  }
  if (e.key === "Delete" && !inEditable) {
    if (!getState().selectedClOrdId) return;
    e.preventDefault();
    onKeyboardCancel();
  }
}

export function setMdInputs({ url, symbols }) {
  if (typeof url === "string") $("md-url").value = url;
  if (Array.isArray(symbols)) $("md-symbols").value = symbols.join(",");
}

export function setMdFeedback(message, kind) {
  const el = $("md-feedback");
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false;
  el.textContent = message;
  el.className = `feedback ${kind === "ok" ? "ok" : "error"}`;
}

export function setTicketFeedback(message, kind) {
  const el = $("ticket-feedback");
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false;
  el.textContent = message;
  const cls = kind === "ok" ? "ok" : kind === "warn" ? "warn" : "error";
  el.className = `feedback ${cls}`;
}

// #342: Transient WS error toast. Surfaces frame-level errors from the
// data WebSocket (unknown_channel, malformed subscribe, server-side
// close) so the trader notices a quietly-failing subscription without
// having to open devtools. Auto-dismisses after WS_ERROR_TOAST_MS;
// successive errors restart the timer (most recent message wins).
const WS_ERROR_TOAST_MS = 6_000;
let _wsErrorToastTimer = null;
export function showWsErrorToast(message) {
  const el = $("ws-error-toast");
  if (!el) return;
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false;
  el.textContent = message;
  if (_wsErrorToastTimer) clearTimeout(_wsErrorToastTimer);
  _wsErrorToastTimer = setTimeout(() => {
    _wsErrorToastTimer = null;
    el.hidden = true;
    el.textContent = "";
  }, WS_ERROR_TOAST_MS);
}

// #421: Transient toast for order-submit feedback. Replaces the
// "accepted: …" text that used to sit under the ticket form (easy to
// miss when the trader's eyes were on the blotter). `kind` is one of
// "ok" (default — green), "warn" (yellow), "error" (red). Auto-dismisses
// after ORDER_TOAST_MS; successive calls restart the timer. Pass
// `null` to hide immediately.
const ORDER_TOAST_MS = 5_000;
let _orderToastTimer = null;
export function showOrderToast(message, kind) {
  const el = $("order-toast");
  if (!el) return;
  if (!message) {
    if (_orderToastTimer) { clearTimeout(_orderToastTimer); _orderToastTimer = null; }
    el.hidden = true;
    el.textContent = "";
    el.className = "order-toast";
    return;
  }
  el.hidden = false;
  el.textContent = message;
  const cls = kind === "warn" ? "warn" : kind === "error" ? "error" : "";
  el.className = cls ? `order-toast ${cls}` : "order-toast";
  if (_orderToastTimer) clearTimeout(_orderToastTimer);
  _orderToastTimer = setTimeout(() => {
    _orderToastTimer = null;
    el.hidden = true;
    el.textContent = "";
    el.className = "order-toast";
  }, ORDER_TOAST_MS);
}

// Submit button disabled-state is the OR of two independent conditions
// tracked on dataset flags so two writers (the in-flight submit path and
// the phase-coupling halt path) don't clobber each other's intent.
// Always go through applySubmitDisabled() — never write submit.disabled
// directly.
function applySubmitDisabled() {
  const el = $("ticket-submit");
  if (!el) return;
  const inflight        = el.dataset.submitInflight   === "1";
  const halted          = el.dataset.haltDisabled     === "1";
  // Q1.4 (#256). Client-side validation gates Submit alongside the
  // existing in-flight + halt flags. Server remains authority — this
  // is purely UX so the trader doesn't have to round-trip on errors
  // we already know about (Stop without StopPrice, GTD in the past,
  // IOC/FOK + MarketWithLeftover, etc.).
  const validationFailed = el.dataset.validationFailed === "1";
  const disabled = inflight || halted || validationFailed;
  el.disabled = disabled;
  if (disabled) {
    el.setAttribute("aria-disabled", "true");
  } else {
    el.removeAttribute("aria-disabled");
  }
}

export function setTicketSubmitting(submitting) {
  const el = $("ticket-submit");
  if (submitting) {
    el.dataset.submitInflight = "1";
  } else {
    delete el.dataset.submitInflight;
  }
  el.textContent = submitting ? "Submitting…" : "Submit";
  applySubmitDisabled();
}

export function clearTicket() {
  $("ticket-symbol").value = "";
  $("ticket-qty").value = "";
  $("ticket-price").value = "";
  // Q1.4 (#256). Reset the conditional inputs too so a subsequent
  // ticket starts clean.
  const sp = $("ticket-stop-price");  if (sp) sp.value = "";
  const gtd = $("ticket-good-till-date"); if (gtd) gtd.value = "";
  // Q3.4 (#284). Reset the iceberg display-qty input; leave the
  // reset-policy select on its default ("Always") since it's
  // inert when display qty is empty.
  const dq = $("ticket-display-qty"); if (dq) dq.value = "";
  const dp = $("ticket-display-reset-policy"); if (dp) dp.value = "Always";
  syncTicketRules();
  refreshTicketValidation();
}

// T4 — reflect the per-symbol lot/tick on the qty/price inputs and
// in the hint line. The qty input's step+min match the lot size so
// browser arrow-up/down increments by the right amount and the
// HTML5 validation message matches what validateOrder() will say
// at submit-time. Hint format is intentionally short ("lot 100 ·
// tick 0.01") so it doesn't crowd the small ticket panel.
function syncTicketRules() {
  const symEl = $("ticket-symbol");
  const qtyEl = $("ticket-qty");
  const pxEl  = $("ticket-price");
  const hint  = $("ticket-rules-hint");
  const sym = (symEl?.value ?? "").trim().toUpperCase();
  const r = rulesFor(sym);
  if (qtyEl) {
    qtyEl.step = String(r.lotSize);
    qtyEl.min  = String(r.lotSize);
  }
  if (pxEl) {
    pxEl.step = String(r.tickSize);
  }
  if (hint) {
    hint.textContent = `lot ${r.lotSize} · tick ${r.tickSize}`;
  }
}

export function setStatusPill(status) {
  const el = $("ws-status");
  el.textContent = status;
  el.className = `status-pill status-${status}`;
  el.setAttribute("aria-label", `WebSocket: ${status}`);
}

export function setUserLabel(user) {
  const el = $("user-label");
  if (el) {
    if (user?.username) {
      el.textContent = user.firm ? `${user.username} @ ${user.firm}` : user.username;
    } else {
      el.textContent = "";
    }
  }
  const roleEl = $("user-role");
  if (roleEl) {
    if (user?.role && user.role !== "user") {
      roleEl.textContent = user.role;
      roleEl.hidden = false;
    } else {
      roleEl.textContent = "";
      roleEl.hidden = true;
    }
  }
  // Fase 1 (#397). The tablist is visible whenever a user is signed
  // in; per-button gating is computed inside setViewToggleVisible.
  const loggedIn = !!user?.username;
  setViewToggleVisible(loggedIn, getState().currentView);
}

// #385. Render the live cash balance in the topbar. The widget shows
// "R$ —" until the first `balance.me` frame lands (or when the user
// logs out / WS reconnects clears the slice). Negative balances are
// rendered with a `balance-negative` class so the trader notices they
// are underwater. Format uses pt-BR thousands/decimal separators to
// match the rest of the trader UI (price ticket, P&L panel).
const BALANCE_FORMATTER = new Intl.NumberFormat("pt-BR", {
  style: "currency",
  currency: "BRL",
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

function renderBalance() {
  const el = $("user-balance");
  if (!el) return;
  const st = getState();
  if (!st.user?.username) {
    // Logged out — hide the badge entirely so the topbar collapses
    // back to the login layout.
    el.hidden = true;
    el.textContent = "";
    el.classList.remove("balance-negative");
    return;
  }
  el.hidden = false;
  const bal = st.balance;
  if (bal == null || !Number.isFinite(bal.available)) {
    el.textContent = "R$ —";
    el.classList.remove("balance-negative");
    el.title = "Available balance — awaiting data";
    return;
  }
  el.textContent = BALANCE_FORMATTER.format(bal.available);
  el.classList.toggle("balance-negative", bal.available < 0);
  el.title = `Available balance: ${BALANCE_FORMATTER.format(bal.available)}`;
}

function applyCurrentView(view) {
  const trader = $("trader-view");
  const admin = $("admin-view");
  const history = $("history-view");
  const compliance = $("compliance-view");
  const settings = $("settings-view");
  const algos = $("algos-view");
  if (!trader || !admin) return;
  const showTraderView = view === "trader";
  const showAdminView = view === "admin";
  const showHistoryView = view === "history";
  const showComplianceView = view === "compliance";
  const showSettingsView = view === "settings";
  const showAlgosView = view === "algos";
  trader.hidden = !showTraderView;
  admin.hidden = !showAdminView;
  if (history)     history.hidden     = !showHistoryView;
  if (compliance)  compliance.hidden  = !showComplianceView;
  if (settings)    settings.hidden    = !showSettingsView;
  if (algos)       algos.hidden       = !showAlgosView;
  // Fase 1 (#397): the primary tablist now persists across every view.
  // `setViewToggleVisible(true, …)` runs whenever a user is signed in;
  // the logged-out path (showLogin) is the only caller that passes
  // `false`. Fase 3 (#399) folded the former `bot-credentials` view
  // into Settings, so no special-case highlight remap is needed.
  setViewToggleVisible(true, view);
  // Trader-specific topbar controls (symbol selector) are only
  // meaningful while the trading view is mounted.
  const symbolWrap = $("selected-symbol")?.closest("label.symbol-select");
  if (symbolWrap) symbolWrap.hidden = !showTraderView;
}

// Periodic UI tick for time-based elements (in-flight elapsed, reconnect
// countdown, market-data staleness, DOB "no book" promotion). Started
// lazily on first render so SSR / non-browser hosts stay quiet.
let tickTimer = null;
let lastSlowTick = 0;
function ensureTicker() {
  if (tickTimer) return;
  tickTimer = setInterval(() => {
    renderInflight();
    // Slow tick (1s) for time-driven re-renders that don't need 4 Hz.
    const now = Date.now();
    if (now - lastSlowTick >= 1000) {
      lastSlowTick = now;
      // #342: reconnect countdown ticks at 1Hz (integer seconds) — the
      // previous 4Hz / decisecond display flickered without conveying
      // useful information. State changes still trigger an immediate
      // re-render via the wsReconnect subscription.
      renderReconnect();
      // #71: heatmap intensity decays as the rolling window rolls,
      // even with no new trades. A periodic re-render keeps the
      // cells fading instead of latching on the last trade.
      if (_heatmapEnabled) renderHeatmap();
      renderMarketData();
      // Only re-render the DOB while we're still waiting for a snapshot
      // — the live render path is already wired to the book slice.
      const st = getState();
      const entry = st.selectedSymbol ? st.book.get(st.selectedSymbol) : null;
      if (st.selectedSymbol && (!entry || !entry.ready)) renderDob();
      // Same idea for the chart: tick re-render while waiting so the
      // copy can flip from "awaiting…" to "no candle snapshot received"
      // once the timeout passes (issue #91). Once a snapshot has arrived
      // (ready=true), the live render path covers updates — only keep
      // ticking if we're still waiting for the snapshot itself.
      const cperRes = st.selectedSymbol ? st.candles.get(st.selectedSymbol) : null;
      const centry  = cperRes?.get(st.chartResolution);
      if (st.selectedSymbol && (!centry || !centry.ready)) {
        scheduleChartRender();
      }
    }
  }, 250);
}

function renderInflight() {
  const el = $("ticket-inflight");
  if (!el) return;
  const inflight = getState().submitInflight;
  if (!inflight) { el.hidden = true; el.textContent = ""; return; }
  const elapsed = Math.max(0, Date.now() - inflight.startedAt);
  el.hidden = false;
  el.textContent = `awaiting ACK… ${elapsed} ms`;
}

function renderReconnect() {
  const el = $("ws-reconnect");
  if (!el) return;
  const r = getState().wsReconnect;
  if (!r || !r.nextAt) { el.hidden = true; el.textContent = ""; return; }
  // #342: integer-second precision is enough at 1Hz, and prevents the
  // jitter the old `.1s` format showed every tick even when nothing
  // had changed at the second granularity.
  const remaining = Math.max(0, r.nextAt - Date.now());
  el.hidden = false;
  el.textContent = `retry in ${Math.ceil(remaining / 1000)}s`;
}

function renderFirmsHealth() {
  const el = $("firms-health");
  if (!el) return;
  const fh = getState().firmsHealth;
  if (!fh || !Array.isArray(fh.firms) || fh.firms.length === 0) {
    el.hidden = true;
    el.textContent = "";
    el.title = "";
    return;
  }
  // Pick the worst-state firm to drive the badge colour, but show the
  // full list in the tooltip so an admin sees per-firm state at a glance.
  const ranked = fh.firms.map(f => ({
    firmId: f.firmId,
    state: f.sessionState ?? "unknown",
    reconnecting: !!f.reconnecting,
  }));
  const anyReconnecting = ranked.some(r => r.reconnecting);
  const allEstablished = ranked.every(r => r.state === "Established");
  const tone = anyReconnecting ? "warn" : (allEstablished ? "ok" : "muted");
  const summary = `${ranked.length} firm${ranked.length === 1 ? "" : "s"}`;
  el.hidden = false;
  el.className = `firms-health firms-health-${tone}`;
  el.textContent = `${summary} · ${fh.mode}`;
  el.title = ranked.map(r => `${r.firmId}: ${r.state}${r.reconnecting ? " (reconnecting)" : ""}`).join("\n");
}

// Header gateway pill, fed by the public /health poll. Visible to every
// logged-in user (admins also see the richer #firms-health badge from
// /admin/firms). Hidden when the host is in a no-session mode (Mock/
// Stub/Unavailable — /health.exchange.firms is absent), so it never
// guesses at a state we can't observe.
function renderGatewayPill() {
  const el = $("gateway-status");
  if (!el) return;
  const gh = getState().gatewayHealth;
  // No data yet, or host doesn't expose firm-level state → keep hidden.
  if (!gh || !Array.isArray(gh.firms)) {
    el.hidden = true;
    return;
  }
  el.hidden = false;
  let toneClass, label, ariaLabel, tooltip;
  if (gh.error) {
    toneClass = "status-disconnected";
    label = "gateway: unreachable";
    ariaLabel = `Exchange gateway: unreachable (${gh.error})`;
    tooltip = `/health fetch failed: ${gh.error}`;
  } else if (gh.firms.length === 0) {
    // Real mode wired but no firms registered — defensive; mirrors the
    // /health "no firms" branch where readyForOrders is vacuously true.
    toneClass = "status-not_ready";
    label = "gateway: no firms";
    ariaLabel = "Exchange gateway: no firms configured";
    tooltip = `${gh.mode}: no firms`;
  } else {
    const allEstablished = gh.firms.every(f => f.state === "established");
    const anyReconnecting = gh.firms.some(f => !!f.reconnecting);
    if (allEstablished && gh.readyForOrders) {
      toneClass = "status-connected";
      label = "gateway";
      ariaLabel = "Exchange gateway: established";
    } else if (anyReconnecting) {
      toneClass = "status-connecting";
      label = "gateway: reconnecting";
      ariaLabel = "Exchange gateway: reconnecting";
    } else {
      toneClass = "status-disconnected";
      // Pick the most useful single-word state to surface in the pill
      // when the firms differ; tooltip carries the per-firm breakdown.
      const worst = gh.firms.find(f => f.state !== "established") ?? gh.firms[0];
      label = `gateway: ${worst.state}`;
      ariaLabel = `Exchange gateway: ${worst.state}`;
    }
    tooltip = gh.firms.map(f =>
      `${f.firmId}: ${f.state}${f.reconnecting ? " (reconnecting)" : ""} v${f.sessionVerId}`
    ).join("\n");
  }
  el.className = `status-pill ${toneClass}`;
  el.textContent = label;
  el.setAttribute("aria-label", ariaLabel);
  el.title = tooltip;
}

export function renderForSlice(slice) {
  // state.clearAll() (logout / session expiry / WS "clear" frame)
  // notifies "all". The Order Detail modal is owned by ui and would
  // otherwise survive the state wipe — leaving the previous user's
  // ClOrdID rendered on top of the login screen with the capture-
  // phase Esc/Tab listener still live. Close it before any panel
  // re-render so subsequent renders run against a clean slate.
  if (slice === "all") closeOrderDetail();
  if (slice === "orders" || slice === "all" || slice === "blotterFilter" || slice === "blotterPage" || slice === "selectedOrder") renderBlotter();
  if (slice === "orders" || slice === "all") renderCancelAllButton();
  if (slice === "positions" || slice === "all") renderPositions();
  if (slice === "executions" || slice === "all") renderExecutions();
  if (slice === "executions" || slice === "orders" || slice === "all") refreshOpenOrderDetail();
  if (slice === "status") {
    setStatusPill(getState().status);
    renderReconnect(); // pill change usually correlates with countdown reset
    renderStaleness("ws");
  }
  if (slice === "user")   setUserLabel(getState().user);
  if (slice === "balance" || slice === "user" || slice === "all") renderBalance();
  if (slice === "marketData" || slice === "all") renderMarketData();
  if (slice === "marketDataStatus") {
    setMdStatusPill(getState().marketDataStatus);
    renderStaleness("md");
  }
  if (slice === "all") { renderStaleness("ws"); renderStaleness("md"); }
  if (slice === "submitInflight") renderInflight();
  if (slice === "wsReconnect") renderReconnect();
  if (slice === "firmsHealth" || slice === "all") renderFirmsHealth();
  if (slice === "gatewayHealth" || slice === "all") renderGatewayPill();
  if (slice === "currentView" || slice === "all") applyCurrentView(getState().currentView);
  if (slice === "watchlist" || slice === "selectedSymbol" || slice === "all") renderSelectedSymbol();
  if (slice === "watchlist" || slice === "selectedSymbol" || slice === "book" || slice === "all") renderDob();
  if (slice === "watchlist" || slice === "selectedSymbol" || slice === "chartResolution" || slice === "candles" || slice === "all") scheduleChartRender();
  if (slice === "watchlist" || slice === "selectedSymbol" || slice === "tapeShowAll" || slice === "tape" || slice === "all") scheduleTapeRender();
  // #71: heatmap re-renders on any tape/trade update (via "heatmap"
  // slice notify) and also when the watchlist or selection changes
  // so the cell set + selected-cell highlight stay in sync.
  if (slice === "heatmap" || slice === "watchlist" || slice === "selectedSymbol" || slice === "all") renderHeatmap();
  // Q1.6 (#258). Phase + auction wiring.
  if (slice === "phases" || slice === "marketData" || slice === "watchlist" || slice === "all") renderMarketData();
  if (slice === "phases" || slice === "selectedSymbol" || slice === "all") {
    reconcileAuctionPanel();
    renderTicketPhaseCoupling();
  }
  if (slice === "auction" || slice === "auctionPanelSymbol" || slice === "all") renderAuctionPanel();
  // Q1.4 (#256). The risk-policy slice flips the GTD horizon used by
  // validateTicketState, so a policy update must re-run ticket
  // validation — otherwise a late-arriving fetch leaves the submit
  // button in a stale enabled/disabled state until the trader nudges
  // an input.
  if (slice === "riskPolicy" || slice === "all") refreshTicketValidation();
}

// ── Stale-data overlay (T2) ────────────────────────────────────────
// Panels fed by the trader WS go stale whenever `state.status !==
// "connected"`; panels fed by the MD WS go stale on
// `state.marketDataStatus !== "connected"`. The visual cue is two-
// part: a `panel--stale` class (dims the data area in CSS) + an
// injected `<span class="stale-tag">stale · HH:MM:SS</span>` next to
// the panel's `<h2>` showing the last successful update timestamp.
// The timestamp is captured at the moment of staleness, not animated,
// so a frozen-but-still-mounted UI is unambiguous.
const WS_PANEL_SELECTORS = [".panel.blotter", ".panel.positions", ".panel.executions"];
const MD_PANEL_SELECTORS = [".panel.market-data", ".panel.dob", ".panel.chart", ".panel.tape"];

function fmtStaleTimestamp(ms) {
  if (!ms) return "no data";
  return new Date(ms).toLocaleTimeString("en-US", { hour12: false });
}

function renderStaleness(kind) {
  const s = getState();
  const isStale = kind === "ws"
    ? s.status !== "connected"
    : s.marketDataStatus !== "connected";
  const lastAt = kind === "ws" ? s.lastWsActivity : s.lastMdActivity;
  const selectors = kind === "ws" ? WS_PANEL_SELECTORS : MD_PANEL_SELECTORS;
  const label = isStale ? `stale · ${fmtStaleTimestamp(lastAt)}` : null;
  for (const sel of selectors) {
    const panel = document.querySelector(sel);
    if (!panel) continue;
    panel.classList.toggle("panel--stale", isStale);
    let tag = panel.querySelector(":scope > h2 > .stale-tag");
    if (label) {
      const h2 = panel.querySelector(":scope > h2");
      if (!h2) continue;
      if (!tag) {
        tag = document.createElement("span");
        tag.className = "stale-tag";
        tag.setAttribute("role", "status");
        tag.setAttribute("aria-live", "polite");
        h2.appendChild(tag);
      }
      tag.textContent = label;
    } else if (tag) {
      tag.remove();
    }
  }
}

function renderAll() {
  renderBlotter();
  renderPositions();
  renderExecutions();
  setStatusPill(getState().status);
  setUserLabel(getState().user);
  renderMarketData();
  renderSelectedSymbol();
  renderDob();
  scheduleChartRender();
  scheduleTapeRender();
  setMdStatusPill(getState().marketDataStatus);
  renderInflight();
  renderReconnect();
  renderFirmsHealth();
  reconcileAuctionPanel();
  renderAuctionPanel();
  renderTicketPhaseCoupling();
  ensureTicker();
}

function setMdStatusPill(status) {
  const el = $("md-status");
  if (!el) return;
  el.textContent = status;
  el.className = `status-pill status-${status}`;
  el.setAttribute("aria-label", `Market data: ${status}`);
}

const MD_STALE_MS = 5_000;

function renderMarketData() {
  const body = $("md-body");
  if (!body) return;
  const watch = getState().watchlist;
  const md = getState().marketData;
  // Show one row per watchlist symbol so the user sees pending
  // subscriptions even before the first trade arrives.
  const rows = watch.length > 0 ? watch : [...md.keys()];
  if (rows.length === 0) {
    body.innerHTML = `<tr><td colspan="5" class="muted">No subscriptions</td></tr>`;
    return;
  }
  const now = Date.now();
  body.innerHTML = rows.map(symbol => {
    const e = md.get(symbol);
    if (!e || e.lastPrice == null) {
      return `<tr><td>${escapeHtml(symbol)}${phaseBadgeHtml(symbol)}</td><td colspan="4" class="muted-cell">awaiting data…</td></tr>`;
    }
    const ts = e.updatedAt ? new Date(e.updatedAt).toISOString().slice(11, 19) : "—";
    const stale = e.updatedAt && (now - e.updatedAt) > MD_STALE_MS;
    const tsCls = stale ? ' class="md-cell-stale"' : "";
    return `<tr>
      <td>${escapeHtml(symbol)}${phaseBadgeHtml(symbol)}</td>
      <td class="num">${fmtPx(e.lastPrice)}</td>
      <td class="num">${fmtQty(e.lastQty)}</td>
      <td class="num">${e.lastTradeId ?? "—"}</td>
      <td${tsCls}>${ts}</td>
    </tr>`;
  }).join("");
}

// ── #71 Volume heatmap ──────────────────────────────────────────────

const HEATMAP_KEY = "b3tp.heatmap.enabled";
let _heatmapEnabled = false;

function readHeatmapEnabled() {
  try { return sessionStorage.getItem(HEATMAP_KEY) === "1"; }
  catch { return false; }
}
function writeHeatmapEnabled(on) {
  try {
    if (on) sessionStorage.setItem(HEATMAP_KEY, "1");
    else    sessionStorage.removeItem(HEATMAP_KEY);
  } catch { /* swallow */ }
}

// Pure helper: given a Map<symbol,sum>, return a Map<symbol, intensity>
// where intensity ∈ [0,1] normalised by the per-render global max. A
// uniform-zero grid (no recent trades anywhere) returns all-zero so the
// renderer can paint a uniform "cold" cell rather than divide-by-zero.
export function normaliseHeatmap(volumes) {
  let max = 0;
  for (const v of volumes.values()) if (v > max) max = v;
  const out = new Map();
  for (const [k, v] of volumes) out.set(k, max > 0 ? v / max : 0);
  return out;
}

function heatmapCellColor(intensity) {
  // 0 → dark slate (no recent volume), 1 → hot red. Use HSL so the
  // mid-range cells stay clearly distinguishable from the floor.
  if (!(intensity > 0)) return "rgba(255,255,255,.04)";
  // Hue 30 (warm orange) → 0 (red); lightness 18% → 50%.
  const hue   = 30 - 30 * intensity;
  const light = 18 + 32 * intensity;
  return `hsl(${hue.toFixed(1)} 80% ${light.toFixed(1)}%)`;
}

function renderHeatmap() {
  const el = $("heatmap-panel");
  if (!el) return;
  if (!_heatmapEnabled) {
    if (!el.hidden) { el.hidden = true; el.replaceChildren(); }
    return;
  }
  el.hidden = false;
  const st = getState();
  // Drive the grid from the watchlist so the cell layout matches what
  // the user explicitly subscribed to. Fall back to any symbol that has
  // received market data — keeps the panel useful even before the
  // trader edits the watchlist.
  const symbols = st.watchlist.length > 0
    ? [...st.watchlist]
    : [...st.marketData.keys()];
  if (symbols.length === 0) {
    el.replaceChildren();
    const empty = document.createElement("p");
    empty.className = "heatmap-empty muted";
    empty.textContent = "no symbols subscribed";
    el.appendChild(empty);
    return;
  }
  const volumes = computeHeatmapVolumes(st.heatmapTrades, symbols, Date.now(), HEATMAP_WINDOW_MS);
  const intensities = normaliseHeatmap(volumes);
  const sel = st.selectedSymbol;
  // Diff against existing cell set so we don't churn the DOM on every
  // tick — cells are reused, only style + label content change.
  const existing = new Map();
  for (const child of el.children) {
    if (child.dataset && child.dataset.symbol) existing.set(child.dataset.symbol, child);
  }
  const frag = document.createDocumentFragment();
  for (const sym of symbols) {
    const intensity = intensities.get(sym) ?? 0;
    const vol = volumes.get(sym) ?? 0;
    let cell = existing.get(sym);
    if (!cell) {
      cell = document.createElement("button");
      cell.type = "button";
      cell.className = "heatmap-cell";
      cell.setAttribute("role", "gridcell");
      cell.dataset.symbol = sym;
      cell.addEventListener("click", () => onSelectSymbol(sym));
    } else {
      existing.delete(sym);
    }
    cell.style.backgroundColor = heatmapCellColor(intensity);
    cell.classList.toggle("heatmap-cell--selected", sym === sel);
    cell.setAttribute("aria-label",
      `${sym}: ${fmtQty(vol)} traded in last ${Math.round(HEATMAP_WINDOW_MS / 1000)}s`);
    cell.title = cell.getAttribute("aria-label");
    cell.innerHTML = `<span class="heatmap-cell-sym">${escapeHtml(sym)}</span>` +
                     `<span class="heatmap-cell-vol">${fmtQty(vol)}</span>`;
    frag.appendChild(cell);
  }
  // Surviving entries in `existing` are symbols no longer in the
  // watchlist (or removed snapshot) — drop their cells.
  for (const stale of existing.values()) stale.remove();
  el.replaceChildren(frag);
}

function setHeatmapEnabled(on) {
  _heatmapEnabled = !!on;
  writeHeatmapEnabled(_heatmapEnabled);
  const btn = $("heatmap-toggle");
  if (btn) btn.setAttribute("aria-pressed", _heatmapEnabled ? "true" : "false");
  renderHeatmap();
}

// ── Q1.6 (#258): Phase badge + auction panel + ticket coupling ────

// Maps the wire-side TradingPhase enum names to the trader-facing
// labels + CSS class fragments. Unknown is intentionally not in the
// map: we render no badge at all when the phase is Unknown so a
// not-yet-loaded snapshot doesn't show a misleading "OPEN" default.
const PHASE_LABELS = {
  Reserved:         { label: "RESERVED",  cls: "RESERVED"  },
  OpeningCall:      { label: "PRE-OPEN",  cls: "PRE-OPEN"  },
  Open:             { label: "OPEN",      cls: "OPEN"      },
  FinalClosingCall: { label: "CLOSING",   cls: "CLOSING"   },
  Close:            { label: "CLOSED",    cls: "CLOSED"    },
};

export function phaseBadgeHtml(symbol) {
  const phase = getPhase(symbol);
  const meta = PHASE_LABELS[phase];
  if (!meta) return "";
  const aria = `${symbol} phase: ${meta.label}`;
  return ` <span class="phase-badge ${meta.cls}" data-symbol="${escapeHtml(symbol)}" aria-label="${escapeHtml(aria)}">${meta.label}</span>`;
}

// Auto-open / refresh the auction panel based on the selected symbol's
// phase. Called whenever phases or selectedSymbol changes. The "open"
// state is owned by state.auctionPanelSymbol so the WS layer can key
// off it for subscribe/unsubscribe.
export function reconcileAuctionPanel() {
  const st = getState();
  const sym = st.selectedSymbol;
  if (!sym) {
    if (st.auctionPanelSymbol !== null) setAuctionPanelSymbol(null);
    return;
  }
  const phase = getPhase(sym);
  if (isAuctionPhase(phase)) {
    // Auto-open / switch to the selected symbol. Assigning a new symbol
    // implicitly drops any previous panel-symbol subscription so symbol
    // switches in-flight don't leak an orphaned auction.${prev} sub.
    if (st.auctionPanelSymbol !== sym) setAuctionPanelSymbol(sym);
  } else {
    // Selected symbol is NOT in an auction phase. Drop the panel +
    // subscription whenever it's open — covers both cases:
    //   (a) the same symbol left auction (cross printed),
    //   (b) the trader switched to a non-auction symbol while a panel
    //       for a different symbol was still pinned (the previous
    //       implementation only closed when symbols matched, leaking
    //       the auction.${prev} subscription).
    // The trader can manually re-open via the toggle button for any
    // selected symbol if they want to keep watching.
    if (st.auctionPanelSymbol !== null) {
      setAuctionPanelSymbol(null);
    }
  }
}

export function renderAuctionPanel() {
  const panel = $("auction-panel");
  if (!panel) return;
  const st = getState();
  const sym = st.auctionPanelSymbol;
  if (!sym) {
    panel.hidden = true;
    panel.classList.add("collapsed");
    const body = $("auction-body");
    if (body) body.hidden = true;
    const toggle = $("auction-toggle");
    if (toggle) toggle.setAttribute("aria-expanded", "false");
    const caret = panel.querySelector(".auction-caret");
    if (caret) caret.textContent = "▸";
    return;
  }

  panel.hidden = false;
  panel.classList.remove("collapsed");
  panel.setAttribute("aria-label", `Auction state for ${sym}`);
  const body = $("auction-body");
  if (body) body.hidden = false;
  const toggle = $("auction-toggle");
  if (toggle) toggle.setAttribute("aria-expanded", "true");
  const caret = panel.querySelector(".auction-caret");
  if (caret) caret.textContent = "▾";

  const tag = $("auction-symbol-tag");
  if (tag) tag.textContent = sym;

  const aux = getAuctionState(sym);
  const top = aux?.top ?? null;
  const prev = aux?.prevTop ?? null;
  const matchQty = aux?.indicativeMatchQty ?? null;
  const imb = aux?.imbalance ?? null;
  const imbSide = aux?.imbalanceSide ?? null;

  const topPriceEl = $("auction-top-price");
  if (topPriceEl) topPriceEl.textContent = top == null ? "—" : fmtPx(top);
  const arrowEl = $("auction-top-arrow");
  if (arrowEl) {
    if (top != null && prev != null && top !== prev) {
      arrowEl.textContent = top > prev ? "▲" : "▼";
      arrowEl.className = "auction-arrow " + (top > prev ? "up" : "down");
    } else {
      arrowEl.textContent = "";
      arrowEl.className = "auction-arrow";
    }
  }

  const matchEl = $("auction-match-qty");
  if (matchEl) matchEl.textContent = matchQty == null ? "—" : fmtQty(matchQty);

  const imbEl = $("auction-imbalance");
  if (imbEl) {
    if (imb == null || imb === 0) {
      imbEl.textContent = imb == null ? "—" : "0";
      imbEl.className = "auction-value";
    } else {
      const sideLabel = imbSide && imbSide !== "None" ? imbSide : "";
      imbEl.textContent = `${fmtQty(imb)}${sideLabel ? ` ${sideLabel}` : ""}`;
      imbEl.className = "auction-value " + (imbSide === "Buy"  ? "imb-buy"
                                          : imbSide === "Sell" ? "imb-sell"
                                          : "");
    }
  }

  // Time-to-uncross: upstream B3MatchingPlatform doesn't expose this
  // today (#321 in the upstream tracker — when it lands, the auction
  // frame will gain a field we can render here without further UI
  // work). For now ship the placeholder.
  const ttuEl = $("auction-ttu");
  if (ttuEl) ttuEl.textContent = "—";

  const printsEl = $("auction-prints");
  if (printsEl) {
    const prints = aux?.lastPrints ?? [];
    if (prints.length === 0) {
      printsEl.innerHTML = `<li class="muted-line">No prints yet</li>`;
    } else {
      printsEl.innerHTML = prints.map(p => {
        const ts = p.at ? new Date(p.at).toISOString().slice(11, 19) : "—";
        const kind = escapeHtml(p.kind ?? "");
        return `<li><span class="auction-print-kind">${kind}</span> <span class="auction-print-px">${fmtPx(p.price)}</span> × <span class="auction-print-qty">${fmtQty(p.qty)}</span> <span class="muted-line">${escapeHtml(ts)}</span></li>`;
      }).join("");
    }
  }
}

// Order-ticket coupling. Reacts to phase transitions on the symbol the
// trader is currently typing into. Three things happen:
//   1. TIF default flips to GoodForAuction in OpeningCall /
//      FinalClosingCall (the trader gets a hint explaining why).
//   2. TIF=Day in an auction phase shows a soft warning that the order
//      will sit pending until the cross — non-blocking.
//   3. Reserved (halt) disables Submit with an explanatory tooltip.
export function renderTicketPhaseCoupling() {
  const symEl = $("ticket-symbol");
  const tifEl = $("ticket-tif");
  const submitEl = $("ticket-submit");
  const hintEl = $("ticket-tif-hint");
  if (!symEl || !tifEl || !submitEl) return;

  const sym = (symEl.value || "").trim().toUpperCase();
  const phase = sym ? getPhase(sym) : "Unknown";

  // (1) Auto-pick GoodForAuction the first time we see an auction
  // phase for this symbol — but don't trample a value the trader has
  // explicitly chosen for this ticket. We mark the auto-pick on the
  // dataset so a manual change clears it.
  const inAuction = isAuctionPhase(phase);
  if (inAuction) {
    if (tifEl.value === "Day" && tifEl.dataset.userPicked !== "1") {
      tifEl.value = "GoodForAuction";
      tifEl.dataset.autoPicked = "1";
    }
  } else if (tifEl.dataset.autoPicked === "1") {
    // Phase left auction territory — revert the auto-pick to Day so
    // the trader doesn't accidentally submit a GoodForAuction on the
    // wrong phase.
    tifEl.value = "Day";
    delete tifEl.dataset.autoPicked;
  }

  // (2) Hint / soft warning under the TIF select.
  if (hintEl) {
    if (inAuction && tifEl.value === "GoodForAuction") {
      hintEl.hidden = false;
      hintEl.className = "field-hint hint-info";
      hintEl.textContent = "Auction phase — GoodForAuction recommended";
    } else if (inAuction && tifEl.value === "Day") {
      hintEl.hidden = false;
      hintEl.className = "field-hint hint-warn";
      hintEl.textContent = "This order will remain pending until the open.";
    } else {
      hintEl.hidden = true;
      hintEl.textContent = "";
      hintEl.className = "field-hint";
    }
  }

  // (3) Reserved (halt) disables Submit. We track the halt-disable
  // independently of the inflight-disable via dataset flags so toggling
  // phases doesn't re-enable a submitting button (and vice-versa).
  // applySubmitDisabled() ORs both conditions together.
  const halted = phase === "Reserved";
  if (halted) {
    submitEl.dataset.haltDisabled = "1";
    submitEl.setAttribute("title", "Instrument halted");
  } else if (submitEl.dataset.haltDisabled === "1") {
    delete submitEl.dataset.haltDisabled;
    submitEl.removeAttribute("title");
  }
  applySubmitDisabled();
}

const DOB_TOP_N = 10;
const DOB_NO_BOOK_AFTER_MS = 10_000;

function renderDob() {
  const bidsBody = document.querySelector("#dob-bids tbody");
  const asksBody = document.querySelector("#dob-asks tbody");
  const feedback = $("dob-feedback");
  const spreadEl = $("dob-spread");
  if (!bidsBody || !asksBody) return;

  const st = getState();
  const current = st.selectedSymbol;

  if (!current) {
    bidsBody.innerHTML = `<tr><td colspan="3" class="muted-cell">select a symbol</td></tr>`;
    asksBody.innerHTML = `<tr><td colspan="3" class="muted-cell">select a symbol</td></tr>`;
    if (feedback) { feedback.hidden = true; feedback.textContent = ""; }
    if (spreadEl) spreadEl.textContent = "";
    return;
  }

  const entry = st.book.get(current);
  if (!entry || !entry.ready) {
    // After ~10s without a snapshot, swap the soft "awaiting…" copy for
    // a louder hint that something is wrong with the MD subscription
    // (most commonly: MBP not enabled or the URL is mistyped).
    // #379: also factor in the last MD-side reset (mdWorker `md.clear` on
    // reconnect, or an explicit clearAllBooks). Otherwise a long-standing
    // selection + a fresh reconnect would trip the agressive warning
    // immediately, even when a healthy snapshot is moments away.
    const sinceMd = Math.max(st.selectedSymbolSetAt || 0, st.lastMdResetAt || 0);
    const waited = sinceMd ? Date.now() - sinceMd : 0;
    const msg = waited > DOB_NO_BOOK_AFTER_MS
      ? "no book — check MD settings ⚙"
      : "awaiting book snapshot…";
    bidsBody.innerHTML = `<tr><td colspan="3" class="muted-cell">${msg}</td></tr>`;
    asksBody.innerHTML = `<tr><td colspan="3" class="muted-cell">${msg}</td></tr>`;
    if (feedback) { feedback.hidden = true; feedback.textContent = ""; }
    if (spreadEl) spreadEl.textContent = "";
    return;
  }

  const bids = [...entry.bids.entries()]
    .map(([k, v]) => ({ price: Number(k), qty: v.qty, count: v.count }))
    .sort((a, b) => b.price - a.price)
    .slice(0, DOB_TOP_N);
  const asks = [...entry.asks.entries()]
    .map(([k, v]) => ({ price: Number(k), qty: v.qty, count: v.count }))
    .sort((a, b) => a.price - b.price)
    .slice(0, DOB_TOP_N);

  bidsBody.innerHTML = renderDobSide(bids, "bid");
  asksBody.innerHTML = renderDobSide(asks, "ask");
  if (feedback) { feedback.hidden = true; feedback.textContent = ""; }

  // #342: Spread + mid sub-header. Saves the trader from eyeballing
  // top-of-book themselves. Crossed / locked markets render as a
  // muted "—" rather than a misleading negative spread.
  if (spreadEl) {
    if (bids.length === 0 || asks.length === 0) {
      spreadEl.textContent = "";
    } else {
      const bestBid = bids[0].price;
      const bestAsk = asks[0].price;
      const spread = bestAsk - bestBid;
      const mid = (bestAsk + bestBid) / 2;
      if (!Number.isFinite(spread) || spread <= 0) {
        spreadEl.textContent = `mid ${fmtPx(mid)} · spread —`;
      } else {
        const bps = mid > 0 ? Math.round((spread / mid) * 10000) : 0;
        spreadEl.textContent = `mid ${fmtPx(mid)} · spread ${fmtPx(spread)} (${bps} bp)`;
      }
    }
  }
}

function renderDobSide(levels, side) {
  if (levels.length === 0) {
    return `<tr><td colspan="3" class="muted-cell">empty</td></tr>`;
  }
  let cum = 0;
  return levels.map(lv => {
    cum += Number(lv.qty) || 0;
    const price = fmtPx(lv.price);
    const qty = fmtQty(lv.qty);
    const cumS = fmtQty(cum);
    if (side === "bid") {
      return `<tr><td class="num">${cumS}</td><td class="num">${qty}</td><td class="num">${price}</td></tr>`;
    }
    return `<tr><td class="num">${price}</td><td class="num">${qty}</td><td class="num">${cumS}</td></tr>`;
  }).join("");
}

function renderSelectedSymbol() {
  const sel = $("selected-symbol");
  if (!sel) return;
  const st = getState();
  const watch = st.watchlist;
  const desired = ["", ...watch];
  const existing = [...sel.options].map(o => o.value);
  if (desired.length !== existing.length || desired.some((v, i) => v !== existing[i])) {
    sel.innerHTML = `<option value="">— select —</option>` +
      watch.map(s => `<option value="${escapeHtml(s)}">${escapeHtml(s)}</option>`).join("");
  }
  if (sel.value !== (st.selectedSymbol ?? "")) sel.value = st.selectedSymbol ?? "";

  // Sync the ticket-symbol <datalist> from the watchlist so the trader
  // gets autocomplete for tickers they're already subscribed to.
  const dl = $("watchlist-symbols");
  if (dl) {
    const want = watch.map(s => `<option value="${escapeHtml(s)}"></option>`).join("");
    if (dl.innerHTML !== want) dl.innerHTML = want;
  }

  // Mirror the active symbol on each panel header tag.
  for (const id of ["dob-symbol-tag", "chart-symbol-tag", "tape-symbol-tag"]) {
    const tag = $(id);
    if (!tag) continue;
    if (id === "tape-symbol-tag" && st.tapeShowAll) {
      tag.textContent = "all";
      tag.classList.add("symbol-tag--muted");
    } else {
      tag.textContent = st.selectedSymbol ?? "—";
      tag.classList.toggle("symbol-tag--muted", !st.selectedSymbol);
    }
  }

  // Keep the tape "All symbols" checkbox in sync with state.
  const tapeAll = $("tape-show-all");
  if (tapeAll && tapeAll.checked !== !!st.tapeShowAll) {
    tapeAll.checked = !!st.tapeShowAll;
  }
}

// ── Chart panel (T3) ──────────────────────────────────────────────

const CHART_VISIBLE_BARS = 150;
const CHART_VIEW_W = 300;
const CHART_VIEW_H = 100;
const CHART_PADDING = 4;
// Mirror DOB_NO_BOOK_AFTER_MS: after this long without a candle frame
// for the selected symbol, swap the soft "awaiting…" copy for a louder
// hint that the server probably doesn't publish candles (the current
// b3-marketdata image has zero candle support — issue #91).
const CHART_NO_DATA_AFTER_MS = 8_000;

let chartRafHandle = null;

function scheduleChartRender() {
  if (chartRafHandle != null) return;
  // requestAnimationFrame may be unavailable in the test runtime; fall
  // back to a microtask to keep the contract identical (one repaint
  // per batch of state notifies).
  const raf = typeof requestAnimationFrame === "function"
    ? requestAnimationFrame
    : (cb) => setTimeout(cb, 16);
  chartRafHandle = raf(() => {
    chartRafHandle = null;
    renderChart();
  });
}

function renderChart() {
  const svg   = $("chart-svg");
  const empty = $("chart-empty");
  const resSel = $("chart-resolution");
  if (!svg || !resSel) return;

  const st = getState();

  const resStr = String(st.chartResolution);
  if (resSel.value !== resStr) resSel.value = resStr;

  const showEmpty = (msg) => {
    svg.innerHTML = "";
    if (empty) { empty.hidden = false; empty.textContent = msg; }
  };

  if (!st.selectedSymbol) { showEmpty("select a symbol"); return; }

  const perRes = st.candles.get(st.selectedSymbol);
  const entry = perRes?.get(st.chartResolution);
  if (!entry || !entry.ready) {
    // #379: see renderDob — chart staleness must also reset on MD-side
    // reconnects, not just on selection changes.
    const sinceMd = Math.max(st.selectedSymbolSetAt || 0, st.lastMdResetAt || 0);
    const waited = sinceMd ? Date.now() - sinceMd : 0;
    showEmpty(waited > CHART_NO_DATA_AFTER_MS
      ? "no candle snapshot received"
      : "awaiting candle snapshot…");
    return;
  }
  if (entry.bars.length === 0) {
    // Snapshot arrived empty — aggregator has no history yet for this
    // resolution. The first CandleUpdate will fix this when a trade
    // closes a window.
    showEmpty("no candles yet — waiting for first trade");
    return;
  }

  if (empty) empty.hidden = true;

  const bars = entry.bars.slice(-CHART_VISIBLE_BARS);
  const lows = bars.map(b => Number(b.low));
  const highs = bars.map(b => Number(b.high));
  const lo = Math.min(...lows);
  const hi = Math.max(...highs);
  const range = hi - lo || Math.abs(hi) * 1e-4 || 1; // avoid div-zero on flat books

  const innerW = CHART_VIEW_W - CHART_PADDING * 2;
  const innerH = CHART_VIEW_H - CHART_PADDING * 2;
  const slotW = innerW / bars.length;
  const bodyW = Math.max(0.4, slotW * 0.7);

  const yFor = (price) => CHART_PADDING + (1 - (Number(price) - lo) / range) * innerH;

  let parts = "";
  for (let i = 0; i < bars.length; i++) {
    const b = bars[i];
    const cx = CHART_PADDING + slotW * (i + 0.5);
    const yHigh = yFor(b.high);
    const yLow  = yFor(b.low);
    const yOpen  = yFor(b.open);
    const yClose = yFor(b.close);
    const up = Number(b.close) >= Number(b.open);
    const cls = up ? "candle-up" : "candle-down";
    const yTop = Math.min(yOpen, yClose);
    const bodyH = Math.max(0.4, Math.abs(yClose - yOpen));
    parts += `<line class="candle-wick ${cls}" x1="${cx.toFixed(2)}" x2="${cx.toFixed(2)}" y1="${yHigh.toFixed(2)}" y2="${yLow.toFixed(2)}"/>`;
    parts += `<rect class="${cls}" x="${(cx - bodyW / 2).toFixed(2)}" y="${yTop.toFixed(2)}" width="${bodyW.toFixed(2)}" height="${bodyH.toFixed(2)}"/>`;
  }
  svg.innerHTML = parts;
}

// ── Trade tape (T4) ───────────────────────────────────────────────

const TAPE_VISIBLE = 200;

let tapeRafHandle = null;
let tapePaused = false;
let tapeDirty = false;

function scheduleTapeRender() {
  // Pause-on-hover: while the user is reading the tape, freeze DOM
  // updates so rows don't shift under the cursor. Latest state still
  // gets painted on mouseleave (we just remember a repaint is owed).
  tapeDirty = true;
  if (tapePaused) return;
  if (tapeRafHandle != null) return;
  const raf = typeof requestAnimationFrame === "function"
    ? requestAnimationFrame
    : (cb) => setTimeout(cb, 16);
  tapeRafHandle = raf(() => {
    tapeRafHandle = null;
    tapeDirty = false;
    renderTape();
  });
}

function renderTape() {
  const list = $("tape-list");
  if (!list) return;

  const st = getState();

  // Source of rows: when tapeShowAll is true, flatten every cached
  // symbol and re-sort by receivedAt desc. Otherwise scope to the
  // shared selectedSymbol — if no symbol is picked, render empty.
  let rows;
  if (st.tapeShowAll) {
    rows = [];
    for (const [sym, arr] of st.tape) {
      for (const e of arr) rows.push({ ...e, symbol: sym });
    }
    rows.sort((a, b) => b.receivedAt - a.receivedAt);
    rows = rows.slice(0, TAPE_VISIBLE);
  } else if (st.selectedSymbol) {
    const arr = st.tape.get(st.selectedSymbol);
    rows = arr ? arr.slice().reverse() : [];
    rows = rows.slice(0, TAPE_VISIBLE).map(e => ({ ...e, symbol: st.selectedSymbol }));
  } else {
    rows = [];
  }

  if (rows.length === 0) {
    const msg = st.tapeShowAll
      ? "no trades yet"
      : (st.selectedSymbol ? "no trades yet" : "select a symbol");
    list.innerHTML = `<li class="tape-empty">${msg}</li>`;
    return;
  }

  list.innerHTML = rows.map(tapeRow).join("");
}

function tapeRow(e) {
  const cls = `tape-${e.side}` + (e.busted ? " tape-busted" : "");
  // Include milliseconds (slice 11..23) so two prints in the same
  // second can still be ordered visually.
  const ts = new Date(e.receivedAt).toISOString().slice(11, 23);
  const arrow = e.side === "up" ? "▲" : e.side === "down" ? "▼" : "·";
  return `<li class="${cls}">`
    + `<span>${ts}</span>`
    + `<span>${escapeHtml(e.symbol)}</span>`
    + `<span class="tape-num">${arrow} ${fmtPx(e.price)}</span>`
    + `<span class="tape-num">${fmtQty(e.qty)}</span>`
    + `<span class="tape-num">#${e.tradeId}</span>`
    + `</li>`;
}

const BLOTTER_PAGE_SIZE = 25;

function renderBlotter() {
  const body = $("blotter-body");
  const st = getState();
  const filter = st.blotterFilter ?? { text: "", status: "", hideTerminal: true };
  syncFilterInputs(filter);
  const search = filter.text.trim().toUpperCase();
  const wantStatus = filter.status;
  const hideTerm = filter.hideTerminal !== false;
  const all = [...st.orders.values()];
  // Default sort: newest-first by per-ClOrdID arrival sequence
  // (assigned in state.applyOrders*). Falling back to clOrdId keeps
  // ordering deterministic if seq is missing for any reason.
  const seqOf = (o) => st.orderSeq?.get(o.clOrdId) ?? 0;
  const filtered = all
    .filter(o => !search || o.symbol.toUpperCase().includes(search) || o.clOrdId.toUpperCase().includes(search))
    .filter(o => !wantStatus || o.status === wantStatus)
    // #342: "Working only" hides terminal rows (Filled / Cancelled /
    // Rejected). Skipped when an explicit status filter is set so the
    // trader can still pin a terminal status when they want to.
    .filter(o => !hideTerm || wantStatus || !isTerminalOrderStatus(o.status))
    .sort((a, b) => {
      const diff = seqOf(b) - seqOf(a);
      return diff !== 0 ? diff : b.clOrdId.localeCompare(a.clOrdId);
    });

  const totalPages = Math.max(1, Math.ceil(filtered.length / BLOTTER_PAGE_SIZE));
  const page = Math.min(Math.max(1, st.blotterPage ?? 1), totalPages);
  const start = (page - 1) * BLOTTER_PAGE_SIZE;
  const pageRows = filtered.slice(start, start + BLOTTER_PAGE_SIZE);

  $("blotter-count").textContent = `${filtered.length}/${all.length}`;
  body.innerHTML = pageRows.map(o => orderRow(o, st)).join("");

  const pager = $("blotter-pagination");
  if (pager) {
    if (totalPages <= 1) {
      pager.hidden = true;
    } else {
      pager.hidden = false;
      const info = $("blotter-page-info");
      if (info) info.textContent = `page ${page} / ${totalPages}`;
      const prev = $("blotter-prev");
      const next = $("blotter-next");
      if (prev) prev.disabled = page <= 1;
      if (next) next.disabled = page >= totalPages;
    }
  }
}

const HIGHLIGHT_MS = 2000;

function orderRow(o, st) {
  const terminal = isTerminalOrderStatus(o.status);
  const cancelInflight = st.inflightCancels?.has(o.clOrdId);
  const modifyInflight = st.inflightModifies?.has(o.clOrdId);
  const price = o.price == null ? "—" : fmtPx(o.price);
  const highlightAt = st.ordersHighlight?.get(o.clOrdId);
  const fresh = highlightAt && (Date.now() - highlightAt) < HIGHLIGHT_MS;
  const selected = st.selectedClOrdId === o.clOrdId;
  // Slice 3 of #132. Surfaced from OrderDto.IsStale/StaleReason/StaledAtUtc.
  // The platform's auto-detect (slice 2) bulk-marks every working order
  // for a firm when the FIXP gateway sees a venue-divergence signal, so
  // the trader needs an at-a-glance "this may not be at the venue any
  // more" cue plus disabled actions (the backend already 409s; we gate
  // client-side to avoid the round-trip and to show intent honestly).
  const isStale = !!o.isStale;
  const cls = [
    fresh ? "row-fresh" : "",
    selected ? "row-selected" : "",
    isStale ? "row-stale" : "",
  ].filter(Boolean).join(" ");
  const cancelDisabled = terminal || cancelInflight || modifyInflight || isStale;
  const cancelLabel = cancelInflight ? "Cancelling…" : "Cancel";
  const cancelCls = "cancel-btn" + (cancelInflight ? " cancelling" : "");
  // Slice 5 of #122. Modify button shares row-selection delegation
  // (data-clordid on the button so the click handler can map it back
  // to the order without walking the row). Disabled while terminal
  // or while either a cancel or another modify is in flight — the
  // backend's in-flight guard would 409 a second modify anyway, but
  // gating client-side avoids the round-trip and keeps the UX honest.
  const modifyDisabled = terminal || modifyInflight || cancelInflight || isStale;
  const modifyLabel = modifyInflight ? "Modifying…" : "Modify";
  const modifyCls = "modify-btn" + (modifyInflight ? " modifying" : "");
  const staleTitle = isStale
    ? `Stale: ${o.staleReason || "venue desync"}${o.staledAtUtc ? ` (${o.staledAtUtc})` : ""}`
    : "";
  const staleBadge = isStale
    ? `<span class="order-stale-badge" title="${escapeHtml(staleTitle)}">stale</span>`
    : "";
  const optionBadge = o.securityType === "Option" ? optionBadgeHtml(o.optionPutOrCall) : "";
  const optionTooltip = formatOptionTooltip(o);
  const symbolTitle = optionTooltip ? ` title="${escapeHtml(optionTooltip)}"` : "";
  const actionTitle = isStale ? `disabled — ${staleTitle}` : "";
  return `<tr data-clordid="${escapeHtml(o.clOrdId)}"${cls ? ` class="${cls}"` : ""}>
    <td><code>${escapeHtml(o.clOrdId)}</code></td>
    <td${symbolTitle}>${escapeHtml(o.symbol)}${optionBadge}</td>
    <td>${escapeHtml(o.side)}</td>
    <td>${typeChipHtml(o.type)}</td>
    <td>${escapeHtml(o.timeInForce ?? "")}</td>
    <td class="num">${fmtQty(o.quantity)}</td>
    <td class="num">${fmtQty(o.leavesQuantity)}</td>
    <td class="num">${fmtQty(o.cumulativeQuantity)}</td>
    <td class="num">${price}</td>
    <td class="status-cell-${escapeHtml(o.status)}">${escapeHtml(o.status)}${staleBadge}</td>
    <td class="row-stale-actions"><button class="${modifyCls}" data-clordid="${escapeHtml(o.clOrdId)}" aria-label="Modify order ${escapeHtml(o.clOrdId)}" title="${escapeHtml(actionTitle || "Modify")}" ${modifyDisabled ? "disabled" : ""}>${modifyLabel}</button></td>
    <td class="row-stale-actions"><button class="${cancelCls}" data-clordid="${escapeHtml(o.clOrdId)}" aria-label="Cancel order ${escapeHtml(o.clOrdId)}" title="${escapeHtml(actionTitle || "Cancel (Del)")}" ${cancelDisabled ? "disabled" : ""}>${cancelLabel}</button></td>
  </tr>`;
}

function syncFilterInputs(filter) {
  const t = $("blotter-filter-text");
  const s = $("blotter-filter-status");
  const h = $("blotter-hide-terminal");
  if (t && document.activeElement !== t) t.value = filter.text;
  if (s && s.value !== filter.status) s.value = filter.status;
  if (h) {
    const want = filter.hideTerminal !== false;
    if (h.checked !== want) h.checked = want;
  }
}

function renderPositions() {
  const body = $("positions-body");
  let positions = [...getState().positions.values()]
    .filter(p => p.netQuantity !== 0);
  
  // FE-OPT-3 (#499). Apply expiry filter if set.
  if (_expiryFilter) {
    positions = positions.filter(p => 
      p.securityType === "Option" && p.optionExpirationDate === _expiryFilter
    );
  }
  
  sortPositionsInPlace(positions, _positionsSort);
  
  if (positions.length === 0) {
    const msg = _expiryFilter 
      ? `No positions for expiry ${formatExpiryChip(_expiryFilter)}`
      : "No positions";
    body.innerHTML = `<tr><td colspan="3" class="muted">${msg}</td></tr>`;
    syncPositionsSortHeaders();
    syncPositionsGroupToggle();
    renderExpiryStrip();
    return;
  }
  
  if (_positionsGrouped) {
    body.innerHTML = renderGroupedPositions(positions);
  } else {
    body.innerHTML = positions.map(p => renderPositionRow(p)).join("");
  }
  syncPositionsSortHeaders();
  syncPositionsGroupToggle();
  renderExpiryStrip();
}

// FE-OPT-3 (#499). Render a single position row (used in flat and grouped views).
function renderPositionRow(p, indent = false) {
  const optionBadge = p.securityType === "Option" ? optionBadgeHtml(p.optionPutOrCall) : "";
  const optionTooltip = formatOptionTooltip(p);
  const symbolTitle = optionTooltip ? ` title="${escapeHtml(optionTooltip)}"` : "";
  const indentClass = indent ? ' class="pos-indent"' : "";
  return `<tr${indentClass}>
    <td${symbolTitle}>${escapeHtml(p.symbol)}${optionBadge}</td>
    <td class="num">${fmtQty(p.netQuantity)}</td>
    <td class="num">${fmtPx(p.averageEntryPrice)}</td>
  </tr>`;
}

// FE-OPT-3 (#499). Group positions by underlying symbol.
function renderGroupedPositions(positions) {
  // Group: options go under their underlyingSymbol, equities stand alone.
  const groups = new Map();
  for (const p of positions) {
    const key = p.securityType === "Option" && p.optionUnderlyingSymbol
      ? p.optionUnderlyingSymbol
      : p.symbol;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(p);
  }
  
  // Sort group keys alphabetically.
  const sortedKeys = [...groups.keys()].sort((a, b) => a.localeCompare(b));
  
  let html = "";
  for (const key of sortedKeys) {
    const items = groups.get(key);
    // Count total net contracts in group.
    const totalNet = items.reduce((sum, p) => sum + (Number(p.netQuantity) || 0), 0);
    // Check if this is a single equity (not a group of options).
    const isSingleEquity = items.length === 1 && items[0].securityType !== "Option";
    
    if (isSingleEquity) {
      // Render single equity without group header.
      html += renderPositionRow(items[0]);
    } else {
      // Render group header row.
      const netSign = totalNet > 0 ? "+" : "";
      html += `<tr class="pos-group-header" data-underlying="${escapeHtml(key)}">
        <td><strong>${escapeHtml(key)}</strong> <span class="muted">(${items.length})</span></td>
        <td class="num"><strong>${netSign}${fmtQty(totalNet)}</strong></td>
        <td></td>
      </tr>`;
      // Render items (options) indented under the header.
      for (const p of items) {
        html += renderPositionRow(p, true);
      }
    }
  }
  return html;
}

// FE-OPT-3 (#499). Sync the group toggle button state.
function syncPositionsGroupToggle() {
  const btn = $("positions-group-toggle");
  if (!btn) return;
  btn.setAttribute("aria-pressed", _positionsGrouped ? "true" : "false");
  btn.classList.toggle("active", _positionsGrouped);
}

// FE-OPT-3 (#499). Render expiry strip showing upcoming option expirations.
let _expiryFilter = null; // null = show all, else ISO date string
function renderExpiryStrip() {
  const strip = $("positions-expiry-strip");
  const items = $("expiry-strip-items");
  if (!strip || !items) return;
  
  const positions = [...getState().positions.values()]
    .filter(p => p.netQuantity !== 0 && p.securityType === "Option" && p.optionExpirationDate);
  
  if (positions.length === 0) {
    strip.hidden = true;
    return;
  }
  
  // Collect unique expiry dates and count positions per date.
  const expiries = new Map();
  for (const p of positions) {
    const exp = p.optionExpirationDate;
    expiries.set(exp, (expiries.get(exp) || 0) + 1);
  }
  
  // Sort by date.
  const sortedDates = [...expiries.keys()].sort();
  
  // Render chips.
  items.innerHTML = sortedDates.map(exp => {
    const count = expiries.get(exp);
    const isActive = _expiryFilter === exp;
    const label = formatExpiryChip(exp);
    return `<button type="button" class="expiry-chip${isActive ? " active" : ""}" 
            data-expiry="${escapeHtml(exp)}" title="${count} position(s)">
      ${label} <span class="expiry-count">(${count})</span>
    </button>`;
  }).join("");
  
  // Add "All" chip.
  const allActive = _expiryFilter === null;
  items.innerHTML = `<button type="button" class="expiry-chip${allActive ? " active" : ""}" 
    data-expiry="">All</button>` + items.innerHTML;
  
  strip.hidden = false;
}

// Format expiry date as short label (e.g., "Jun 20").
function formatExpiryChip(isoDate) {
  try {
    const d = new Date(isoDate + "T12:00:00");
    return d.toLocaleDateString("en-US", { month: "short", day: "numeric" });
  } catch {
    return isoDate;
  }
}

// #342: pure sort helper so the column logic can be exercised without
// touching the DOM. `dir` is "asc" | "desc"; for the |net| column we
// compare absolute values so longs and shorts of equal magnitude land
// adjacent.
export function sortPositionsInPlace(rows, sort) {
  const dirMul = sort.dir === "asc" ? 1 : -1;
  if (sort.col === "symbol") {
    rows.sort((a, b) => a.symbol.localeCompare(b.symbol) * dirMul);
  } else if (sort.col === "price") {
    rows.sort((a, b) => ((Number(a.averageEntryPrice) || 0) - (Number(b.averageEntryPrice) || 0)) * dirMul);
  } else {
    // absNet (default).
    rows.sort((a, b) => (Math.abs(Number(a.netQuantity) || 0) - Math.abs(Number(b.netQuantity) || 0)) * dirMul);
  }
  return rows;
}

function syncPositionsSortHeaders() {
  const ths = document.querySelectorAll(".panel.positions th.sortable");
  ths.forEach(th => {
    const key = th.getAttribute("data-sort-key");
    if (key === _positionsSort.col) {
      th.setAttribute("aria-sort", _positionsSort.dir === "asc" ? "ascending" : "descending");
    } else {
      th.setAttribute("aria-sort", "none");
    }
  });
}

// Lazily-constructed virtualizer for the Executions log (#409). We
// reuse one controller across re-renders so the spacer/window pair
// stays in place and the scroll position is preserved between deltas.
let _execVList = null;
// Row height in px. Must match `.executions-log .exec-row { height }`
// in styles.css — the virtualizer relies on a fixed row size.
const EXEC_ROW_HEIGHT = 24;

// #408. Mobile navigation drawer instance. Constructed by bindUi once;
// setViewToggleVisible mirrors per-role visibility into it whenever
// the tablist re-renders.
let _mobileDrawer = null;

function renderExecutions() {
  const log = $("executions-log");
  if (!log) return;
  const filter = _execSymbolFilter.trim().toUpperCase();
  let items = getState().executions;
  if (filter) {
    items = items.filter(e => typeof e.symbol === "string" && e.symbol.toUpperCase().includes(filter));
  }
  // Newest first. slice() to avoid mutating state.
  const ordered = items.slice().reverse();
  if (!_execVList) {
    _execVList = createVirtualList(log, {
      rowHeight: EXEC_ROW_HEIGHT,
      overscan: 8,
      renderRow: execRow,
    });
  }
  _execVList.setItems(ordered);
}

function execRow(e) {
  const ts = new Date(e.timestampUtc).toISOString().slice(11, 23);
  const reason = e.rejectReason ? ` — ${escapeHtml(e.rejectReason)}` : "";
  const lastPx = e.lastQuantity > 0 ? ` @ ${fmtPx(e.lastPrice)}` : "";
  const lastQty = e.lastQuantity > 0 ? fmtQty(e.lastQuantity) : "";
  // Categorize STP cancels for the trader: server-driven STP from
  // the matching engine (#117) is distinct from the local pre-trade
  // reject. Both categories surface as small badges so a glance at
  // the executions log tells the trader which layer fired.
  const stpBadge = stpBadgeFor(e);
  return `<div class="exec-row">
    <span class="ts">${ts}</span>
    <span class="kind ${escapeHtml(e.kind)}">${escapeHtml(execKindLabel(e.kind))}</span>
    <span class="meta">${escapeHtml(e.clOrdId)} ${escapeHtml(e.symbol)} ${lastQty}${lastPx}${stpBadge}${reason}</span>
  </div>`;
}

function stpBadgeFor(e) {
  if (e.isNativeStp) {
    return ` <span class="badge stp-native" title="Cancelado pelo motor de matching da B3 por Self-Trade Prevention">STP servidor B3</span>`;
  }
  if (e.kind === "Rejected"
      && typeof e.rejectReason === "string"
      && e.rejectReason.startsWith("self_trade_prevention")) {
    return ` <span class="badge stp-local" title="Rejeitado pela camada local de Self-Trade Prevention antes de chegar ao gateway">STP local</span>`;
  }
  return "";
}

// Pure helper extracted from bindUi() so the visibility rules can be
// unit tested without booting the entire UI. Mutates the supplied
// elements in place.
export function applyTicketConditionalVisibility({
  type, tif,
  priceEl, priceLabel,
  stopPriceEl, stopPriceLabel,
  gtdEl, gtdLabel,
}) {
  const showPrice = type === "Limit" || type === "StopLimit" || type === "MarketWithLeftover";
  const showStop  = isStopOrderType(type);
  const showGtd   = isGtdTif(tif);

  if (priceLabel) priceLabel.hidden = !showPrice;
  if (priceEl) {
    priceEl.disabled = !showPrice;
    priceEl.required = showPrice;
    if (!showPrice) priceEl.value = "";
  }

  if (stopPriceLabel) stopPriceLabel.hidden = !showStop;
  if (stopPriceEl) {
    stopPriceEl.disabled = !showStop;
    stopPriceEl.required = showStop;
    if (!showStop) stopPriceEl.value = "";
  }

  if (gtdLabel) gtdLabel.hidden = !showGtd;
  if (gtdEl) {
    gtdEl.disabled = !showGtd;
    gtdEl.required = showGtd;
    if (!showGtd) gtdEl.value = "";
  }
}

function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => (
    { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]
  ));
}

// ── Q1.4 (#256) helpers ────────────────────────────────────────────

// Render the OrderType column in the working-orders table as a small
// colored chip ("LIM" / "MKT" / "STP" / "STPL" / "MWL"). Unknown types
// fall through to a plain escaped string so a future enum addition
// stays visible while the chip table catches up.
export function typeChipHtml(type) {
  const meta = ORDER_TYPE_CHIP[type];
  if (!meta) return escapeHtml(type ?? "");
  return `<span class="type-chip ${meta.cls}" title="${escapeHtml(type)}">${meta.label}</span>`;
}

// Render option badges ("C" for Calls, "P" for Puts) for option orders/positions.
// Returns an empty string for non-options; renders a colored badge for options.
function optionBadgeHtml(putOrCall) {
  if (!putOrCall) return "";
  const isCall = putOrCall === "Call";
  const label = isCall ? "C" : "P";
  const cls = isCall ? "option-call" : "option-put";
  const title = isCall ? "Call" : "Put";
  return ` <span class="option-badge ${cls}" title="${title}">${label}</span>`;
}

// Map ExecKind enum strings (as emitted by the backend `executions.me`
// stream) to user-friendly display labels. PR #418 split the legacy
// `Replaced` into two events: the original ClOrdID terminalises as
// `Replaced` (still labeled "Replaced") and the replacement entering
// Working surfaces as `ReplacedNew` — which would render literally as
// "ReplacedNew" without this mapping. All other kinds keep their raw
// enum spelling so a future ExecKind addition stays visible while the
// label table catches up. Returns the raw input (escape-safe) when no
// mapping is registered.
const EXEC_KIND_LABELS = Object.freeze({
  ReplacedNew: "Replacement",
});
export function execKindLabel(kind) {
  if (kind == null) return "";
  return EXEC_KIND_LABELS[kind] ?? kind;
}

// Format an ISO timestamp for the order-detail GTD field. Returns "—"
// for null/empty inputs so the renderer can call this unconditionally.
export function fmtGtd(iso) {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return escapeHtml(iso);
  // YYYY-MM-DD HH:mm UTC. Keeps the column compact and unambiguous —
  // the trader sees the venue's wall clock, no locale surprises.
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())} UTC`;
}

// Format option tooltip for hover. Returns null for non-options; for options
// returns a string like "PETR4 Call 35.00 @ 2026-06-20" (underlying putOrCall strike @ expiry).
function formatOptionTooltip(order) {
  if (order.securityType !== "Option") return null;
  const side = order.optionPutOrCall || "?";
  const strike = order.optionStrikePrice != null ? order.optionStrikePrice.toFixed(2) : "?";
  const expiry = order.optionExpirationDate || "?";
  const underlying = order.optionUnderlyingSymbol || "?";
  return `${underlying} ${side} ${strike} @ ${expiry}`;
}

// Pure validator. Returns { valid, errors } where errors is a record
// keyed by field name. Mirrors the backend Q1.1 risk pipeline subset
// the trader benefits from knowing about pre-submit; the server is
// still authoritative.
//
// Inputs are the visible form values plus the `hidden` flags so the
// validator can suppress errors for fields that aren't relevant to
// the current type/TIF combination.
export function validateTicketState(formState) {
  const {
    type, side, tif,
    price, stopPrice, goodTillDate,
    priceHidden, stopPriceHidden, gtdHidden,
    now,
    maxGtdHorizonDays,
  } = formState ?? {};
  const errors = {};

  // Stop / StopLimit require StopPrice > 0.
  if (!stopPriceHidden && isStopOrderType(type)) {
    const sp = Number(stopPrice);
    if (!Number.isFinite(sp) || sp <= 0) {
      errors.stopPrice = "stop price required";
    }
  }

  // StopLimit additionally requires Limit price; Buy ⇒ price ≥ stopPrice,
  // Sell ⇒ price ≤ stopPrice (mirrors the backend trigger semantics).
  if (type === "StopLimit") {
    const px = Number(price);
    if (!priceHidden && (!Number.isFinite(px) || px <= 0)) {
      errors.price = "limit price required";
    } else if (!stopPriceHidden && !errors.stopPrice && !errors.price) {
      const sp = Number(stopPrice);
      if (side === "Buy" && px < sp) {
        errors.price = "Buy StopLimit: price must be ≥ stop price";
      } else if (side === "Sell" && px > sp) {
        errors.price = "Sell StopLimit: price must be ≤ stop price";
      }
    }
  }

  // GTD requires goodTillDate in (now, now + maxGtdHorizonDays].
  // The horizon mirrors the backend `Trading:Risk:MaxGtdHorizon`
  // surfaced via /policy/risk; falls back to 30 days when the
  // policy fetch hasn't completed (or failed) so the UI is never
  // blocked by a slow boot.
  if (!gtdHidden && isGtdTif(tif)) {
    if (!goodTillDate) {
      errors.goodTillDate = "good-till-date required";
    } else {
      const t  = Date.parse(goodTillDate);
      const ts = typeof now === "number" ? now : Date.now();
      const horizonDays = Number.isFinite(maxGtdHorizonDays) && maxGtdHorizonDays > 0
        ? maxGtdHorizonDays
        : 30;
      const cap = ts + horizonDays * 24 * 60 * 60 * 1000;
      if (!Number.isFinite(t) || t <= ts) {
        errors.goodTillDate = "good-till-date must be in the future";
      } else if (t > cap) {
        errors.goodTillDate = `good-till-date must be within ${horizonDays} days`;
      }
    }
  }

  // IOC / FOK + MarketWithLeftover are mutually exclusive (the leftover
  // semantics contradict the immediate-or-die intent).
  if (type === "MarketWithLeftover" && (tif === "IOC" || tif === "FOK")) {
    errors.tif = "MarketWithLeftover is incompatible with IOC/FOK";
  }

  return { valid: Object.keys(errors).length === 0, errors };
}

// Read the live ticket form, run validateTicketState, render the
// inline aria-live error block, and toggle the submit-disabled flag.
function refreshTicketValidation() {
  const typeEl = $("ticket-type");
  const tifEl  = $("ticket-tif");
  const sideEl = $("ticket-side");
  const priceEl = $("ticket-price");
  const stopEl  = $("ticket-stop-price");
  const gtdEl   = $("ticket-good-till-date");
  const errEl   = $("ticket-validation");
  const submitEl = $("ticket-submit");
  if (!typeEl || !tifEl) return;

  const priceLabel = $("ticket-price-label");
  const stopLabel  = $("ticket-stop-price-label");
  const gtdLabel   = $("ticket-good-till-date-label");

  const result = validateTicketState({
    type: typeEl.value,
    side: sideEl?.value ?? "Buy",
    tif:  tifEl.value,
    price:        priceEl?.value ?? "",
    stopPrice:    stopEl?.value ?? "",
    goodTillDate: gtdEl?.value ?? "",
    priceHidden:    priceLabel ? !!priceLabel.hidden : !!priceEl?.disabled,
    stopPriceHidden: stopLabel ? !!stopLabel.hidden : !!stopEl?.disabled,
    gtdHidden:       gtdLabel  ? !!gtdLabel.hidden  : !!gtdEl?.disabled,
    now: Date.now(),
    maxGtdHorizonDays: getState().riskPolicy?.maxGtdHorizonDays,
  });

  if (errEl) {
    if (result.valid) {
      errEl.hidden = true;
      errEl.textContent = "";
    } else {
      errEl.hidden = false;
      errEl.textContent = Object.values(result.errors).join(" · ");
    }
  }
  if (submitEl) {
    if (result.valid) {
      delete submitEl.dataset.validationFailed;
    } else {
      submitEl.dataset.validationFailed = "1";
    }
    applySubmitDisabled();
  }
  return result;
}
export { refreshTicketValidation, openChainPicker, closeChainPicker, buildChainGrid, handleChainCellClick };
