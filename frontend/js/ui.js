// Render-only DOM layer. State lives in state.js; this module wires
// updates to elements and exposes onAction hooks for user gestures.

import {
  getState, subscribe, isTerminalOrderStatus,
} from "./state.js";

const $ = (id) => document.getElementById(id);

let onSubmitOrder = () => {};
let onCancelOrder = () => {};
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

// ── Session-expiry modal ───────────────────────────────────────────
let sessionModalSubmit = null;
let sessionModalLogout = null;
let sessionModalBackdrop = null;

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
  form.addEventListener("submit", sessionModalSubmit);
  logoutBtn?.addEventListener("click", sessionModalLogout);
  modal.addEventListener("click", sessionModalBackdrop);
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
}

export function setSessionModalError(message) {
  const el = $("session-modal-error");
  if (!el) return;
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false; el.textContent = message;
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

  $("logout").addEventListener("click", () => onLogout());

  // Event delegation for per-row Cancel buttons in the blotter,
  // plus row selection (clicking anywhere outside the cancel button).
  $("blotter-body").addEventListener("click", (e) => {
    const btn = e.target.closest(".cancel-btn");
    if (btn) {
      const clOrdId = btn.dataset.clordid;
      if (clOrdId) onCancelOrder(clOrdId);
      return;
    }
    const row = e.target.closest("tr[data-clordid]");
    if (row) onSelectOrder(row.dataset.clordid);
  });

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
  // text input to avoid stealing keystrokes.
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
    // Esc inside the ticket form clears it; outside, clear blotter selection.
    if (target?.closest && target.closest("#ticket-form")) {
      clearTicket();
    } else {
      onSelectOrder(null);
    }
    return;
  }
  if ((e.key === "Delete" || e.key === "Backspace") && !inEditable) {
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

export function setTicketSubmitting(submitting) {
  $("ticket-submit").disabled = !!submitting;
  $("ticket-submit").textContent = submitting ? "Submitting…" : "Submit";
}

export function clearTicket() {
  $("ticket-symbol").value = "";
  $("ticket-qty").value = "";
  $("ticket-price").value = "";
}

export function setStatusPill(status) {
  const el = $("ws-status");
  el.textContent = status;
  el.className = `status-pill status-${status}`;
  el.setAttribute("aria-label", `WebSocket: ${status}`);
}

export function setUserLabel(user) {
  $("user-label").textContent = user ? `${user.username}` : "";
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
// countdown). Started lazily on first render so SSR / non-browser hosts
// stay quiet.
let tickTimer = null;
function ensureTicker() {
  if (tickTimer) return;
  tickTimer = setInterval(() => {
    renderInflight();
    renderReconnect();
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

function renderForSlice(slice) {
  if (slice === "orders" || slice === "all" || slice === "blotterFilter" || slice === "blotterPage" || slice === "selectedOrder") renderBlotter();
  if (slice === "positions" || slice === "all") renderPositions();
  if (slice === "executions" || slice === "all") renderExecutions();
  if (slice === "status") {
    setStatusPill(getState().status);
    renderReconnect(); // pill change usually correlates with countdown reset
  }
  if (slice === "user")   setUserLabel(getState().user);
  if (slice === "marketData" || slice === "all") renderMarketData();
  if (slice === "marketDataStatus") setMdStatusPill(getState().marketDataStatus);
  if (slice === "submitInflight") renderInflight();
  if (slice === "wsReconnect") renderReconnect();
  if (slice === "firmsHealth" || slice === "all") renderFirmsHealth();
  if (slice === "currentView" || slice === "all") applyCurrentView(getState().currentView);
  if (slice === "watchlist" || slice === "selectedSymbol" || slice === "all") renderSelectedSymbol();
  if (slice === "watchlist" || slice === "selectedSymbol" || slice === "book" || slice === "all") renderDob();
  if (slice === "watchlist" || slice === "selectedSymbol" || slice === "chartResolution" || slice === "candles" || slice === "all") scheduleChartRender();
  if (slice === "watchlist" || slice === "selectedSymbol" || slice === "tapeShowAll" || slice === "tape" || slice === "all") scheduleTapeRender();
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
  body.innerHTML = rows.map(symbol => {
    const e = md.get(symbol);
    if (!e || e.lastPrice == null) {
      return `<tr><td>${escapeHtml(symbol)}</td><td colspan="4" class="muted-cell">awaiting data…</td></tr>`;
    }
    const ts = e.updatedAt ? new Date(e.updatedAt).toISOString().slice(11, 19) : "—";
    return `<tr>
      <td>${escapeHtml(symbol)}</td>
      <td class="num">${Number(e.lastPrice).toFixed(2)}</td>
      <td class="num">${e.lastQty ?? "—"}</td>
      <td class="num">${e.lastTradeId ?? "—"}</td>
      <td>${ts}</td>
    </tr>`;
  }).join("");
}

const DOB_TOP_N = 10;

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
    bidsBody.innerHTML = `<tr><td colspan="3" class="muted-cell">awaiting book snapshot…</td></tr>`;
    asksBody.innerHTML = `<tr><td colspan="3" class="muted-cell">awaiting book snapshot…</td></tr>`;
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
    const price = lv.price.toFixed(2);
    const qty = lv.qty;
    if (side === "bid") {
      return `<tr><td class="num">${cum}</td><td class="num">${qty}</td><td class="num">${price}</td></tr>`;
    }
    return `<tr><td class="num">${price}</td><td class="num">${qty}</td><td class="num">${cum}</td></tr>`;
  }).join("");
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
  const ts = new Date(e.receivedAt).toISOString().slice(11, 19);
  const arrow = e.side === "up" ? "▲" : e.side === "down" ? "▼" : "·";
  return `<li class="${cls}">`
    + `<span>${ts}</span>`
    + `<span>${escapeHtml(e.symbol)}</span>`
    + `<span class="tape-num">${arrow} ${Number(e.price).toFixed(2)}</span>`
    + `<span class="tape-num">${e.qty}</span>`
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
  const price = o.price == null ? "—" : Number(o.price).toFixed(2);
  const highlightAt = st.ordersHighlight?.get(o.clOrdId);
  const fresh = highlightAt && (Date.now() - highlightAt) < HIGHLIGHT_MS;
  const selected = st.selectedClOrdId === o.clOrdId;
  const cls = [fresh ? "row-fresh" : "", selected ? "row-selected" : ""].filter(Boolean).join(" ");
  return `<tr data-clordid="${escapeHtml(o.clOrdId)}"${cls ? ` class="${cls}"` : ""}>
    <td><code>${escapeHtml(o.clOrdId)}</code></td>
    <td>${escapeHtml(o.symbol)}</td>
    <td>${escapeHtml(o.side)}</td>
    <td>${escapeHtml(o.type)}</td>
    <td class="num">${o.quantity}</td>
    <td class="num">${o.leavesQuantity}</td>
    <td class="num">${o.cumulativeQuantity}</td>
    <td class="num">${price}</td>
    <td class="status-cell-${escapeHtml(o.status)}">${escapeHtml(o.status)}</td>
    <td><button class="cancel-btn" data-clordid="${escapeHtml(o.clOrdId)}" aria-label="Cancel order ${escapeHtml(o.clOrdId)}" ${terminal ? "disabled" : ""}>Cancel</button></td>
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
        <td class="num">${p.netQuantity}</td>
        <td class="num">${Number(p.averageEntryPrice).toFixed(2)}</td>
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
  const lastPx = e.lastQuantity > 0 ? ` @ ${Number(e.lastPrice).toFixed(2)}` : "";
  return `<li>
    <span class="ts">${ts}</span>
    <span class="kind ${escapeHtml(e.kind)}">${escapeHtml(e.kind)}</span>
    <span class="meta">${escapeHtml(e.clOrdId)} ${escapeHtml(e.symbol)} ${e.lastQuantity || ""}${lastPx}${reason}</span>
  </li>`;
}

function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => (
    { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]
  ));
}
