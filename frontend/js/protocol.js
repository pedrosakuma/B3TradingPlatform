// REST client for the Phase 2 surface. The WebSocket lives in worker.js;
// this module is HTTP-only.

export function defaultBackend() {
  // Prefer the page origin so requests go through the nginx reverse-proxy
  // (same-origin, no CORS). The legacy "localhost:5000" shortcut only kicks
  // in for non-http schemes (e.g. file://) where there is no real origin to
  // talk to. The login form lets the user override either way.
  if (location.protocol === "http:" || location.protocol === "https:") {
    return location.origin;
  }
  return "http://localhost:5000";
}

// Default WebSocket endpoint for the OPTIONAL B3MarketDataPlatform feed
// (DOB / candles / trade prints). Distinct origin from the trader WS, so
// it can't go through the nginx reverse-proxy. Convention: same host as
// the page, port 8081, /ws path. Returns "" off-localhost so non-dev
// deployments don't auto-attempt a guess that's likely wrong.
export function defaultMarketDataUrl() {
  if (location.protocol !== "http:" && location.protocol !== "https:") return "";
  if (location.hostname !== "localhost" && location.hostname !== "127.0.0.1") return "";
  const wsScheme = location.protocol === "https:" ? "wss:" : "ws:";
  return `${wsScheme}//${location.hostname}:8081/ws`;
}

async function jsonOrThrow(resp) {
  const text = await resp.text();
  let body;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  if (!resp.ok) {
    const message = (body && (body.error || body.message)) || `HTTP ${resp.status}`;
    const err = new Error(message);
    err.status = resp.status;
    err.body = body;
    throw err;
  }
  return body;
}

export async function login(backend, username, password) {
  const resp = await fetch(`${backend}/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username, password }),
  });
  return jsonOrThrow(resp);
}

// Self-service signup. Returns the same shape as /auth/login (token +
// expiresAt) so the caller can drop the new user straight into the
// trader view without a follow-up login round-trip. v0 is FIRM01-only,
// role=user — see backend AuthEndpoints for the policy.
export async function signup(backend, username, password) {
  const resp = await fetch(`${backend}/auth/signup`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username, password }),
  });
  return jsonOrThrow(resp);
}

// Cheap, side-effect-free probe used at boot to detect stored tokens
// that no longer authenticate (e.g. after the host's signing key was
// rotated). Returns true on 2xx, false on 401/403, throws on network
// errors so the caller can fall back to the optimistic path.
export async function validateSession(backend, token) {
  const resp = await fetch(`${backend}/positions`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (resp.status === 401 || resp.status === 403) return false;
  return resp.ok;
}

// Q1.4 (#256). Effective risk-policy values mirrored to the FE so the
// ticket validator can match the server cap on GTD horizon. The server
// stays authoritative — this is a hint, not a substitute. Returns
// `{ maxGtdHorizonDays: number }` on 2xx; throws on auth/network
// failure so the caller can decide to fall back silently.
export async function getRiskPolicy(backend, token) {
  const resp = await fetch(`${backend}/policy/risk`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

export async function submitOrder(backend, token, payload) {
  const resp = await fetch(`${backend}/orders`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify(payload),
  });
  return jsonOrThrow(resp);
}

export async function cancelOrder(backend, token, clOrdId) {
  const resp = await fetch(`${backend}/orders/${encodeURIComponent(clOrdId)}`, {
    method: "DELETE",
    headers: { Authorization: `Bearer ${token}` },
  });
  if (resp.status === 204 || resp.status === 404) return null;
  return jsonOrThrow(resp);
}

// Slice 5 of #122. Cancel-replace ("modify") the working order.
// Backend returns 202 + { ClOrdId, OriginalClOrdId } on accept, with
// the new ClOrdID being the venue-bound replacement. Other status
// codes (404 / 409 / 400 / 422 / 502 / 503) bubble up via jsonOrThrow
// so the caller can surface a user-readable reason.
export async function modifyOrder(backend, token, clOrdId, payload) {
  const resp = await fetch(`${backend}/orders/${encodeURIComponent(clOrdId)}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify(payload),
  });
  return jsonOrThrow(resp);
}

