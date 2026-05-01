// App entry point: wires login → worker → state → UI together.

import { defaultBackend, login, submitOrder, cancelOrder } from "./protocol.js";
import * as state from "./state.js";
import * as ui from "./ui.js";

const SESSION_KEY = "b3tp.session";

let worker = null;
let session = null;          // { token, expiresAt, username, backend }
let expiryTimer = null;

function init() {
  document.getElementById("login-backend").placeholder = defaultBackend();
  document.getElementById("login-form").addEventListener("submit", onLogin);
  ui.bindUi();
  ui.setHandlers({
    onSubmitOrder: handleSubmitOrder,
    onCancelOrder: handleCancelOrder,
    onLogout: logout,
  });

  const stored = readSession();
  if (stored) startSession(stored);
  else ui.showLogin();
}

async function onLogin(e) {
  e.preventDefault();
  ui.setLoginError(null);
  const backend = (document.getElementById("login-backend").value || defaultBackend()).replace(/\/+$/, "");
  const username = document.getElementById("login-username").value.trim();
  const password = document.getElementById("login-password").value;
  try {
    const resp = await login(backend, username, password);
    const next = { token: resp.token, expiresAt: resp.expiresAt, username, backend };
    writeSession(next);
    startSession(next);
  } catch (err) {
    ui.setLoginError(err.message || "Login failed");
  }
}

function startSession(next) {
  session = next;
  state.setUser({ username: next.username, expiresAt: next.expiresAt, backend: next.backend });
  state.clearAll();
  state.setStatus("connecting");
  ui.showTrader();

  startWorker();
  scheduleExpiry();
}

function scheduleExpiry() {
  if (expiryTimer) clearTimeout(expiryTimer);
  if (!session?.expiresAt) return;
  const remaining = new Date(session.expiresAt).getTime() - Date.now();
  if (remaining <= 0) { logout(); return; }
  expiryTimer = setTimeout(logout, Math.max(1_000, remaining));
}

function startWorker() {
  worker = new Worker(new URL("./worker.js", import.meta.url), { type: "module" });
  worker.onmessage = (ev) => onWorkerMessage(ev.data);
  worker.postMessage({ type: "start", backend: session.backend, token: session.token });
}

function onWorkerMessage(msg) {
  switch (msg.type) {
    case "status":              state.setStatus(msg.value); break;
    case "clear":               state.clearAll(); break;
    case "orders.snapshot":     state.applyOrdersSnapshot(msg.data); break;
    case "orders.delta":        state.applyOrdersDelta(msg.data); break;
    case "positions.snapshot":  state.applyPositionsSnapshot(msg.data); break;
    case "positions.delta":     state.applyPositionsDelta(msg.data); break;
    case "executions.snapshot": state.applyExecutionsSnapshot(msg.data); break;
    case "executions.delta":    state.applyExecutionsDelta(msg.data); break;
    case "error":
      // A frame-level error from the server (e.g., unknown_channel).
      // Surface in the executions log to keep it visible without a toast.
      console.warn("[ws]", msg);
      break;
  }
}

async function handleSubmitOrder(payload) {
  if (!session) return;
  if (!payload.symbol)               return ui.setTicketFeedback("symbol required", "error");
  if (!Number.isFinite(payload.quantity) || payload.quantity <= 0)
    return ui.setTicketFeedback("quantity must be positive", "error");
  if (payload.type === "Limit" && (!Number.isFinite(payload.price) || payload.price <= 0))
    return ui.setTicketFeedback("limit price required", "error");

  ui.setTicketSubmitting(true);
  ui.setTicketFeedback(null);
  try {
    const resp = await submitOrder(session.backend, session.token, payload);
    ui.setTicketFeedback(`accepted: ${resp.clOrdId}${resp.status ? ` (${resp.status})` : ""}`, "ok");
    ui.clearTicket();
  } catch (err) {
    if (err.status === 401) { logout(); return; }
    ui.setTicketFeedback(err.message || "submit failed", "error");
  } finally {
    ui.setTicketSubmitting(false);
  }
}

async function handleCancelOrder(clOrdId) {
  if (!session) return;
  try {
    await cancelOrder(session.backend, session.token, clOrdId);
  } catch (err) {
    if (err.status === 401) { logout(); return; }
    ui.setTicketFeedback(`cancel failed: ${err.message}`, "error");
  }
}

function logout() {
  if (expiryTimer) { clearTimeout(expiryTimer); expiryTimer = null; }
  if (worker) {
    try { worker.postMessage({ type: "stop" }); } catch { /* swallow */ }
    worker.terminate();
    worker = null;
  }
  session = null;
  clearSession();
  state.setUser(null);
  state.setStatus("disconnected");
  state.clearAll();
  ui.showLogin();
}

function readSession() {
  try {
    const raw = sessionStorage.getItem(SESSION_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (!parsed.token || !parsed.expiresAt) return null;
    if (new Date(parsed.expiresAt).getTime() <= Date.now()) return null;
    return parsed;
  } catch { return null; }
}
function writeSession(s) { sessionStorage.setItem(SESSION_KEY, JSON.stringify(s)); }
function clearSession()  { sessionStorage.removeItem(SESSION_KEY); }

init();
