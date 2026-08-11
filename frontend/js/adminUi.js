// Admin view: firms grid, killswitch toggles, EOD trigger.
// Visible only when the JWT role is "admin"; the DOM is mounted but
// hidden for non-admin sessions and the network calls are gated in
// app.js as well so 403s don't pollute logs if the role drifts.

import { getState, subscribe } from "./state.js";
import { formatUtcTime } from "./formatters.js";

const $ = (id) => document.getElementById(id);

let onToggleFirm = () => {};
let onToggleEndClient = () => {};
let onAddEndClient = () => {};
let onToggleHalt = () => {};
let onAddHalt = () => {};
let onRunEod = () => {};
let onRefresh = () => {};
let onLoadOutboundMutations = () => {};
let onLoadOutboundMutationDetail = () => {};
let onRegisterOutboundEvidence = () => {};
let onResolveOutboundMutation = () => {};
let onResolveVenueConfirmedBatch = () => {};
let onApproveOutboundMutation = () => {};
let currentUsername = null;

export function setAdminHandlers(handlers) {
  onToggleFirm      = handlers.onToggleFirm      ?? onToggleFirm;
  onToggleEndClient = handlers.onToggleEndClient ?? onToggleEndClient;
  onAddEndClient    = handlers.onAddEndClient    ?? onAddEndClient;
  onToggleHalt      = handlers.onToggleHalt      ?? onToggleHalt;
  onAddHalt         = handlers.onAddHalt         ?? onAddHalt;
  onRunEod          = handlers.onRunEod          ?? onRunEod;
  onRefresh         = handlers.onRefresh         ?? onRefresh;
  onLoadOutboundMutations       = handlers.onLoadOutboundMutations       ?? onLoadOutboundMutations;
  onLoadOutboundMutationDetail  = handlers.onLoadOutboundMutationDetail  ?? onLoadOutboundMutationDetail;
  onRegisterOutboundEvidence    = handlers.onRegisterOutboundEvidence    ?? onRegisterOutboundEvidence;
  onResolveOutboundMutation     = handlers.onResolveOutboundMutation     ?? onResolveOutboundMutation;
  onResolveVenueConfirmedBatch  = handlers.onResolveVenueConfirmedBatch  ?? onResolveVenueConfirmedBatch;
  onApproveOutboundMutation     = handlers.onApproveOutboundMutation     ?? onApproveOutboundMutation;
  // Used only as a client-side UX hint (hide/disable Approve for the
  // proposer's own session) — the server independently rejects
  // self-approval regardless of what the UI shows (#785).
  if (handlers.currentUsername !== undefined) currentUsername = handlers.currentUsername;
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

  bindOutboundMutationsUi();

  subscribe(renderForSlice);
}

function confirmTwice(first, second) {
  return window.confirm(first) && window.confirm(second);
}

// #785. Outbound mutation reconciliation panel wiring. Kept in its own
// function (rather than inlined into bindAdminUi) because it owns a
// small amount of local UI state — which mutationId is currently
// expanded — that the other admin panels don't need.
let expandedMutationId = null;

function bindOutboundMutationsUi() {
  $("outbound-mutations-filter-form").addEventListener("submit", (e) => {
    e.preventDefault();
    loadOutboundMutations();
  });

  $("outbound-mutations-body").addEventListener("click", (e) => {
    const btn = e.target.closest(".outbound-mutation-expand");
    if (!btn) return;
    const mutationId = btn.dataset.mutationId;
    expandedMutationId = expandedMutationId === mutationId ? null : mutationId;
    if (expandedMutationId) onLoadOutboundMutationDetail(expandedMutationId);
    renderOutboundMutations();
    renderOutboundMutationDetail();
  });

  $("outbound-resolve-confirmed-batch").addEventListener("click", async () => {
    const candidates = venueConfirmedCandidates();
    if (candidates.length === 0) return;
    if (!window.confirm(
      `Resolve ${candidates.length} venue-confirmed mutation${candidates.length === 1 ? "" : "s"} using the terminal Execution Reports already recorded by the platform?`,
    )) return;
    const button = $("outbound-resolve-confirmed-batch");
    const originalLabel = button.textContent;
    button.disabled = true;
    button.textContent = "Resolving…";
    try {
      await onResolveVenueConfirmedBatch(candidates.map(mutation => mutation.mutationId));
    } finally {
      button.disabled = false;
      button.textContent = originalLabel;
    }
  });

  $("outbound-evidence-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    if (!expandedMutationId) return;
    const payload = {
      sourceType: $("outbound-evidence-source-type").value,
      evidenceReference: $("outbound-evidence-reference").value.trim(),
      coverageStartUtc: toUtcIso($("outbound-evidence-coverage-start").value),
      coverageEndUtc: toUtcIso($("outbound-evidence-coverage-end").value),
      attestationReference: $("outbound-evidence-attestation").value.trim(),
    };
    await onRegisterOutboundEvidence(expandedMutationId, payload);
  });

  $("outbound-resolve-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    if (!expandedMutationId) return;
    const decision = $("outbound-resolve-decision").value;
    if (!confirmTwice(
      `Resolve mutation ${expandedMutationId} as "${decision}"?`,
      decision === "venue_absent"
        ? "Confirm: this requires a SECOND, DIFFERENT admin to approve before capacity is released."
        : `Confirm: this decision applies immediately and cannot be un-sent.`,
    )) return;
    const payload = {
      decision,
      evidenceType: $("outbound-resolve-evidence-type").value,
      evidenceReference: $("outbound-resolve-evidence-reference").value.trim(),
      reason: $("outbound-resolve-reason").value.trim(),
    };
    await onResolveOutboundMutation(expandedMutationId, payload);
  });

  $("outbound-pending-proposals").addEventListener("click", (e) => {
    const btn = e.target.closest(".outbound-approve-proposal");
    if (!btn) return;
    const mutationId = btn.dataset.mutationId;
    const proposalId = btn.dataset.proposalId;
    if (!confirmTwice(
      `Approve resolution proposal ${proposalId}?`,
      "Confirm: approving releases capacity for this mutation. This is a maker/checker step — you must be a DIFFERENT admin from the one who proposed it.",
    )) return;
    onApproveOutboundMutation(mutationId, proposalId);
  });
}