// Admin-only: per-firm operator visibility. Returns 403 for non-admin
// callers — the UI must gate the call by inspecting the JWT role
// before invoking this. Schema mirrors AdminEndpoints.MapAdmin /firms.
export async function getAdminFirms(backend, token) {
  const resp = await fetch(`${backend}/admin/firms`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

// Admin-only: current killswitch state (lists of killed firms /
// end-clients). Backend: GET /admin/kill -> { EndClients, Firms }.
export async function getKillStatus(backend, token) {
  const resp = await fetch(`${backend}/admin/kill`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

// Admin-only: toggle killswitch. POST = engage, DELETE = revive.
// Returns 204 on success, 503 on WAL backpressure.
async function toggleKill(backend, token, scope, id, engage) {
  const resp = await fetch(
    `${backend}/admin/kill/${scope}/${encodeURIComponent(id)}`,
    {
      method: engage ? "POST" : "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  if (resp.status === 204) return null;
  return jsonOrThrow(resp);
}

export const killFirm        = (b, t, id) => toggleKill(b, t, "firm",       id, true);
export const reviveFirm      = (b, t, id) => toggleKill(b, t, "firm",       id, false);
export const killEndClient   = (b, t, id) => toggleKill(b, t, "end-client", id, true);
export const reviveEndClient = (b, t, id) => toggleKill(b, t, "end-client", id, false);

// Admin-only: current per-symbol trading halt set.
// Backend: GET /admin/halts -> { Symbols: [...] }.
export async function getHaltStatus(backend, token) {
  const resp = await fetch(`${backend}/admin/halts`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

// Admin-only: toggle a symbol halt. POST = halt, DELETE = resume.
// Returns 204 on success, 503 on WAL backpressure.
async function toggleHalt(backend, token, symbol, halt) {
  const resp = await fetch(
    `${backend}/admin/halts/${encodeURIComponent(symbol)}`,
    {
      method: halt ? "POST" : "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  if (resp.status === 204) return null;
  return jsonOrThrow(resp);
}

export const haltSymbol   = (b, t, sym) => toggleHalt(b, t, sym, true);
export const resumeSymbol = (b, t, sym) => toggleHalt(b, t, sym, false);

// Admin-only: trigger EOD materialisation. Returns the report or 409
// when persistence is disabled.
export async function runEod(backend, token) {
  const resp = await fetch(`${backend}/admin/eod`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

// ── Q2.6 (#273). History / P&L / Statement (read-side) ────────────
// All four endpoints are JWT-scoped to the caller's `sub` claim. The
// history endpoints accept ISO-8601 `from` / `to` plus an optional
// `symbol` filter and return `{ items, nextCursor }`; nextCursor is
// `null` when the server has no further pages. `cursor` is treated as
// an opaque token — never parse it client-side. Limit is clamped
// server-side (cap = 500), so the FE picks a friendlier default.

function _appendHistoryQuery(url, { from, to, symbol, cursor, limit } = {}) {
  if (from)   url.searchParams.set("from", from);
  if (to)     url.searchParams.set("to", to);
  if (symbol) url.searchParams.set("symbol", symbol);
  if (cursor) url.searchParams.set("cursor", cursor);
  if (limit)  url.searchParams.set("limit", String(limit));
}

export async function getOrdersHistory(backend, token, opts = {}) {
  const url = new URL(`${backend}/orders/history`);
  _appendHistoryQuery(url, opts);
  const resp = await fetch(url.toString(), {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

export async function getExecutionsHistory(backend, token, opts = {}) {
  const url = new URL(`${backend}/executions/history`);
  _appendHistoryQuery(url, opts);
  const resp = await fetch(url.toString(), {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

export async function getPnlToday(backend, token) {
  const resp = await fetch(`${backend}/pnl/today`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

// Q2.6 (#273). Statement JSON for a specific dayKey (YYYY-MM-DD), or
// today when dayKey is null/empty. Backend serves /statement/{dayKey?}
// — the trailing slash variant returns today.
export async function getStatement(backend, token, dayKey) {
  const path = dayKey ? `/statement/${encodeURIComponent(dayKey)}` : `/statement`;
  const resp = await fetch(`${backend}${path}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

// Q2.6 (#273). Parse the RFC-6266 / RFC-2616 Content-Disposition header
// to extract the server-suggested filename. Exported so the FE download
// path and its unit tests can share the exact same logic — the browser
// applies the header automatically to anchor downloads, but here we
// fetch into a blob and trigger via `URL.createObjectURL`, so we must
// honor the header ourselves to avoid emitting a generic "download"
// name. Returns null when the header is absent or unparseable.
export function parseContentDispositionFilename(header) {
  if (!header || typeof header !== "string") return null;
  // Prefer RFC-5987 filename* (UTF-8 capable, used by ASP.NET when the
  // filename contains non-ASCII or punctuation). Falls back to plain
  // filename= when filename* is absent.
  const star = /filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)/i.exec(header);
  if (star && star[1]) {
    try { return decodeURIComponent(star[1].trim()); } catch { /* fall through */ }
  }
  const plain = /filename\s*=\s*("([^"]+)"|([^;]+))/i.exec(header);
  if (plain) {
    const v = (plain[2] ?? plain[3] ?? "").trim();
    if (v) return v;
  }
  return null;
}

// Q2.6 (#273). CSV statement download. Returns `{ blob, filename }`
// — the caller is responsible for wiring it through URL.createObjectURL
// + a synthetic anchor click (kept here so unit tests can stub fetch
// without dragging in the browser download plumbing).
export async function downloadStatementCsv(backend, token, dayKey) {
  if (!dayKey) throw new Error("dayKey is required for CSV download");
  const url = `${backend}/statement/${encodeURIComponent(dayKey)}.csv`;
  const resp = await fetch(url, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!resp.ok) {
    // Mirror jsonOrThrow's error surface so callers can branch on
    // err.status (e.g. 401 → logout) without special-casing the CSV path.
    let body = null;
    try { body = await resp.text(); } catch { /* ignore */ }
    const err = new Error(`HTTP ${resp.status}`);
    err.status = resp.status;
    err.body = body;
    throw err;
  }
  const blob = await resp.blob();
  const filename = parseContentDispositionFilename(resp.headers.get("Content-Disposition"))
    || `statement-${dayKey}.csv`;
  return { blob, filename };
}

// All operations act on the authenticated user's `sub` claim — the backend
// scopes by JWT, so no user-id parameter is sent. Cross-user reads/writes
// always 404, so the UI only ever sees its own caller's rows.

// GET /api/user-bot-credentials -> [{ id, label, credShortId, createdAtUtc, revokedAt }]
// Read-side DTO; never includes the bearer secret.
//
// ── User-bot credentials (sub-issue #169 of RFC user-bot-fixp-listener-v0).
// All operations act on the authenticated user's `sub` claim — the backend
// scopes by JWT, so no user-id parameter is sent. Cross-user reads/writes
// always 404, so the UI only ever sees its own caller's rows.
export async function listUserBotCredentials(backend, token) {
  const resp = await fetch(`${backend}/api/user-bot-credentials`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

// POST /api/user-bot-credentials { label } -> 201 with the same shape
// PLUS a `plainSecret` field. This is the ONLY response that carries
// the plaintext PAT (`b3t_xxx_yyy`) — the platform discards the secret
// after returning it, so callers MUST surface it to the user immediately.
// Never persist `plainSecret` to storage; keep it in component state and
// drop it as soon as the user dismisses the "shown once" modal.
export async function createUserBotCredential(backend, token, label) {
  const resp = await fetch(`${backend}/api/user-bot-credentials`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify({ label }),
  });
  return jsonOrThrow(resp);
}

// DELETE /api/user-bot-credentials/{id} -> 204 on success, 404 if
// the credential never belonged to this user (oracle-safe). Idempotent.
export async function deleteUserBotCredential(backend, token, id) {
  const resp = await fetch(
    `${backend}/api/user-bot-credentials/${encodeURIComponent(id)}`,
    {
      method: "DELETE",
      headers: { Authorization: `Bearer ${token}` },
    });
  if (resp.status === 204 || resp.status === 404) return null;
  return jsonOrThrow(resp);
}
