// Render-only DOM layer. State lives in state.js; this module wires
// updates to elements and exposes onAction hooks for user gestures.

import {
  getState, subscribe, isTerminalOrderStatus,
} from "./state.js";
import { rulesFor } from "./validation.js";

const $ = (id) => document.getElementById(id);

// ── Number formatting (pt-BR) ──────────────────────────────────────
// B3 traders expect Brazilian locale (`100.000,00`) for quantities,
// prices and notionals. Centralised here so every panel stays in sync
// and we have a single place to flip the locale if the product call
// changes later.
const _qtyFmt = new Intl.NumberFormat("pt-BR", { maximumFractionDigits: 0 });
const _pxFmt  = new Intl.NumberFormat("pt-BR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
function fmtQty(n) {
  if (n == null || n === "" || Number.isNaN(Number(n))) return "—";
  return _qtyFmt.format(Number(n));
}
function fmtPx(n) {
  if (n == null || n === "" || Number.isNaN(Number(n))) return "—";
  return _pxFmt.format(Number(n));
}
export { fmtQty, fmtPx };

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
  $("trader-view").hidden = true;
  $("admin-view").hidden = true;
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
  $("trader-view").hidden = false;
  $("admin-view").hidden = true;
}

function setViewToggleVisible(visible, current) {
  const wrap = $("view-toggle");
  if (!wrap) return;
  wrap.hidden = !visible;
  for (const btn of wrap.querySelectorAll("button[data-view]")) {
    btn.classList.toggle("active", btn.dataset.view === current);
    btn.setAttribute("aria-selected", btn.dataset.view === current ? "true" : "false");
  }
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
}

export function setSessionModalError(message) {
  const el = $("session-modal-error");
  if (!el) return;
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false; el.textContent = message;
}

// ── Modify-order modal (slice 5 of #122) ───────────────────────────

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
  const error = $("modify-modal-error");
  if (!modal || !form || !qty) return;

  form.dataset.clordid = clOrdId;
  qty.value = order.quantity ?? "";
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
  if (error) { error.hidden = true; error.textContent = ""; }
  modal.hidden = false;
  // Focus the qty field so keyboard-only operators don't need a
  // round-trip through the mouse to change the size.
  setTimeout(() => qty.focus(), 0);
}

