// Q2.6 (#273). History tab + P&L panel + Statement download UI.
//
// Sibling top-level view (toggled by app.js via state.currentView) that
// hosts three independent panels:
//
//   1. P&L panel — totals + per-symbol table. Initial values are seeded
//      by GET /api/pnl/today; the `pnl.me` WS channel delivers live updates
//      (worker.js subscribes statically, state.applyPnlSnapshot/Delta
//      replaces wholesale). This module is render-only — the slice
//      reducers live in state.js.
//
//   2. Statement download — date picker + "Download CSV" + "View JSON".
//      CSV path goes through downloadStatementCsv() (blob + a synthetic
//      anchor click, filename pulled from Content-Disposition). JSON
//      opens a modal with formatted text.
//
//   3. History tab — orders + executions tables with a shared date /
//      symbol filter bar and cursor-based "load more" pagination.
//
// Tests cover the pure pieces (filename parser, state reducers) and
// the renderers via dom-stub.mjs — same pattern as bot-credentials.

import { getState, subscribe } from "./state.js";
import { fmtQty, fmtPx, execKindLabel } from "./ui.js";
import { formatSignedCurrency, formatUtcDateTime } from "./formatters.js";

const $ = (id) => document.getElementById(id);

let onOpenView         = () => {};
let onBack             = () => {};
let onRefresh          = () => {};
let onApplyFilters     = () => {};
let onLoadMoreOrders   = () => {};
let onLoadMoreExecs    = () => {};
let onDownloadCsv      = () => {};
let onViewJson         = () => {};

export function setHistoryHandlers(h = {}) {
  onOpenView         = h.onOpenView         ?? onOpenView;
  onBack             = h.onBack             ?? onBack;
  onRefresh          = h.onRefresh          ?? onRefresh;
  onApplyFilters     = h.onApplyFilters     ?? onApplyFilters;
  onLoadMoreOrders   = h.onLoadMoreOrders   ?? onLoadMoreOrders;
  onLoadMoreExecs    = h.onLoadMoreExecs    ?? onLoadMoreExecs;
  onDownloadCsv      = h.onDownloadCsv      ?? onDownloadCsv;
  onViewJson         = h.onViewJson         ?? onViewJson;
}

export function bindHistoryUi() {
  $("history-open")?.addEventListener("click", () => onOpenView());
  $("history-back")?.addEventListener("click", () => onBack());
  $("history-refresh")?.addEventListener("click", () => onRefresh());

  $("history-filters")?.addEventListener("submit", (e) => {
    e.preventDefault();
    onApplyFilters(readFiltersFromDom());
  });

  $("history-orders-more")?.addEventListener("click", () => onLoadMoreOrders());
  $("history-executions-more")?.addEventListener("click", () => onLoadMoreExecs());

  $("statement-form")?.addEventListener("submit", (e) => {
    e.preventDefault();
    const date = ($("statement-date")?.value || "").trim();
    onDownloadCsv(date || todayDayKey());
  });
  $("statement-view-json-btn")?.addEventListener("click", () => {
    const date = ($("statement-date")?.value || "").trim();
    onViewJson(date || todayDayKey());
  });
  $("statement-json-close")?.addEventListener("click", closeStatementJsonModal);

  // Default the statement date input to today (UTC) so the operator
  // hits Download without first picking a date.
  const dateInput = $("statement-date");
  if (dateInput && !dateInput.value) dateInput.value = todayDayKey();

  // Re-render the panels on any history / pnl / statement slice change.
  subscribe((slice) => {
    if (slice === "pnl"     || slice === "all") renderPnl();
    if (slice === "history" || slice === "all") renderHistory();
    if (slice === "statement") renderStatement();
  });
  renderPnl();
  renderHistory();
  renderStatement();
}

function readFiltersFromDom() {
  return {
    from:   ($("history-from")?.value   || "").trim(),
    to:     ($("history-to")?.value     || "").trim(),
    symbol: ($("history-symbol")?.value || "").trim().toUpperCase(),
  };
}

