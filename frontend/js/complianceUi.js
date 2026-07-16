// Q4.14 (#314). Compliance role / UI bundle.
//
// Renders the four-panel compliance console:
//   1. Drop-copy live feed (rolling table, last 200 messages)
//   2. Audit-log search (form → GET /admin/audit, cursor pagination)
//   3. Best-execution evidence drill-down (GET /fills/{id}/touch)
//   4. CVM 35 / 505 report download (GET /reports/cvm/{model}/{date})
//
// SCOPE / ROLE
// ────────────
// Visible to JWT role `compliance` AND `admin`. The role gating
// lives in app.js (which tab shows up in the nav) and in
// applyComplianceVisibility below (defensive — if the role drifts
// mid-session the view stays hidden).
//
// Compliance is intentionally READ-ONLY. There are no mutating
// surfaces here — every action is a fetch with idempotent semantics.
//
// MULTI-ROLE TOGGLE (issue #314 spec)
// ───────────────────────────────────
// The JWT today carries a single `role` claim. The issue mentions
// "Toggle de role no login se user tem múltiplas roles", but no
// multi-role path is wired end-to-end (JwtIssuer emits one role; the
// authorization policies match one role). Once the platform supports
// multi-role principals, the nav-tab gating in app.js
// (`tabsForRole(role)`) is the single seam to extend; this module
// renders the same panels regardless of which roles brought the
// caller in.
//
// LGPD
// ────
// The audit-log panel surfaces actor usernames. Compliance is
// firm-scoped server-side at /admin/audit (Q4.14 backend slice), so
// the rendered rows are always within the caller's own firm — no
// cross-firm names leak through this UI.

import {
  getState,
  subscribe,
  appendComplianceFeed,
  setComplianceFeedPaused,
  clearComplianceFeed,
  setComplianceConnection,
  COMPLIANCE_FEED_CAP,
} from "./state.js";

const $ = (id) => document.getElementById(id);

let onAuditSearch = () => {};
let onAuditNext = () => {};
let onFillTouchLookup = () => {};
let onCvmDownload = () => {};

let lastAuditCursor = null;
let lastAuditOpts = null;

export function setComplianceHandlers(handlers) {
  onAuditSearch      = handlers.onAuditSearch      ?? onAuditSearch;
  onAuditNext        = handlers.onAuditNext        ?? onAuditNext;
  onFillTouchLookup  = handlers.onFillTouchLookup  ?? onFillTouchLookup;
  onCvmDownload      = handlers.onCvmDownload      ?? onCvmDownload;
}

export function bindComplianceUi() {
  // ── 1. Drop-copy feed controls ────────────────────────────────
  const pauseBtn = $("compliance-feed-pause");
  if (pauseBtn) {
    pauseBtn.addEventListener("click", () => {
      const paused = !getState().complianceFeed?.paused;
      setComplianceFeedPaused(paused);
    });
  }
  const clearBtn = $("compliance-feed-clear");
  if (clearBtn) {
    clearBtn.addEventListener("click", () => clearComplianceFeed());
  }

  // ── 2. Audit search form ──────────────────────────────────────
  const auditForm = $("compliance-audit-form");
  if (auditForm) {
    auditForm.addEventListener("submit", (e) => {
      e.preventDefault();
      lastAuditCursor = null;
      const opts = readAuditFormOpts();
      lastAuditOpts = opts;
      onAuditSearch(opts);
    });
  }
  const nextBtn = $("compliance-audit-next");
  if (nextBtn) {
    nextBtn.addEventListener("click", () => {
      if (!lastAuditCursor || !lastAuditOpts) return;
      onAuditNext({ ...lastAuditOpts, cursor: lastAuditCursor });
    });
  }

  // ── 3. Best-exec touch lookup ─────────────────────────────────
  const touchForm = $("compliance-touch-form");
  if (touchForm) {
    touchForm.addEventListener("submit", (e) => {
      e.preventDefault();
      const id = $("compliance-touch-id").value.trim();
      if (!id) return;
      onFillTouchLookup(id);
    });
  }

  // ── 4. CVM download buttons ───────────────────────────────────
  const cvm35 = $("compliance-cvm-35");
  const cvm505 = $("compliance-cvm-505");
  if (cvm35) {
    cvm35.addEventListener("click", () => {
      const date = $("compliance-cvm-date").value;
      if (!date) { setCvmFeedback("pick a date", "error"); return; }
      onCvmDownload({ model: 35, date });
    });
  }
  if (cvm505) {
    cvm505.addEventListener("click", () => {
      const date = $("compliance-cvm-date").value;
      if (!date) { setCvmFeedback("pick a date", "error"); return; }
      onCvmDownload({ model: 505, date });
    });
  }

  // Default the CVM date to "yesterday" in BRT (UTC-3). The B3
  // session boundary lands at end-of-day BRT; using the day before
  // ensures the WAL has settled before regulators ingest.
  const dateInput = $("compliance-cvm-date");
  if (dateInput && !dateInput.value) dateInput.value = yesterdayBrt();

  subscribe(renderForSlice);
}

