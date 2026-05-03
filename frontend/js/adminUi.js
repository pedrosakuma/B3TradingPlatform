// Admin view: firms grid, killswitch toggles, EOD trigger.
// Visible only when the JWT role is "admin"; the DOM is mounted but
// hidden for non-admin sessions and the network calls are gated in
// app.js as well so 403s don't pollute logs if the role drifts.

import { getState, subscribe } from "./state.js";

const $ = (id) => document.getElementById(id);

let onToggleFirm = () => {};
let onToggleEndClient = () => {};
let onAddEndClient = () => {};
let onRunEod = () => {};
let onRefresh = () => {};

export function setAdminHandlers(handlers) {
  onToggleFirm      = handlers.onToggleFirm      ?? onToggleFirm;
  onToggleEndClient = handlers.onToggleEndClient ?? onToggleEndClient;
  onAddEndClient    = handlers.onAddEndClient    ?? onAddEndClient;
  onRunEod          = handlers.onRunEod          ?? onRunEod;
  onRefresh         = handlers.onRefresh         ?? onRefresh;
}

export function bindAdminUi() {
  $("admin-refresh").addEventListener("click", () => onRefresh());

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

  $("admin-eod-btn").addEventListener("click", () => {
    if (!confirmTwice(
      "Run EOD materialisation now?",
      "Confirm: run EOD against today's WAL. This is normally scheduled.",
    )) return;
    onRunEod();
  });

  subscribe(renderForSlice);
}

function confirmTwice(first, second) {
  return window.confirm(first) && window.confirm(second);
}

function renderForSlice(slice) {
  if (slice === "firmsHealth" || slice === "killStatus" || slice === "all") renderFirms();
  if (slice === "killStatus"  || slice === "all") renderEndClients();
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
    renderEod();
  }
}

export function renderAdminAll() {
  renderFirms();
  renderEndClients();
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
    const btnClass = killed ? "danger-btn revive" : "danger-btn engage";
    return `<tr>
      <td><code>${escapeHtml(f.firmId)}</code></td>
      <td>${escapeHtml(f.endpoint ?? "—")}</td>
      <td>${escapeHtml(f.sessionId ?? "—")}</td>
      <td>${escapeHtml(stateLabel)}</td>
      <td class="num">${escapeHtml(String(verId))}</td>
      <td>${reconn}</td>
      <td>${killed ? `<span class="killed-tag">KILLED</span>` : ""}
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
    <td><button type="button" class="ec-revive danger-btn revive" data-ec="${escapeHtml(id)}">Revive</button></td>
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
  el.className = `feedback ${kind === "ok" ? "ok" : "error"}`;
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