// Q2.6 (#273). YYYY-MM-DD in UTC. The backend dayKey is UTC-anchored
// (see StatementEndpoints.TryResolveDay), so defaulting from local
// time would silently shift days near the user's midnight.
export function todayDayKey() {
  const d = new Date();
  const y = d.getUTCFullYear();
  const m = String(d.getUTCMonth() + 1).padStart(2, "0");
  const day = String(d.getUTCDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

// ── Feedback ───────────────────────────────────────────────────────

export function setHistoryFeedback(message, kind) {
  const el = $("history-feedback");
  if (!el) return;
  if (!message) {
    el.hidden = true;
    el.textContent = "";
    el.className = "feedback";
    return;
  }
  el.hidden = false;
  el.textContent = message;
  el.className = `feedback feedback-${kind ?? "ok"}`;
}

// ── P&L panel ──────────────────────────────────────────────────────

function fmtSigned(n) {
  return formatSignedCurrency(n);
}

function pnlToneClass(v) {
  if (v == null || Number.isNaN(Number(v)) || Number(v) === 0) return "pnl-zero";
  return Number(v) > 0 ? "pnl-pos" : "pnl-neg";
}

export function renderPnl() {
  const pnl = getState().pnl;
  const totalR = $("pnl-total-realized");
  const totalU = $("pnl-total-unrealized");
  const live   = $("pnl-live");
  const body   = $("pnl-rows");
  if (!body) return;

  if (!pnl) {
    if (totalR) { totalR.textContent = "—"; totalR.className = "pnl-total-value"; }
    if (totalU) { totalU.textContent = "—"; totalU.className = "pnl-total-value"; }
    if (live) live.hidden = true;
    body.innerHTML = `<tr><td colspan="6" class="muted">no P&amp;L data yet</td></tr>`;
    return;
  }

  if (totalR) {
    totalR.textContent = fmtSigned(pnl.totalRealized);
    totalR.className = `pnl-total-value ${pnlToneClass(pnl.totalRealized)}`;
  }
  if (totalU) {
    totalU.textContent = fmtSigned(pnl.totalUnrealized);
    totalU.className = `pnl-total-value ${pnlToneClass(pnl.totalUnrealized)}`;
  }
  if (live) live.hidden = false;

  // Merge per-symbol realized + unrealized into one table view. Keyed
  // by symbol; unrealized side carries position/avg/ref; realized
  // side carries the day's realized value.
  const bySym = new Map();
  for (const u of pnl.unrealized || []) {
    bySym.set(u.symbol, {
      symbol: u.symbol,
      position: u.position, avgPrice: u.avgPrice, refPrice: u.refPrice,
      unrealized: u.value, realized: null,
    });
  }
  for (const r of pnl.realized || []) {
    const row = bySym.get(r.symbol) || {
      symbol: r.symbol, position: null, avgPrice: null, refPrice: null,
      unrealized: null, realized: null,
    };
    row.realized = r.value;
    bySym.set(r.symbol, row);
  }

  if (bySym.size === 0) {
    body.innerHTML = `<tr><td colspan="6" class="muted">flat — no open positions or realized P&amp;L today</td></tr>`;
    return;
  }

  const rows = [...bySym.values()].sort((a, b) => a.symbol.localeCompare(b.symbol));
  body.innerHTML = rows.map(r => `
    <tr>
      <td>${escapeHtml(r.symbol)}</td>
      <td class="num">${r.position == null ? "—" : fmtQty(r.position)}</td>
      <td class="num">${r.avgPrice == null ? "—" : fmtPx(r.avgPrice)}</td>
      <td class="num">${r.refPrice == null ? "—" : fmtPx(r.refPrice)}</td>
      <td class="num ${pnlToneClass(r.unrealized)}">${r.unrealized == null ? "—" : fmtSigned(r.unrealized)}</td>
      <td class="num ${pnlToneClass(r.realized)}">${r.realized == null ? "—" : fmtSigned(r.realized)}</td>
    </tr>`).join("");
}

// ── History tab ────────────────────────────────────────────────────

function fmtTs(ts) {
  return formatUtcDateTime(ts, { fallback: String(ts ?? "—") });
}

export function renderHistory() {
  const st = getState();

  const oBody = $("history-orders-body");
  if (oBody) {
    const items = st.historyOrders?.items ?? [];
    if (items.length === 0) {
      oBody.innerHTML = `<tr><td colspan="10" class="muted">${
        st.historyOrders?.loading ? "loading…" : "no orders loaded"
      }</td></tr>`;
    } else {
      oBody.innerHTML = items.map(orderRow).join("");
    }
  }
  const oMore = $("history-orders-more");
  if (oMore) {
    const hasMore = !!st.historyOrders?.nextCursor;
    oMore.hidden = !hasMore;
    oMore.disabled = !!st.historyOrders?.loading;
    oMore.textContent = st.historyOrders?.loading ? "Loading…" : "Load more orders";
  }

  const eBody = $("history-executions-body");
  if (eBody) {
    const items = st.historyExecutions?.items ?? [];
    if (items.length === 0) {
      eBody.innerHTML = `<tr><td colspan="9" class="muted">${
        st.historyExecutions?.loading ? "loading…" : "no executions loaded"
      }</td></tr>`;
    } else {
      eBody.innerHTML = items.map(executionRow).join("");
    }
  }
  const eMore = $("history-executions-more");
  if (eMore) {
    const hasMore = !!st.historyExecutions?.nextCursor;
    eMore.hidden = !hasMore;
    eMore.disabled = !!st.historyExecutions?.loading;
    eMore.textContent = st.historyExecutions?.loading ? "Loading…" : "Load more executions";
  }
}

function orderRow(o) {
  return `<tr>
    <td>${escapeHtml(fmtTs(o.lastUpdatedAtUtc ?? o.createdAtUtc))}</td>
    <td>${escapeHtml(o.clOrdId ?? "")}</td>
    <td>${escapeHtml(o.symbol ?? "")}</td>
    <td>${escapeHtml(o.side ?? "")}</td>
    <td>${escapeHtml(o.type ?? "")}</td>
    <td class="num">${fmtQty(o.quantity)}</td>
    <td class="num">${fmtQty(o.cumulativeQuantity)}</td>
    <td class="num">${o.price == null ? "—" : fmtPx(o.price)}</td>
    <td>${escapeHtml(o.status ?? "")}</td>
    <td>${escapeHtml(o.timeInForce ?? "")}</td>
  </tr>`;
}

function executionRow(e) {
  return `<tr>
    <td>${escapeHtml(fmtTs(e.timestampUtc))}</td>
    <td>${escapeHtml(e.clOrdId ?? "")}</td>
    <td>${escapeHtml(e.symbol ?? "")}</td>
    <td>${escapeHtml(e.side ?? "")}</td>
    <td>${escapeHtml(execKindLabel(e.kind))}</td>
    <td class="num">${fmtQty(e.lastQuantity)}</td>
    <td class="num">${e.lastPrice == null ? "—" : fmtPx(e.lastPrice)}</td>
    <td class="num">${fmtQty(e.cumulativeQuantity)}</td>
    <td>${escapeHtml(e.rejectReason ?? "")}</td>
  </tr>`;
}

// ── Statement ──────────────────────────────────────────────────────

export function renderStatement() {
  const s = getState().statement;
  const status = $("statement-status");
  if (!status) return;
  if (s?.busy) {
    status.hidden = false;
    status.textContent = "working…";
    return;
  }
  if (s?.error) {
    status.hidden = false;
    status.textContent = s.error;
    return;
  }
  if (s?.lastDownload) {
    status.hidden = false;
    status.textContent = `downloaded ${s.lastDownload.filename}`;
    return;
  }
  status.hidden = true;
  status.textContent = "";
}

export function openStatementJsonModal(json) {
  const modal = $("statement-json-modal");
  const body  = $("statement-json-body");
  if (!modal || !body) return;
  try {
    body.textContent = JSON.stringify(json, null, 2);
  } catch {
    body.textContent = String(json);
  }
  modal.hidden = false;
}

export function closeStatementJsonModal() {
  const modal = $("statement-json-modal");
  const body  = $("statement-json-body");
  if (!modal) return;
  modal.hidden = true;
  if (body) body.textContent = "";
}

// Q2.6 (#273). Triggers a browser download from a Blob. Kept as a
// separate helper so the app-level orchestration (fetch → state →
// trigger) can stub it in tests via `triggerBlobDownload`.
export function triggerBlobDownload(blob, filename) {
  if (typeof URL === "undefined" || typeof document === "undefined") return;
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename || "statement.csv";
  // Some browsers require the anchor to be in the document to fire
  // a click reliably. Append → click → remove keeps the DOM clean.
  document.body?.appendChild(a);
  a.click();
  if (a.parentNode) a.parentNode.removeChild(a);
  // Defer revoke to give the browser time to start the download.
  setTimeout(() => { try { URL.revokeObjectURL(url); } catch { /* ignore */ } }, 1000);
}

// Tiny, dependency-free HTML escaper. We only ever interpolate
// server-supplied identifiers (symbol, clOrdId, status enums), but
// belt-and-braces costs nothing here.
function escapeHtml(s) {
  if (s == null) return "";
  return String(s)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}