export function closeModifyModal() {
  const modal = $("modify-modal");
  const form  = $("modify-modal-form");
  if (!modal) return;
  modal.hidden = true;
  if (form) delete form.dataset.clordid;
  setModifyModalError(null);
  setModifyModalSubmitting(false);
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

function submitModifyForm() {
  const form  = $("modify-modal-form");
  const qtyEl = $("modify-modal-qty");
  const pxEl  = $("modify-modal-price");
  if (!form || !qtyEl) return;
  const clOrdId = form.dataset.clordid;
  if (!clOrdId) return;

  const qtyRaw = qtyEl.value.trim();
  const qty = Number(qtyRaw);
  if (!Number.isFinite(qty) || qty <= 0 || !Number.isInteger(qty)) {
    setModifyModalError("Quantity must be a positive integer.");
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
  onModifyOrder(clOrdId, { quantity: qty, price });
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

export function bindUi() {
  // Order ticket: enable/disable price field by type.
  const typeEl = $("ticket-type");
  const priceEl = $("ticket-price");
  const syncPriceField = () => {
    const isMarket = typeEl.value === "Market";
    priceEl.disabled = isMarket;
    priceEl.required = !isMarket;
    if (isMarket) priceEl.value = "";
  };
  typeEl.addEventListener("change", syncPriceField);
  syncPriceField();

  $("ticket-form").addEventListener("submit", (e) => {
    e.preventDefault();
    const payload = {
      symbol: $("ticket-symbol").value.trim().toUpperCase(),
      side:   $("ticket-side").value,
      type:   $("ticket-type").value,
      quantity: Number($("ticket-qty").value),
      price: priceEl.disabled || priceEl.value === "" ? null : Number(priceEl.value),
    };
    onSubmitOrder(payload);
  });

  // Auto-uppercase the symbol field as the trader types so the visual
  // matches what we actually submit (we already toUpperCase on submit).
  const symEl = $("ticket-symbol");
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
      syncTicketRules();
    });
    symEl.addEventListener("compositionend", (e) => {
      const upper = e.target.value.toUpperCase();
      if (e.target.value !== upper) e.target.value = upper;
      syncTicketRules();
    });
    symEl.addEventListener("change", syncTicketRules);
    // Initial paint so the hint reflects defaults before any input.
    syncTicketRules();
  }

  $("logout").addEventListener("click", () => onLogout());

  // Event delegation for per-row Cancel + Modify buttons in the
  // blotter, plus row selection (clicking anywhere outside the
  // action buttons).
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
    if (row) onSelectOrder(row.dataset.clordid);
  });

  // Modify modal wiring (slice 5 of #122).
  const modifyForm = $("modify-modal-form");
  const modifyCancelBtn = $("modify-modal-cancel");
  if (modifyForm) {
    modifyForm.addEventListener("submit", (e) => {
      e.preventDefault();
      submitModifyForm();
    });
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

  // Blotter filter: text + status select. Persisted via app.js.
  const filterText = $("blotter-filter-text");
  const filterStatus = $("blotter-filter-status");
  const fireFilter = () => onBlotterFilter({
    text:   filterText.value,
    status: filterStatus.value,
  });
  if (filterText)   filterText.addEventListener("input",  fireFilter);
  if (filterStatus) filterStatus.addEventListener("change", fireFilter);

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

  // Market data settings popover (⚙ button in MD panel header).
  const mdModal = $("md-settings-modal");
  const mdOpen = $("md-settings-open");
  const mdClose = $("md-settings-close");
  if (mdOpen && mdModal) {
    mdOpen.addEventListener("click", () => {
      mdModal.hidden = false;
      const urlInput = $("md-url");
      if (urlInput) urlInput.focus();
    });
  }
  if (mdClose && mdModal) {
    mdClose.addEventListener("click", () => { mdModal.hidden = true; });
  }
  if (mdModal) {
    mdModal.addEventListener("click", (e) => {
      if (e.target === mdModal) mdModal.hidden = true;
    });
    // Esc closes the popover (non-destructive — no logout).
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && !mdModal.hidden) {
        e.preventDefault();
        mdModal.hidden = true;
      }
    });
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

  // Global keyboard shortcuts:
  //   F2  → focus the order-ticket symbol input
  //   Esc → clear the ticket form (when focus is inside it)
  //   Del → cancel the currently-selected blotter row
  // We deliberately ignore key events when the user is typing into a
  // text input to avoid stealing keystrokes. Backspace is intentionally
  // NOT a cancel shortcut — too easy to fire accidentally.
  document.addEventListener("keydown", onGlobalKeydown);

  subscribe(renderForSlice);
  renderAll();
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
      clearTicket();
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

// Clear the ticket feedback only if it still shows `expected`. Used by
// the success-toast auto-dismiss so a later warning/error message that
// landed before the timer fires isn't accidentally erased.
export function setTicketFeedbackIfMatches(expected, replacement) {
  const el = $("ticket-feedback");
  if (!el || el.hidden) return;
  if (el.textContent !== expected) return;
  setTicketFeedback(replacement, null);
}

export function setTicketSubmitting(submitting) {
  $("ticket-submit").disabled = !!submitting;
  $("ticket-submit").textContent = submitting ? "Submitting…" : "Submit";
}

export function clearTicket() {
  $("ticket-symbol").value = "";
  $("ticket-qty").value = "";
  $("ticket-price").value = "";
  syncTicketRules();
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
  // Toggle visibility of the trader/admin view switch based on role.
  const isAdmin = user?.role === "admin";
  setViewToggleVisible(isAdmin, getState().currentView);
}