function toUtcIso(datetimeLocalValue) {
  if (!datetimeLocalValue) return null;
  // <input type="datetime-local"> has no timezone; the operator is
  // expected to enter the UTC wall-clock time directly (mirrors
  // docs/RUNBOOK.md §0.1's evidence-registration examples, which are
  // always expressed in UTC).
  return `${datetimeLocalValue}:00Z`;
}

async function loadOutboundMutations() {
  const stateFilter = $("outbound-mutations-state").value || undefined;
  const requiresReconciliation = $("outbound-mutations-requires-reconciliation").checked
    ? true
    : undefined;
  await onLoadOutboundMutations({ state: stateFilter, requiresReconciliation });
}

function renderForSlice(slice) {
  if (slice === "firmsHealth" || slice === "killStatus" || slice === "all") renderFirms();
  if (slice === "killStatus"  || slice === "all") renderEndClients();
  if (slice === "haltStatus"  || slice === "all") renderHalts();
  if (slice === "eodReport"   || slice === "all") renderEod();
  if (slice === "outboundMutations"       || slice === "all") renderOutboundMutations();
  if (slice === "outboundMutationDetail"  || slice === "all") renderOutboundMutationDetail();
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
    renderOutboundMutations();
    renderOutboundMutationDetail();
  }
}

export function renderAdminAll() {
  renderFirms();
  renderEndClients();
  renderHalts();
  renderEod();
  renderOutboundMutations();
  renderOutboundMutationDetail();
}

