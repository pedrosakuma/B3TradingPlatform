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

export function setHandlers(handlers) {
  onSubmitOrder = handlers.onSubmitOrder ?? onSubmitOrder;
  onCancelOrder = handlers.onCancelOrder ?? onCancelOrder;
  onLogout      = handlers.onLogout      ?? onLogout;
  onApplyMd     = handlers.onApplyMd     ?? onApplyMd;
  onSwitchView  = handlers.onSwitchView  ?? onSwitchView;
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
  }
}

export function setLoginError(message) {
  const el = $("login-error");
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

  // Event delegation for per-row Cancel buttons in the blotter.
  $("blotter-body").addEventListener("click", (e) => {
    const btn = e.target.closest(".cancel-btn");
    if (!btn) return;
    const clOrdId = btn.dataset.clordid;
    if (clOrdId) onCancelOrder(clOrdId);
  });

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

  subscribe(renderForSlice);
  renderAll();
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
  el.className = `feedback ${kind === "ok" ? "ok" : "error"}`;
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
  if (slice === "orders" || slice === "all") renderBlotter();
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
}

function renderAll() {
  renderBlotter();
  renderPositions();
  renderExecutions();
  setStatusPill(getState().status);
  setUserLabel(getState().user);
  renderMarketData();
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
    body.innerHTML = `<tr><td colspan="5" style="color:var(--muted);text-align:center;padding:1rem">No subscriptions</td></tr>`;
    return;
  }
  body.innerHTML = rows.map(symbol => {
    const e = md.get(symbol);
    if (!e || e.lastPrice == null) {
      return `<tr><td>${escapeHtml(symbol)}</td><td colspan="4" style="color:var(--muted)">awaiting data…</td></tr>`;
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

function renderBlotter() {
  const body = $("blotter-body");
  const orders = [...getState().orders.values()]
    .sort((a, b) => a.clOrdId.localeCompare(b.clOrdId));
  $("blotter-count").textContent = orders.length.toString();
  body.innerHTML = orders.map(orderRow).join("");
}

function orderRow(o) {
  const terminal = isTerminalOrderStatus(o.status);
  const price = o.price == null ? "—" : Number(o.price).toFixed(2);
  return `<tr>
    <td><code>${escapeHtml(o.clOrdId)}</code></td>
    <td>${escapeHtml(o.symbol)}</td>
    <td>${escapeHtml(o.side)}</td>
    <td>${escapeHtml(o.type)}</td>
    <td class="num">${o.quantity}</td>
    <td class="num">${o.leavesQuantity}</td>
    <td class="num">${o.cumulativeQuantity}</td>
    <td class="num">${price}</td>
    <td class="status-cell-${escapeHtml(o.status)}">${escapeHtml(o.status)}</td>
    <td><button class="cancel-btn" data-clordid="${escapeHtml(o.clOrdId)}" ${terminal ? "disabled" : ""}>Cancel</button></td>
  </tr>`;
}

function renderPositions() {
  const body = $("positions-body");
  const positions = [...getState().positions.values()]
    .filter(p => p.netQuantity !== 0)
    .sort((a, b) => a.symbol.localeCompare(b.symbol));
  body.innerHTML = positions.length === 0
    ? `<tr><td colspan="3" style="color:var(--muted);text-align:center;padding:1rem">No positions</td></tr>`
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
