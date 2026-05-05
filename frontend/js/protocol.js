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

// Admin-only: trigger EOD materialisation. Returns the report or 409
// when persistence is disabled.
export async function runEod(backend, token) {
  const resp = await fetch(`${backend}/admin/eod`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` },
  });
  return jsonOrThrow(resp);
}