function applyCurrentView(view) {
  const trader = $("trader-view");
  const admin = $("admin-view");
  if (!trader || !admin) return;
  if (view === "admin") {
    trader.hidden = true;
    admin.hidden = false;
  } else {
    trader.hidden = false;
    admin.hidden = true;
  }
  setViewToggleVisible(getState().user?.role === "admin", view);
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
    renderReconnect();
    // Slow tick (1s) for time-driven re-renders that don't need 4 Hz.
    const now = Date.now();
    if (now - lastSlowTick >= 1000) {
      lastSlowTick = now;
      renderMarketData();
      // Only re-render the DOB while we're still waiting for a snapshot
      // — the live render path is already wired to the book slice.
      const st = getState();
      const entry = st.selectedSymbol ? st.book.get(st.selectedSymbol) : null;
      if (st.selectedSymbol && (!entry || !entry.ready)) renderDob();
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
  const remaining = Math.max(0, r.nextAt - Date.now());
  el.hidden = false;
  el.textContent = `retry in ${(remaining / 1000).toFixed(1)}s`;
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

function renderForSlice(slice) {
  if (slice === "orders" || slice === "all" || slice === "blotterFilter" || slice === "blotterPage" || slice === "selectedOrder") renderBlotter();
  if (slice === "orders" || slice === "all") renderCancelAllButton();
  if (slice === "positions" || slice === "all") renderPositions();
  if (slice === "executions" || slice === "all") renderExecutions();
  if (slice === "status") {
    setStatusPill(getState().status);
    renderReconnect(); // pill change usually correlates with countdown reset
    renderStaleness("ws");
  }
  if (slice === "user")   setUserLabel(getState().user);
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
  return new Date(ms).toLocaleTimeString("pt-BR", { hour12: false });
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
      return `<tr><td>${escapeHtml(symbol)}</td><td colspan="4" class="muted-cell">awaiting data…</td></tr>`;
    }
    const ts = e.updatedAt ? new Date(e.updatedAt).toISOString().slice(11, 19) : "—";
    const stale = e.updatedAt && (now - e.updatedAt) > MD_STALE_MS;
    const tsCls = stale ? ' class="md-cell-stale"' : "";
    return `<tr>
      <td>${escapeHtml(symbol)}</td>
      <td class="num">${fmtPx(e.lastPrice)}</td>
      <td class="num">${fmtQty(e.lastQty)}</td>
      <td class="num">${e.lastTradeId ?? "—"}</td>
      <td${tsCls}>${ts}</td>
    </tr>`;
  }).join("");
}

const DOB_TOP_N = 10;
const DOB_NO_BOOK_AFTER_MS = 10_000;

