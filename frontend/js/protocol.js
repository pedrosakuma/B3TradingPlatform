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

// Deploy-time WebSocket endpoint for the OPTIONAL B3MarketDataPlatform feed
// (DOB / candles / trade prints). Rendered into js/env.js from the
// MARKETDATA_WS_URL env var (see env.js.template / 20-render-env-js.sh).
// This is operator-controlled config, not an end-user preference.
export function configuredMarketDataUrl() {
  const configured = globalThis.window?.__B3_CONFIG__?.marketDataWsUrl;
  return typeof configured === "string" ? configured : "";
}

// Localhost/127.0.0.1 convenience guess retained for non-Settings callers
// that explicitly want the old dev fallback behavior.
export function defaultMarketDataUrl() {
  const configured = configuredMarketDataUrl();
  if (configured !== "") return configured;
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

export async function exchangeExternalToken(backend, accessToken) {
  const resp = await fetch(`${backend}/auth/exchange`, {
    method: "POST",
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  return jsonOrThrow(resp);
}

// #303. Submits a 2FA verification: either a TOTP code or a recovery
// code (server detects via length/shape). When `totpChallengeToken` is
// supplied this finishes the login flow and returns `{ token, expiresAt }`;
// when omitted, the request must be authenticated and confirms a pending
// enrollment.
export async function verifyTotp(backend, { code, totpChallengeToken, token }) {
  const headers = { "Content-Type": "application/json" };
  if (token) headers.Authorization = `Bearer ${token}`;
  const body = totpChallengeToken ? { code, totpChallengeToken } : { code };
  const resp = await fetch(`${backend}/auth/2fa/verify`, {
    method: "POST",
    headers,
    body: JSON.stringify(body),
  });
  return jsonOrThrow(resp);
}

export async function getTotpStatus(backend, token) {
  const resp = await fetch(`${backend}/auth/2fa/status`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

// Begin TOTP enrollment. Returns `{ secret, otpauthUri, recoveryCodes,
// totpChallengeToken }`; the challenge is present for mandatory enrollment
// before the user owns a JWT.
// Recovery codes are shown ONCE — store them client-side until the user
// acknowledges, then discard.
export async function enrollTotp(backend, token, enrollmentToken) {
  const headers = { "Content-Type": "application/json" };
  if (token) headers.Authorization = `Bearer ${token}`;
  const resp = await fetch(`${backend}/auth/2fa/enroll`, {
    method: "POST",
    headers,
    body: JSON.stringify(enrollmentToken ? { enrollmentToken } : {}),
  });
  return jsonOrThrow(resp);
}

// Disable TOTP. Requires a currently-valid TOTP code (or recovery code).
export async function disableTotp(backend, token, code) {
  const resp = await fetch(`${backend}/auth/2fa/disable`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify({ code }),
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

// ── Fase 2 (#398). Algos REST. ─────────────────────────────────────
// Routes are JWT-scoped to the caller's owner+firm. listAlgos accepts
// includeTerminal (default false). createAlgo body shape must match
// AlgoEndpoints.CreateAlgoRequest verbatim — the per-type sub-object
// is read based on `type`. modifyAlgo requires at least one of
// newQuantity / newPrice. cancelAlgo returns 202 + JSON envelope on
// accept and 404 / 409 on miss / terminal — all surfaced via
// jsonOrThrow so the UI can show a readable reason.
export async function listAlgos(backend, token, { includeTerminal = false } = {}) {
  const url = new URL(`${backend}/algo/`);
  if (includeTerminal) url.searchParams.set("includeTerminal", "true");
  const resp = await fetch(url.toString(), {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

export async function getAlgo(backend, token, algoId) {
  const resp = await fetch(`${backend}/algo/${encodeURIComponent(algoId)}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

export async function createAlgo(backend, token, payload) {
  const resp = await fetch(`${backend}/algo`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify(payload),
  });
  return jsonOrThrow(resp);
}

export async function cancelAlgo(backend, token, algoId) {
  const resp = await fetch(`${backend}/algo/${encodeURIComponent(algoId)}`, {
    method: "DELETE",
    headers: { Authorization: `Bearer ${token}` },
  });
  if (resp.status === 204 || resp.status === 404) return null;
  return jsonOrThrow(resp);
}

export async function modifyAlgo(backend, token, algoId, payload) {
  const resp = await fetch(`${backend}/algo/${encodeURIComponent(algoId)}/modify`, {
    method: "POST",
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

function responseContentTypeMatches(header, expectedTypes) {
  if (!header || typeof header !== "string") return false;
  const actual = header.split(";")[0]?.trim().toLowerCase();
  if (!actual) return false;
  return expectedTypes.some((expected) => actual === String(expected).toLowerCase());
}

function throwUnexpectedDownloadContentType(resp, expectedLabel, expectedTypes) {
  const actual = resp.headers.get("Content-Type");
  if (responseContentTypeMatches(actual, expectedTypes)) return;
  const suffix = actual ? `, got ${actual}` : "";
  throw new Error(`unexpected response from server (expected ${expectedLabel}${suffix})`);
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
  throwUnexpectedDownloadContentType(resp, "CSV", ["text/csv"]);
  const blob = await resp.blob();
  const filename = parseContentDispositionFilename(resp.headers.get("Content-Disposition"))
    || `statement-${dayKey}.csv`;
  return { blob, filename };
}

// All operations act on the authenticated user's `sub` claim — the backend
// scopes by JWT, so no user-id parameter is sent. Cross-user reads/writes
// always 404, so the UI only ever sees its own caller's rows.

// GET /api/user-bot-credentials -> [{ id, label, credShortId, createdAtUtc, revokedAt, boundCertThumbprint }]
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

// POST /api/user-bot-credentials { label, boundCertThumbprint? } -> 201
// with the same shape PLUS a `plainSecret` field. This is the ONLY response that carries
// the plaintext PAT (`b3t_xxx_yyy`) — the platform discards the secret
// after returning it, so callers MUST surface it to the user immediately.
// `boundCertThumbprint` is a non-secret nullable SHA-256 client-cert pin
// (64 uppercase hex chars) and may be omitted/empty to leave the credential
// unpinned.
// Never persist `plainSecret` to storage; keep it in component state and
// drop it as soon as the user dismisses the "shown once" modal.
export async function createUserBotCredential(backend, token, label, boundCertThumbprint = null) {
  const body = { label };
  const normalizedThumbprint = String(boundCertThumbprint ?? "").trim();
  if (normalizedThumbprint) body.boundCertThumbprint = normalizedThumbprint;
  const resp = await fetch(`${backend}/api/user-bot-credentials`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify(body),
  });
  return jsonOrThrow(resp);
}

// PUT /api/user-bot-credentials/{id}/cert-binding { boundCertThumbprint }
// -> 204 on success, 404 if the credential never belonged to this user,
// 400 if the nullable non-secret SHA-256 client-cert pin is malformed.
// Send null (or an empty value that normalizes to null here) to clear it.
export async function setUserBotCredentialCertBinding(backend, token, id, boundCertThumbprint) {
  const normalizedThumbprint = String(boundCertThumbprint ?? "").trim();
  const resp = await fetch(
    `${backend}/api/user-bot-credentials/${encodeURIComponent(id)}/cert-binding`,
    {
      method: "PUT",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
      body: JSON.stringify({
        boundCertThumbprint: normalizedThumbprint ? normalizedThumbprint : null,
      }),
    });
  if (resp.status === 204) return null;
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

// ── Q4.14 (#314). Compliance role surfaces ─────────────────────────
// Audit-log query, best-execution touch lookup, CVM report download,
// and the drop-copy WebSocket URL helper. All four are read-only;
// none accept POST/PUT/DELETE on the compliance side.

// GET /admin/audit — admin or compliance. Compliance is firm-scoped
// server-side (the caller's JWT firm forces a filter; ?firmId= is
// ignored for compliance). Filters are all optional; omitted entries
// are not sent so the server applies its defaults (last 24h, limit
// 100). Returns `{ entries, nextCursor }`.
export async function searchAuditLog(backend, token, opts = {}) {
  const url = new URL(`${backend}/admin/audit`);
  if (opts.since)    url.searchParams.set("since",   opts.since);
  if (opts.until)    url.searchParams.set("until",   opts.until);
  if (opts.user)     url.searchParams.set("user",    opts.user);
  if (opts.type)     url.searchParams.set("type",    opts.type);
  if (opts.outcome)  url.searchParams.set("outcome", opts.outcome);
  if (opts.limit)    url.searchParams.set("limit",   String(opts.limit));
  if (opts.cursor)   url.searchParams.set("cursor",  opts.cursor);
  const resp = await fetch(url.toString(), {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

// GET /fills/{id}/touch — best-execution book-touch snapshot for a
// specific fill, keyed by the canonical id `{ClOrdId}:{cumQty}`.
// Returns `{ BestBid, BestAsk, MidPrice, LastTradePrice,
// CapturedAtUtc, Stale }`. 404 when the fill is unknown OR belongs
// to a different firm (no existence leak).
export async function getFillTouch(backend, token, id) {
  if (!id) throw new Error("fill id is required");
  const resp = await fetch(
    `${backend}/fills/${encodeURIComponent(id)}/touch`,
    { headers: { Authorization: `Bearer ${token}` } });
  return jsonOrThrow(resp);
}

// GET /reports/cvm/{model}/{yyyy-MM-dd} — streams an XML body.
// Returns `{ blob, filename }`. The caller wires it through
// URL.createObjectURL + a synthetic anchor click (kept here so unit
// tests can stub fetch without touching the browser download
// plumbing — same shape as downloadStatementCsv). Throws with
// `err.status` on 4xx/5xx so the caller can branch on 404
// (no rows) / 429 (rate-limited) / 503 (WAL backpressure).
export async function downloadCvmReport(backend, token, model, dayKey) {
  if (model !== 35 && model !== 505 && model !== "35" && model !== "505")
    throw new Error("model must be 35 or 505");
  if (!dayKey) throw new Error("dayKey is required");
  const url = `${backend}/reports/cvm/${model}/${encodeURIComponent(dayKey)}`;
  const resp = await fetch(url, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!resp.ok) {
    let body = null;
    try { body = await resp.text(); } catch { /* ignore */ }
    const err = new Error(`HTTP ${resp.status}`);
    err.status = resp.status;
    err.body = body;
    throw err;
  }
  throwUnexpectedDownloadContentType(resp, "XML", ["application/xml", "text/xml"]);
  const blob = await resp.blob();
  const compact = String(dayKey).replace(/-/g, "");
  const filename = parseContentDispositionFilename(resp.headers.get("Content-Disposition"))
    || `cvm_${model}_${compact}.xml`;
  return { blob, filename };
}

// FE-OPT-2 (#498). Fetch option chain for an underlying symbol.
// Returns array of { symbol, securityId, securityType, strikePrice,
// expirationDate, putOrCall, underlyingSymbol, contractMultiplier }.
export async function getInstruments(backend, token, { underlying, type } = {}) {
  const params = new URLSearchParams();
  if (underlying) params.set("underlying", underlying);
  if (type) params.set("type", type);
  const qs = params.toString();
  const url = `${backend}/instruments${qs ? "?" + qs : ""}`;
  const resp = await fetch(url, {
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}

// Build the WebSocket URL for the drop-copy feed. Browsers cannot
// set Authorization on a WS handshake; the JWT travels as
// ?access_token= (per the host's JwtBearerEvents convention for
// /ws/*). Operators must redact this query parameter from access
// logs. Pure builder so unit tests can verify the resulting URL.
export function buildDropCopyWebSocketUrl(backend, token) {
  if (!token) throw new Error("token is required for drop-copy websocket");
  const u = new URL("/ws/dropcopy", backend);
  u.protocol = u.protocol === "https:" ? "wss:" : "ws:";
  u.searchParams.set("access_token", token);
  return u.toString();
}