function readAuditFormOpts() {
  const opts = {};
  const since = $("compliance-audit-since")?.value;
  const until = $("compliance-audit-until")?.value;
  const user  = $("compliance-audit-user")?.value.trim();
  const type  = $("compliance-audit-type")?.value.trim();
  const outcome = $("compliance-audit-outcome")?.value;
  if (since)   opts.since = new Date(since).toISOString();
  if (until)   opts.until = new Date(until).toISOString();
  if (user)    opts.user = user;
  if (type)    opts.type = type;
  if (outcome && outcome !== "any") opts.outcome = outcome;
  opts.limit = 100;
  return opts;
}

function renderForSlice(slice) {
  if (slice === "complianceFeed" || slice === "all") renderFeed();
  if (slice === "complianceConnection" || slice === "all") renderConnection();
  if (slice === "currentView" || slice === "user" || slice === "all") {
    applyComplianceVisibility();
  }
}

function applyComplianceVisibility() {
  const view = $("compliance-view");
  if (!view) return;
  const st = getState();
  const role = st.user?.role;
  const allowed = role === "compliance" || role === "admin";
  const visible = allowed && st.currentView === "compliance";
  view.hidden = !visible;
}

export function onComplianceVisibilityChange() {
  applyComplianceVisibility();
}

function renderFeed() {
  const body = $("compliance-feed-body");
  if (!body) return;
  const { entries, paused } = getState().complianceFeed ?? { entries: [], paused: false };
  const pauseBtn = $("compliance-feed-pause");
  if (pauseBtn) pauseBtn.textContent = paused ? "Resume" : "Pause";
  const status = $("compliance-feed-status");
  if (status) {
    status.textContent = `${entries.length}/${COMPLIANCE_FEED_CAP} entries${paused ? " (paused)" : ""}`;
  }
  if (entries.length === 0) {
    body.innerHTML = `<tr><td colspan="8" class="muted-line">No drop-copy traffic yet.</td></tr>`;
    return;
  }
  // Newest at the top — entries are appended in arrival order so we
  // walk back to front. innerHTML rebuild is fine at <=200 rows; the
  // ring cap guarantees bounded work.
  const rows = [];
  for (let i = entries.length - 1; i >= 0; i--) rows.push(rowHtml(entries[i]));
  body.innerHTML = rows.join("");
}

function renderConnection() {
  const el = $("compliance-feed-connection");
  if (!el) return;
  const connection = getState().complianceConnection ?? { status: "disconnected", retryInMs: null };
  const retrySeconds = connection.retryInMs == null ? null : Math.max(1, Math.ceil(connection.retryInMs / 1000));
  const labels = {
    connected: "Connected",
    connecting: "Connecting…",
    reconnecting: retrySeconds == null ? "Reconnecting…" : `Reconnecting in ${retrySeconds}s…`,
    error: "Connection error",
    disconnected: "Disconnected",
  };
  el.textContent = labels[connection.status] ?? labels.disconnected;
  el.dataset.state = connection.status;
}

function rowHtml(entry) {
  const t = entry.timestamp ? new Date(entry.timestamp).toISOString().slice(11, 19) : "";
  const type = escapeHtml(entry.type ?? "");
  const status = escapeHtml(entry.status ?? "");
  const sym  = escapeHtml(entry.symbol ?? "");
  const side = escapeHtml(entry.side ?? "");
  const qty  = entry.qty != null ? String(entry.qty) : "";
  const px   = entry.price != null ? String(entry.price) : "";
  const cl   = escapeHtml(entry.clOrdId ?? "");
  return `<tr><td>${escapeHtml(t)}</td><td>${type}</td><td>${status}</td>` +
         `<td>${sym}</td><td>${side}</td><td class="num">${qty}</td>` +
         `<td class="num">${px}</td><td>${cl}</td></tr>`;
}

// ── External setters used by app.js after async work ─────────────