function renderDob() {
  const bidsBody = document.querySelector("#dob-bids tbody");
  const asksBody = document.querySelector("#dob-asks tbody");
  const feedback = $("dob-feedback");
  if (!bidsBody || !asksBody) return;

  const st = getState();
  const current = st.selectedSymbol;

  if (!current) {
    bidsBody.innerHTML = `<tr><td colspan="3" class="muted-cell">select a symbol</td></tr>`;
    asksBody.innerHTML = `<tr><td colspan="3" class="muted-cell">select a symbol</td></tr>`;
    if (feedback) { feedback.hidden = true; feedback.textContent = ""; }
    return;
  }

  const entry = st.book.get(current);
  if (!entry || !entry.ready) {
    // After ~10s without a snapshot, swap the soft "awaiting…" copy for
    // a louder hint that something is wrong with the MD subscription
    // (most commonly: MBP not enabled or the URL is mistyped).
    const waited = st.selectedSymbolSetAt ? Date.now() - st.selectedSymbolSetAt : 0;
    const msg = waited > DOB_NO_BOOK_AFTER_MS
      ? "no book — check MD settings ⚙"
      : "awaiting book snapshot…";
    bidsBody.innerHTML = `<tr><td colspan="3" class="muted-cell">${msg}</td></tr>`;
    asksBody.innerHTML = `<tr><td colspan="3" class="muted-cell">${msg}</td></tr>`;
    if (feedback) { feedback.hidden = true; feedback.textContent = ""; }
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
  if (!entry || !entry.ready || entry.bars.length === 0) {
    showEmpty("awaiting candle snapshot…");
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
  const filter = st.blotterFilter ?? { text: "", status: "" };
  syncFilterInputs(filter);
  const search = filter.text.trim().toUpperCase();
  const wantStatus = filter.status;
  const all = [...st.orders.values()];
  // Default sort: newest-first by per-ClOrdID arrival sequence
  // (assigned in state.applyOrders*). Falling back to clOrdId keeps
  // ordering deterministic if seq is missing for any reason.
  const seqOf = (o) => st.orderSeq?.get(o.clOrdId) ?? 0;
  const filtered = all
    .filter(o => !search || o.symbol.toUpperCase().includes(search) || o.clOrdId.toUpperCase().includes(search))
    .filter(o => !wantStatus || o.status === wantStatus)
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
  const cls = [fresh ? "row-fresh" : "", selected ? "row-selected" : ""].filter(Boolean).join(" ");
  const cancelDisabled = terminal || cancelInflight || modifyInflight;
  const cancelLabel = cancelInflight ? "Cancelling…" : "Cancel";
  const cancelCls = "cancel-btn" + (cancelInflight ? " cancelling" : "");
  // Slice 5 of #122. Modify button shares row-selection delegation
  // (data-clordid on the button so the click handler can map it back
  // to the order without walking the row). Disabled while terminal
  // or while either a cancel or another modify is in flight — the
  // backend's in-flight guard would 409 a second modify anyway, but
  // gating client-side avoids the round-trip and keeps the UX honest.
  const modifyDisabled = terminal || modifyInflight || cancelInflight;
  const modifyLabel = modifyInflight ? "Modifying…" : "Modify";
  const modifyCls = "modify-btn" + (modifyInflight ? " modifying" : "");
  return `<tr data-clordid="${escapeHtml(o.clOrdId)}"${cls ? ` class="${cls}"` : ""}>
    <td><code>${escapeHtml(o.clOrdId)}</code></td>
    <td>${escapeHtml(o.symbol)}</td>
    <td>${escapeHtml(o.side)}</td>
    <td>${escapeHtml(o.type)}</td>
    <td class="num">${fmtQty(o.quantity)}</td>
    <td class="num">${fmtQty(o.leavesQuantity)}</td>
    <td class="num">${fmtQty(o.cumulativeQuantity)}</td>
    <td class="num">${price}</td>
    <td class="status-cell-${escapeHtml(o.status)}">${escapeHtml(o.status)}</td>
    <td><button class="${modifyCls}" data-clordid="${escapeHtml(o.clOrdId)}" aria-label="Modify order ${escapeHtml(o.clOrdId)}" title="Modify" ${modifyDisabled ? "disabled" : ""}>${modifyLabel}</button></td>
    <td><button class="${cancelCls}" data-clordid="${escapeHtml(o.clOrdId)}" aria-label="Cancel order ${escapeHtml(o.clOrdId)}" title="Cancel (Del)" ${cancelDisabled ? "disabled" : ""}>${cancelLabel}</button></td>
  </tr>`;
}

function syncFilterInputs(filter) {
  const t = $("blotter-filter-text");
  const s = $("blotter-filter-status");
  if (t && document.activeElement !== t) t.value = filter.text;
  if (s && s.value !== filter.status) s.value = filter.status;
}

function renderPositions() {
  const body = $("positions-body");
  const positions = [...getState().positions.values()]
    .filter(p => p.netQuantity !== 0)
    .sort((a, b) => a.symbol.localeCompare(b.symbol));
  body.innerHTML = positions.length === 0
    ? `<tr><td colspan="3" class="muted">No positions</td></tr>`
    : positions.map(p => `<tr>
        <td>${escapeHtml(p.symbol)}</td>
        <td class="num">${fmtQty(p.netQuantity)}</td>
        <td class="num">${fmtPx(p.averageEntryPrice)}</td>
      </tr>`).join("");
}

function renderExecutions() {
  const log = $("executions-log");
  const items = getState().executions;
  // Newest first.
  log.innerHTML = items.slice().reverse().map(execRow).join("");
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
  return `<li>
    <span class="ts">${ts}</span>
    <span class="kind ${escapeHtml(e.kind)}">${escapeHtml(e.kind)}</span>
    <span class="meta">${escapeHtml(e.clOrdId)} ${escapeHtml(e.symbol)} ${lastQty}${lastPx}${stpBadge}${reason}</span>
  </li>`;
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

function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => (
    { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]
  ));
}
