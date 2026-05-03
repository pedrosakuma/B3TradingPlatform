// REST client for the Phase 2 surface. The WebSocket lives in worker.js;
// this module is HTTP-only.

export function defaultBackend() {
  return location.hostname === "localhost" || location.hostname === "127.0.0.1"
    ? "http://localhost:5000"
    : location.origin;
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
