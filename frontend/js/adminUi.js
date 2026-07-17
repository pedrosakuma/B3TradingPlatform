// Admin view: firms grid, killswitch toggles, EOD trigger.
// Visible only when the JWT role is "admin"; the DOM is mounted but
// hidden for non-admin sessions and the network calls are gated in
// app.js as well so 403s don't pollute logs if the role drifts.

import { getState, subscribe } from "./state.js";

const $ = (id) => document.getElementById(id);

let onToggleFirm = () => {};
let onToggleEndClient = () => {};
let onAddEndClient = () => {};
let onToggleHalt = () => {};
let onAddHalt = () => {};
let onRunEod = () => {};
let onRefresh = () => {};

export function setAdminHandlers(handlers) {
  onToggleFirm      = handlers.onToggleFirm      ?? onToggleFirm;
  onToggleEndClient = handlers.onToggleEndClient ?? onToggleEndClient;
  onAddEndClient    = handlers.onAddEndClient    ?? onAddEndClient;
  onToggleHalt      = handlers.onToggleHalt      ?? onToggleHalt;
  onAddHalt         = handlers.onAddHalt         ?? onAddHalt;
  onRunEod          = handlers.onRunEod          ?? onRunEod;
  onRefresh         = handlers.onRefresh         ?? onRefresh;
}

export function bindAdminUi() {
  $("admin-refresh").addEventListener("click", async () => {
    const button = $("admin-refresh");
    button.disabled = true;
    setAdminFeedback("Refreshing operator state…", "info");
    try {
      await onRefresh();
      setAdminFeedback("Operator state refreshed.", "ok");
    } catch (error) {
      setAdminFeedback(error?.message || "Refresh failed.", "error");
    } finally {
      button.disabled = false;
    }
  });

  $("admin-firms-body").addEventListener("click", (e) => {
    const btn = e.target.closest(".firm-toggle");
    if (!btn) return;
    const firmId = btn.dataset.firm;
    const engage = btn.dataset.action === "engage";
    if (!confirmTwice(
      `Killswitch ${engage ? "ENGAGE" : "REVIVE"} for firm ${firmId}?`,
      `Confirm: ${engage ? "engage" : "revive"} ${firmId}. This affects ALL traders in the firm.`,
    )) return;
    onToggleFirm({ firmId, engage });
  });

  $("admin-endclient-body").addEventListener("click", (e) => {
    const btn = e.target.closest(".ec-revive");
    if (!btn) return;
    const id = btn.dataset.ec;
    if (!confirmTwice(
      `Revive end-client ${id}?`,
      `Confirm: revive ${id}. They will be allowed to send orders again.`,
    )) return;
    onToggleEndClient({ id, engage: false });
  });

  $("admin-add-ec-form").addEventListener("submit", (e) => {
    e.preventDefault();
    const id = $("admin-add-ec-id").value.trim();
    if (!id) return;
    if (!confirmTwice(
      `ENGAGE killswitch for end-client ${id}?`,
      `Confirm: engage ${id}. They will be unable to send orders.`,
    )) return;
    onAddEndClient({ id });
    $("admin-add-ec-id").value = "";
  });

  $("admin-halts-body").addEventListener("click", (e) => {
    const btn = e.target.closest(".halt-resume");
    if (!btn) return;
    const symbol = btn.dataset.symbol;
    if (!confirmTwice(
      `Resume trading for ${symbol}?`,
      `Confirm: resume ${symbol}. Orders for this symbol will be accepted again.`,
    )) return;
    onToggleHalt({ symbol, halt: false });
  });

  $("admin-add-halt-form").addEventListener("submit", (e) => {
    e.preventDefault();
    const symbol = $("admin-add-halt-symbol").value.trim().toUpperCase();
    if (!symbol) return;
    if (!confirmTwice(
      `HALT trading for symbol ${symbol}?`,
      `Confirm: halt ${symbol}. ALL participants will be blocked from sending orders for this symbol until resumed.`,
    )) return;
    onAddHalt({ symbol });
    $("admin-add-halt-symbol").value = "";
  });

  $("admin-eod-btn").addEventListener("click", async () => {
    const btn = $("admin-eod-btn");
    if (btn?.disabled) return;
    if (!confirmTwice(
      "Run EOD materialisation now?",
      "Confirm: run EOD against today's WAL. This is normally scheduled.",
    )) return;
    // Idempotency guard (#342): disable the button while the POST is
    // in flight so a double-click can't fire two concurrent EOD runs.
    // Backend would handle it, but the UI shouldn't suggest the second
    // click "did" anything separate.
    const originalLabel = btn?.textContent;
    if (btn) { btn.disabled = true; btn.textContent = "Running…"; }
    try {
      await onRunEod();
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = originalLabel ?? "Run EOD now"; }
    }
  });

  subscribe(renderForSlice);
}

function confirmTwice(first, second) {
  return window.confirm(first) && window.confirm(second);
}