export function setAuditResults(page) {
  const body = $("compliance-audit-body");
  if (!body) return;
  const entries = page?.entries ?? [];
  if (entries.length === 0) {
    body.innerHTML = `<tr><td colspan="6" class="muted-line">No matching audit events.</td></tr>`;
  } else {
    const rows = entries.map((e) => {
      const t = new Date(e.timestampUtc).toISOString().replace("T", " ").slice(0, 19);
      return `<tr><td>${escapeHtml(t)}</td>` +
             `<td>${escapeHtml(e.eventType ?? "")}</td>` +
             `<td>${escapeHtml(e.outcome ?? "")}</td>` +
             `<td>${escapeHtml(e.actorUsername ?? e.actorUserId ?? "")}</td>` +
             `<td>${escapeHtml(e.actorFirm ?? "")}</td>` +
             `<td>${escapeHtml(e.reasonCode ?? "")}</td></tr>`;
    });
    body.innerHTML = rows.join("");
  }
  lastAuditCursor = page?.nextCursor ?? null;
  const nextBtn = $("compliance-audit-next");
  if (nextBtn) nextBtn.hidden = !lastAuditCursor;
}

export function setAuditFeedback(message, kind) {
  const el = $("compliance-audit-feedback");
  if (!el) return;
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false;
  el.textContent = message;
  el.className = "feedback" + (kind ? ` feedback-${kind}` : "");
}

export function setTouchResult(id, dto) {
  const out = $("compliance-touch-output");
  if (!out) return;
  if (!dto) { out.hidden = true; out.textContent = ""; return; }
  const pretty = JSON.stringify({ id, ...dto }, null, 2);
  out.hidden = false;
  out.textContent = pretty;
}

export function setTouchFeedback(message, kind) {
  const el = $("compliance-touch-feedback");
  if (!el) return;
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false;
  el.textContent = message;
  el.className = "feedback" + (kind ? ` feedback-${kind}` : "");
}

export function setCvmFeedback(message, kind) {
  const el = $("compliance-cvm-feedback");
  if (!el) return;
  if (!message) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false;
  el.textContent = message;
  el.className = "feedback" + (kind ? ` feedback-${kind}` : "");
}

// ── Drop-copy WS lifecycle (called from app.js) ──────────────────

const DROP_COPY_CHANNELS = new Set([
  "dropcopy.orders",
  "dropcopy.fills",
  "dropcopy.cancels",
]);
const DROP_COPY_RECONNECT_BASE_MS = 1_000;
const DROP_COPY_RECONNECT_MAX_MS = 30_000;

let dropCopySocket = null;
let dropCopyUrl = null;
let dropCopyActive = false;
let dropCopyGeneration = 0;
let dropCopyReconnectTimer = null;
let dropCopyReconnectAttempt = 0;

export function openDropCopyFeed(url) {
  if (!url || typeof url !== "string") return null;
  if (typeof WebSocket === "undefined") return null;
  if (dropCopyActive && dropCopyUrl === url) return dropCopySocket;

  closeDropCopyFeed();
  dropCopyActive = true;
  dropCopyUrl = url;
  dropCopyGeneration += 1;
  dropCopyReconnectAttempt = 0;
  return connectDropCopy(dropCopyGeneration);
}

export function closeDropCopyFeed() {
  dropCopyActive = false;
  dropCopyUrl = null;
  dropCopyGeneration += 1;
  if (dropCopyReconnectTimer != null) {
    clearTimeout(dropCopyReconnectTimer);
    dropCopyReconnectTimer = null;
  }
  const ws = dropCopySocket;
  dropCopySocket = null;
  if (ws) {
    try { ws.close(1000, "compliance view closed"); } catch { /* noop */ }
  }
  setComplianceConnection("disconnected");
}

function connectDropCopy(generation) {
  if (!dropCopyActive || generation !== dropCopyGeneration || !dropCopyUrl) return null;
  setComplianceConnection(dropCopyReconnectAttempt === 0 ? "connecting" : "reconnecting");
  try {
    const ws = new WebSocket(dropCopyUrl);
    const snapshotChannels = new Set();
    let snapshotStarted = false;

    ws.addEventListener("open", () => {
      if (!isCurrentDropCopySocket(ws, generation)) return;
      setComplianceConnection("connected");
    });
    ws.addEventListener("message", (e) => {
      if (!isCurrentDropCopySocket(ws, generation)) return;
      try {
        const msg = JSON.parse(e.data);
        const entries = normaliseDropCopyEnvelope(msg);
        if (!entries) return;
        if (msg.type === "snapshot") {
          if (!snapshotStarted) {
            clearComplianceFeed();
            snapshotStarted = true;
          }
          snapshotChannels.add(msg.channel);
          if (snapshotChannels.size === DROP_COPY_CHANNELS.size) {
            dropCopyReconnectAttempt = 0;
          }
        }
        for (const entry of entries) appendComplianceFeed(entry);
      } catch { /* ignore malformed frames — reconnect will re-snapshot */ }
    });
    ws.addEventListener("error", () => {
      if (isCurrentDropCopySocket(ws, generation)) setComplianceConnection("error");
    });
    ws.addEventListener("close", () => {
      if (!isCurrentDropCopySocket(ws, generation)) return;
      dropCopySocket = null;
      scheduleDropCopyReconnect(generation);
    });
    dropCopySocket = ws;
    return ws;
  } catch (err) {
    console.warn("[compliance/dropcopy] open failed", err);
    setComplianceConnection("error");
    scheduleDropCopyReconnect(generation);
    return null;
  }
}