function renderFirms() {
  const body = $("admin-firms-body");
  if (!body) return;
  const fh = getState().firmsHealth;
  const ks = getState().killStatus;
  if (!fh || !Array.isArray(fh.firms)) {
    body.innerHTML = `<tr><td colspan="6" class="muted">awaiting /api/admin/firms…</td></tr>`;
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
    body.innerHTML = `<tr><td colspan="2" class="muted">awaiting /api/admin/kill…</td></tr>`;
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
    body.innerHTML = `<tr><td colspan="2" class="muted">awaiting /api/admin/halts…</td></tr>`;
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

function renderOutboundMutations() {
  const body = $("outbound-mutations-body");
  if (!body) return;
  const om = getState().outboundMutations;
  if (!om) {
    body.innerHTML = `<tr><td colspan="7" class="muted">awaiting /api/admin/outbound-mutations…</td></tr>`;
    return;
  }
  const list = om.mutations ?? [];
  renderGuidedResolution();
  if (list.length === 0) {
    body.innerHTML = `<tr><td colspan="7" class="muted">no matching mutations</td></tr>`;
    return;
  }
  body.innerHTML = list.map(m => {
    const isExpanded = expandedMutationId === m.mutationId;
    const pendingBadge = m.pendingApproval
      ? `<span class="badge badge-warning badge-square badge-uppercase">pending approval</span>`
      : "";
    return `<tr${isExpanded ? ' class="firm-row-bad"' : ""}>
      <td><code>${escapeHtml(m.mutationId)}</code></td>
      <td>${escapeHtml(m.kind ?? "—")}</td>
      <td>${escapeHtml(m.state ?? "—")} ${pendingBadge}</td>
      <td>${escapeHtml(m.primaryClOrdId ?? m.PrimaryClOrdId ?? "—")}</td>
      <td>${escapeHtml(formatUtcTime(m.recordedAtUtc ?? m.RecordedAtUtc))}</td>
      <td>${escapeHtml(m.ambiguityReason ?? "—")}</td>
      <td><button type="button" class="outbound-mutation-expand btn btn-secondary btn-sm"
                  data-mutation-id="${escapeHtml(m.mutationId)}">
        ${isExpanded ? "Hide" : "Details"}
      </button></td>
    </tr>`;
  }).join("");
}

function venueConfirmedCandidates() {
  const list = getState().outboundMutations?.mutations ?? [];
  return list.filter(mutation =>
    mutation.requiresReconciliation === true
    && mutation.state === "venue_acknowledged"
    && mutation.pendingApproval !== true
  );
}

function renderGuidedResolution() {
  const panel = $("outbound-guided-resolution");
  if (!panel) return;
  const candidates = venueConfirmedCandidates();
  panel.hidden = candidates.length === 0;
  if (candidates.length === 0) return;
  setText(
    "outbound-guided-resolution-title",
    `${candidates.length} venue-confirmed mutation${candidates.length === 1 ? "" : "s"} can be resolved safely.`,
  );
  const button = $("outbound-resolve-confirmed-batch");
  if (button && !button.disabled) {
    button.textContent = `Resolve ${candidates.length} confirmed mutation${candidates.length === 1 ? "" : "s"}`;
  }
}

const TERMINAL_EXECUTION_REPORT_KINDS = new Set([
  "rejected",
  "canceled",
  "fill",
  "replaced",
  "expired",
]);

export function findTerminalExecutionReportEvidence(detail) {
  return (detail?.inboundEvidence ?? []).find(evidence => {
    const evidenceId = evidence.evidenceId ?? evidence.EvidenceId;
    const disposition = String(evidence.disposition ?? "").toLowerCase();
    const messageKind = String(evidence.messageKind ?? "").toLowerCase();
    const authoritativeDisposition = disposition === "matched"
      || (disposition === "conflicting"
        && evidence.authoritativeTerminalContradiction === true);
    return evidence.kind === "execution_report"
      && authoritativeDisposition
      && TERMINAL_EXECUTION_REPORT_KINDS.has(messageKind)
      && /^[0-9a-f]{64}$/i.test(evidenceId ?? "");
  }) ?? null;
}

function renderOutboundMutationDetail() {
  const panel = $("outbound-mutation-detail");
  if (!panel) return;
  if (!expandedMutationId) { panel.hidden = true; return; }
  panel.hidden = false;
  setText("outbound-detail-id", expandedMutationId);
  const detail = getState().outboundMutationDetail;
  const summaryEl = $("outbound-detail-summary");
  if (!detail || detail.mutationId !== expandedMutationId) {
    if (summaryEl) summaryEl.textContent = "loading…";
    $("outbound-pending-proposals").innerHTML = "";
    return;
  }
  if (summaryEl) summaryEl.textContent = JSON.stringify(detail.detail, null, 2);

  const proposals = detail.detail?.proposals ?? [];
  const pending = proposals.filter(p => p.approvedAtUtc == null);
  const proposalsEl = $("outbound-pending-proposals");
  if (pending.length === 0) {
    proposalsEl.innerHTML = "";
    return;
  }
  proposalsEl.innerHTML = pending.map(p => {
    // Client-side UX hint only (#785): a same-admin can't meaningfully
    // "checker"-approve their own proposal, so hide the button when we
    // know the current session is the maker. The server independently
    // rejects self-approval regardless of what this renders.
    const isSelf = currentUsername != null && p.makerRef === currentUsername;
    if (isSelf) {
      return `<p class="feedback">Proposal <code>${escapeHtml(p.proposalId)}</code>
        (decision: ${escapeHtml(p.decision)}) awaits approval from a
        <strong>different</strong> admin — you proposed it.</p>`;
    }
    return `<p>
      Proposal <code>${escapeHtml(p.proposalId)}</code>
      — decision: ${escapeHtml(p.decision)}, maker: ${escapeHtml(p.makerRef)}
      <button type="button" class="outbound-approve-proposal btn btn-danger btn-sm"
              data-mutation-id="${escapeHtml(expandedMutationId)}"
              data-proposal-id="${escapeHtml(p.proposalId)}">
        Approve
      </button>
    </p>`;
  }).join("");
}

function renderEod() {
  const el = $("admin-eod-output");
  if (!el) return;
  const r = getState().eodReport;
  if (!r) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false;
  const ts = formatUtcTime(r.ranAt);
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

export function setOutboundMutationsFeedback(message, kind) {
  const el = $("outbound-mutations-feedback");
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