function renderForSlice(slice) {
  if (slice === "firmsHealth" || slice === "killStatus" || slice === "all") renderFirms();
  if (slice === "killStatus"  || slice === "all") renderEndClients();
  if (slice === "haltStatus"  || slice === "all") renderHalts();
  if (slice === "eodReport"   || slice === "all") renderEod();
  if (slice === "currentView") onViewChanged();
}

function onViewChanged() {
  // No-op hook for now; app.js drives view show/hide. Kept so the admin
  // panel re-renders when first revealed (state may have arrived while
  // the trader view was on top).
  if (getState().currentView === "admin") {
    renderFirms();
    renderEndClients();
    renderHalts();
    renderEod();
  }
}

export function renderAdminAll() {
  renderFirms();
  renderEndClients();
  renderHalts();
  renderEod();
}

function renderFirms() {
  const body = $("admin-firms-body");
  if (!body) return;
  const fh = getState().firmsHealth;
  const ks = getState().killStatus;
  if (!fh || !Array.isArray(fh.firms)) {
    body.innerHTML = `<tr><td colspan="6" class="muted">awaiting /admin/firms…</td></tr>`;
    setText("admin-mode", "—");
    return;
  }
  setText("admin-mode", fh.mode ?? "—");
  const killedFirms = new Set(ks?.firms ?? []);
  body.innerHTML = fh.firms.map(f => {
    const killed = killedFirms.has(f.firmId);
    const stateLabel = f.sessionState ?? "—";
    const verId = f.sessionVerId ?? "—";
    const reconn = f.reconnecting ? "yes" : "no";
    const action = killed ? "revive" : "engage";
    const btnLabel = killed ? "Revive" : "Killswitch";
    const btnClass = killed ? "btn btn-success btn-sm" : "btn btn-danger btn-sm";
    // Visually flag firms that are not in a healthy "Established &
    // not-reconnecting" state so the admin's eye lands on the row that
    // needs attention without scanning the whole table (#342). Killed
    // firms are already flagged via the KILLED tag + Revive button.
    const bad = !killed && (f.sessionState !== "Established" || f.reconnecting === true);
    const rowCls = bad ? ` class="firm-row-bad"` : "";
    return `<tr${rowCls}>
      <td><code>${escapeHtml(f.firmId)}</code></td>
      <td>${escapeHtml(f.endpoint ?? "—")}</td>
      <td>${escapeHtml(f.sessionId ?? "—")}</td>
      <td>${escapeHtml(stateLabel)}</td>
      <td class="num">${escapeHtml(String(verId))}</td>
      <td>${reconn}</td>
      <td>${killed ? `<span class="killed-tag badge badge-danger badge-square badge-uppercase">KILLED</span>` : ""}
        <button type="button" class="firm-toggle ${btnClass}"
                data-firm="${escapeHtml(f.firmId)}" data-action="${action}">
          ${btnLabel}
        </button></td>
    </tr>`;
  }).join("");
}

function renderEndClients() {
  const body = $("admin-endclient-body");
  if (!body) return;
  const ks = getState().killStatus;
  if (!ks) {
    body.innerHTML = `<tr><td colspan="2" class="muted">awaiting /admin/kill…</td></tr>`;
    return;
  }
  const list = ks.endClients ?? [];
  if (list.length === 0) {
    body.innerHTML = `<tr><td colspan="2" class="muted">no killed end-clients</td></tr>`;
    return;
  }
  body.innerHTML = list.map(id => `<tr>
    <td><code>${escapeHtml(id)}</code></td>
    <td><button type="button" class="ec-revive btn btn-success btn-sm" data-ec="${escapeHtml(id)}">Revive</button></td>
  </tr>`).join("");
}

function renderHalts() {
  const body = $("admin-halts-body");
  if (!body) return;
  const hs = getState().haltStatus;
  if (!hs) {
    body.innerHTML = `<tr><td colspan="2" class="muted">awaiting /admin/halts…</td></tr>`;
    return;
  }
  const list = hs.symbols ?? [];
  if (list.length === 0) {
    body.innerHTML = `<tr><td colspan="2" class="muted">no halted symbols</td></tr>`;
    return;
  }
  body.innerHTML = list.map(sym => `<tr>
    <td><code>${escapeHtml(sym)}</code></td>
    <td><button type="button" class="halt-resume btn btn-success btn-sm" data-symbol="${escapeHtml(sym)}">Resume</button></td>
  </tr>`).join("");
}

function renderEod() {
  const el = $("admin-eod-output");
  if (!el) return;
  const r = getState().eodReport;
  if (!r) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false;
  const ts = new Date(r.ranAt).toISOString().slice(11, 19);
  el.textContent = `[${ts}] ${JSON.stringify(r.report, null, 2)}`;
}

export function setAdminFeedback(message, kind) {
  const el = $("admin-feedback");
  if (!el) return;
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false;
  el.textContent = message;
  el.className = `feedback ${kind === "ok" ? "ok" : kind === "error" ? "error" : ""}`;
}

function setText(id, text) {
  const el = $(id);
  if (el) el.textContent = text;
}

function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => (
    { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]
  ));
}
