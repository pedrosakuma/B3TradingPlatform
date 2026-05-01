// Render-only DOM layer. State lives in state.js; this module wires
// updates to elements and exposes onAction hooks for user gestures.

import {
  getState, subscribe, isTerminalOrderStatus,
} from "./state.js";

const $ = (id) => document.getElementById(id);

let onSubmitOrder = () => {};
let onCancelOrder = () => {};
let onLogout      = () => {};

export function setHandlers(handlers) {
  onSubmitOrder = handlers.onSubmitOrder ?? onSubmitOrder;
  onCancelOrder = handlers.onCancelOrder ?? onCancelOrder;
  onLogout      = handlers.onLogout      ?? onLogout;
}

export function showLogin() {
  $("login-view").hidden = false;
  $("trader-view").hidden = true;
}

export function showTrader() {
  $("login-view").hidden = true;
  $("trader-view").hidden = false;
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

  subscribe(renderForSlice);
  renderAll();
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
}

function renderForSlice(slice) {
  if (slice === "orders" || slice === "all") renderBlotter();
  if (slice === "positions" || slice === "all") renderPositions();
  if (slice === "executions" || slice === "all") renderExecutions();
  if (slice === "status") setStatusPill(getState().status);
  if (slice === "user")   setUserLabel(getState().user);
}

function renderAll() {
  renderBlotter();
  renderPositions();
  renderExecutions();
  setStatusPill(getState().status);
  setUserLabel(getState().user);
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