function isCurrentDropCopySocket(ws, generation) {
  return dropCopyActive && generation === dropCopyGeneration && dropCopySocket === ws;
}

function scheduleDropCopyReconnect(generation) {
  if (!dropCopyActive || generation !== dropCopyGeneration || dropCopyReconnectTimer != null) return;
  const delay = dropCopyReconnectDelayMs(dropCopyReconnectAttempt);
  dropCopyReconnectAttempt += 1;
  setComplianceConnection("reconnecting", delay);
  dropCopyReconnectTimer = setTimeout(() => {
    dropCopyReconnectTimer = null;
    connectDropCopy(generation);
  }, delay);
}

export function dropCopyReconnectDelayMs(attempt) {
  const exponent = Math.max(0, Math.floor(Number(attempt) || 0));
  return Math.min(DROP_COPY_RECONNECT_MAX_MS, DROP_COPY_RECONNECT_BASE_MS * (2 ** exponent));
}

// Convert the backend OutboundMessage JSON contract
// `{type, channel, seq, data}` into zero or more table rows. Snapshot
// data is an array; delta data is one DTO.
export function normaliseDropCopyEnvelope(msg) {
  if (!msg || typeof msg !== "object") return null;
  if ((msg.type !== "snapshot" && msg.type !== "delta") || !DROP_COPY_CHANNELS.has(msg.channel)) return null;
  const payloads = msg.type === "snapshot"
    ? (Array.isArray(msg.data) ? msg.data : null)
    : (msg.data && typeof msg.data === "object" && !Array.isArray(msg.data) ? [msg.data] : null);
  if (!payloads) return null;
  return payloads.map((payload) => normaliseDropCopyRow(msg.channel, msg.seq, payload));
}

function normaliseDropCopyRow(channel, seq, payload) {
  const isOrder = channel === "dropcopy.orders";
  const type = isOrder
    ? "Order"
    : (payload.kind ?? (channel === "dropcopy.fills" ? "Fill" : "Canceled"));
  return {
    timestamp: payload.timestampUtc ?? Date.now(),
    type,
    status: payload.status ?? "",
    symbol: payload.symbol ?? "",
    side: payload.side ?? "",
    qty: isOrder ? (payload.quantity ?? null) : (payload.lastQuantity ?? null),
    price: isOrder ? (payload.price ?? null) : (payload.lastPrice ?? null),
    clOrdId: payload.clOrdId ?? "",
    channel,
    seq,
  };
}

// ── Utils ──────────────────────────────────────────────────────────

function escapeHtml(s) {
  return String(s)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

export function yesterdayBrt() {
  // Yesterday in America/Sao_Paulo (UTC-3, no DST since 2019). Pure
  // arithmetic so we don't depend on Intl in tests.
  const now = new Date();
  const brt = new Date(now.getTime() - 3 * 60 * 60 * 1000);
  brt.setUTCDate(brt.getUTCDate() - 1);
  const yyyy = brt.getUTCFullYear();
  const mm = String(brt.getUTCMonth() + 1).padStart(2, "0");
  const dd = String(brt.getUTCDate()).padStart(2, "0");
  return `${yyyy}-${mm}-${dd}`;
}

// Returns the role → views map so app.js (and the unit test) share a
// single source of truth for nav-tab gating.
//
// Fase 1 (#397) reshape: every signed-in role gets Trading + History +
// Settings + Algos (Algos is disabled in the UI until Fase 2 lands but
// still belongs to the nav set — `handleSwitchView` blocks the actual
// activation). Admin layers on Admin + Compliance. Compliance role is
// pinned to its own console plus History so the user can audit their
// own session activity. `bot-credentials` is no longer a primary tab —
// it's reached from the Settings view.
export function tabsForRole(role) {
  if (role === "admin")      return ["trader", "algos", "history", "settings", "admin", "compliance"];
  if (role === "compliance") return ["compliance", "history"];
  return ["trader", "algos", "history", "settings"];
}

export function defaultViewForRole(role) {
  return role === "compliance" ? "compliance" : "trader";
}

export function reconcileComplianceRenewal({ role, currentView, onReopen, onLeave }) {
  if (currentView !== "compliance") return "unchanged";
  if (tabsForRole(role).includes("compliance")) {
    onReopen?.();
    return "reopen";
  }
  onLeave?.();
  return "leave";
}
