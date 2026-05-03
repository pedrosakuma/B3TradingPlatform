// App entry point: wires login → worker → state → UI together.

import { defaultBackend, login, submitOrder, cancelOrder, getAdminFirms,
         getKillStatus, killFirm, reviveFirm, killEndClient, reviveEndClient,
         runEod } from "./protocol.js";
import { claimsFromToken } from "./jwt.js";
import * as state from "./state.js";
import * as ui from "./ui.js";
import * as adminUi from "./adminUi.js";

const SESSION_KEY = "b3tp.session";
const MD_KEY = "b3tp.md";
const DEFAULT_WATCHLIST = ["PETR4", "VALE3"];
const FIRMS_POLL_INTERVAL_MS = 5_000;

let worker = null;
let mdWorker = null;
let session = null;          // { token, expiresAt, username, backend, role, firm }
let mdConfig = null;         // { url, symbols }
let expiryTimer = null;
let firmsPollTimer = null;

function init() {
  document.getElementById("login-backend").placeholder = defaultBackend();
  document.getElementById("login-form").addEventListener("submit", onLogin);
  ui.bindUi();
  adminUi.bindAdminUi();
  ui.setHandlers({
    onSubmitOrder: handleSubmitOrder,
    onCancelOrder: handleCancelOrder,
    onLogout: logout,
    onApplyMd: handleApplyMd,
    onSwitchView: handleSwitchView,
  });
  adminUi.setAdminHandlers({
    onToggleFirm:      handleToggleFirm,
    onToggleEndClient: handleToggleEndClient,
    onAddEndClient:    handleAddEndClient,
    onRunEod:          handleRunEod,
    onRefresh:         refreshAdminData,
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
    const claims = claimsFromToken(resp.token);
    const next = {
      token: resp.token,
      expiresAt: resp.expiresAt,
      username,
      backend,
      role: claims.role,
      firm: claims.firm,
    };
    writeSession(next);
    startSession(next);
  } catch (err) {
    ui.setLoginError(err.message || "Login failed");
  }
}

function startSession(next) {
  // Backfill claims for sessions persisted before this field existed.
  if (next.role == null || next.firm == null) {
    const claims = claimsFromToken(next.token);
    next = { ...next, role: next.role ?? claims.role, firm: next.firm ?? claims.firm };
  }
  session = next;
  state.setUser({
    username: next.username,
    expiresAt: next.expiresAt,
    backend: next.backend,
    role: next.role,
    firm: next.firm,
  });
  state.clearAll();
  state.setStatus("connecting");
  state.setSubmitInflight(null);
  state.setWsReconnect(null);
  state.setFirmsHealth(null);
  state.setKillStatus(null);
  state.setEodReport(null);
  state.setCurrentView("trader");
  ui.showTrader();

  startWorker();
  startMdWorker();
  startFirmsPoll();
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

function defaultMdUrl() {
  // Heuristic: same host as the trading-host backend with port 8081 and
  // ws:// scheme. Operators on a different topology can override.
  try {
    const u = new URL(session.backend);
    u.protocol = u.protocol === "https:" ? "wss:" : "ws:";
    u.port = "8081";
    u.pathname = "/ws";
    return u.toString();
  } catch { return ""; }
}

function readMdConfig() {
  try {
    const raw = sessionStorage.getItem(MD_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      if (parsed && typeof parsed.url === "string" && Array.isArray(parsed.symbols)) {
        return parsed;
      }
    }
  } catch { /* fall through */ }
  return { url: defaultMdUrl(), symbols: DEFAULT_WATCHLIST.slice() };
}

function writeMdConfig(cfg) { sessionStorage.setItem(MD_KEY, JSON.stringify(cfg)); }
function clearMdConfig()    { sessionStorage.removeItem(MD_KEY); }

function startMdWorker() {
  mdConfig = readMdConfig();
  ui.setMdInputs(mdConfig);
  state.setWatchlist(mdConfig.symbols);
  state.setMarketDataStatus("disconnected");
  if (!mdConfig.url) return; // user hasn't configured an endpoint yet

  mdWorker = new Worker(new URL("./mdWorker.js", import.meta.url), { type: "module" });
  mdWorker.onmessage = (ev) => onMdWorkerMessage(ev.data);
  mdWorker.postMessage({
    type: "start",
    url: mdConfig.url,
    symbols: mdConfig.symbols,
  });
}

function handleApplyMd({ url, symbols }) {
  if (!url) {
    ui.setMdFeedback("ws url required", "error");
    return;
  }
  const next = { url, symbols };
  // URL change forces a full restart (different endpoint = different
  // session / securityIds). Symbol-only changes go via setSymbols so
  // we don't blip the connection on every watchlist tweak.
  const urlChanged = !mdConfig || mdConfig.url !== url;
  mdConfig = next;
  writeMdConfig(next);
  state.setWatchlist(symbols);

  if (urlChanged || !mdWorker) {
    if (mdWorker) {
      try { mdWorker.postMessage({ type: "stop" }); } catch { /* swallow */ }
      mdWorker.terminate();
      mdWorker = null;
    }
    state.clearMarketData();
    startMdWorker();
  } else {
    mdWorker.postMessage({ type: "setSymbols", symbols });
    // Clear cache entries for symbols no longer in the watchlist.
    const wanted = new Set(symbols);
    for (const sym of [...state.getState().marketData.keys()]) {
      if (!wanted.has(sym)) state.removeMdSymbol(sym);
    }
  }
  ui.setMdFeedback(`watching ${symbols.length} symbol(s)`, "ok");
}

function onMdWorkerMessage(msg) {
  switch (msg.type) {
    case "md.status":   state.setMarketDataStatus(msg.value); break;
    case "md.clear":    state.clearMarketData(); break;
    case "md.trade":    state.applyMdTrade(msg); break;
    case "md.info":     state.applyMdInfo(msg); break;
    case "md.bust":
      // Risk consumers ignore busts (the next live trade overwrites);
      // surface in the executions log so the trader sees it happened.
      console.warn("[md] trade bust", msg);
      break;
    case "md.subError":
      ui.setMdFeedback(`subscribe ${msg.symbol}: ${msg.errorName}`, "error");
      state.removeMdSymbol(msg.symbol);
      break;
    case "md.removed":  state.removeMdSymbol(msg.symbol); break;
    case "md.error":    console.warn("[md]", msg); break;
  }
}

function onWorkerMessage(msg) {
  switch (msg.type) {
    case "status":              state.setStatus(msg.value); break;
    case "reconnect.scheduled":
      state.setWsReconnect(msg.nextAt ? { nextAt: msg.nextAt } : null);
      break;
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
  state.setSubmitInflight({ startedAt: Date.now() });
  try {
    const resp = await submitOrder(session.backend, session.token, payload);
    ui.setTicketFeedback(`accepted: ${resp.clOrdId}${resp.status ? ` (${resp.status})` : ""}`, "ok");
    ui.clearTicket();
  } catch (err) {
    if (err.status === 401) { logout(); return; }
    ui.setTicketFeedback(err.message || "submit failed", "error");
  } finally {
    ui.setTicketSubmitting(false);
    state.setSubmitInflight(null);
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
  stopFirmsPoll();
  if (worker) {
    try { worker.postMessage({ type: "stop" }); } catch { /* swallow */ }
    worker.terminate();
    worker = null;
  }
  if (mdWorker) {
    try { mdWorker.postMessage({ type: "stop" }); } catch { /* swallow */ }
    mdWorker.terminate();
    mdWorker = null;
  }
  session = null;
  mdConfig = null;
  clearSession();
  clearMdConfig();
  state.setUser(null);
  state.setStatus("disconnected");
  state.setMarketDataStatus("disconnected");
  state.setSubmitInflight(null);
  state.setWsReconnect(null);
  state.setFirmsHealth(null);
  state.setKillStatus(null);
  state.setEodReport(null);
  state.setCurrentView("trader");
  state.clearAll();
  state.clearMarketData();
  state.setWatchlist([]);
  ui.showLogin();
}

// ── Admin firms poll ───────────────────────────────────────────────
// Only admins can hit /admin/firms (backend returns 403 otherwise).
// We gate the call client-side too so the network panel stays clean.

function startFirmsPoll() {
  stopFirmsPoll();
  if (!session || session.role !== "admin") return;
  // Fire once immediately so the badge shows up without waiting a tick.
  pollFirmsOnce();
  firmsPollTimer = setInterval(pollFirmsOnce, FIRMS_POLL_INTERVAL_MS);
}

function stopFirmsPoll() {
  if (firmsPollTimer) { clearInterval(firmsPollTimer); firmsPollTimer = null; }
}

async function pollFirmsOnce() {
  if (!session) return;
  try {
    const [firms, kill] = await Promise.all([
      getAdminFirms(session.backend, session.token),
      getKillStatus(session.backend, session.token),
    ]);
    state.setFirmsHealth({ ...firms, fetchedAt: Date.now() });
    state.setKillStatus({
      firms: kill?.Firms ?? [],
      endClients: kill?.EndClients ?? [],
      fetchedAt: Date.now(),
    });
  } catch (err) {
    if (err.status === 401) { logout(); return; }
    // 403 here means the JWT role drifted; stop polling so we don't
    // hammer the endpoint with rejected calls.
    if (err.status === 403) { stopFirmsPoll(); return; }
    console.warn("[admin/poll]", err);
  }
}

// ── Admin view + actions ───────────────────────────────────────────

function handleSwitchView(view) {
  if (view === "admin" && session?.role !== "admin") return; // safety
  state.setCurrentView(view);
  if (view === "admin") refreshAdminData();
}

async function refreshAdminData() {
  await pollFirmsOnce();
}

async function withAdminCall(fn, okMessage) {
  try {
    await fn();
    adminUi.setAdminFeedback(okMessage, "ok");
    await pollFirmsOnce();
  } catch (err) {
    if (err.status === 401) { logout(); return; }
    if (err.status === 403) { adminUi.setAdminFeedback("forbidden — role lost?", "error"); return; }
    if (err.status === 503) { adminUi.setAdminFeedback("WAL backpressure — retry shortly", "error"); return; }
    if (err.status === 409) { adminUi.setAdminFeedback(err.message || "conflict", "error"); return; }
    adminUi.setAdminFeedback(err.message || "request failed", "error");
  }
}

function handleToggleFirm({ firmId, engage }) {
  withAdminCall(
    () => (engage ? killFirm : reviveFirm)(session.backend, session.token, firmId),
    `firm ${firmId}: ${engage ? "killed" : "revived"}`,
  );
}

function handleToggleEndClient({ id, engage }) {
  withAdminCall(
    () => (engage ? killEndClient : reviveEndClient)(session.backend, session.token, id),
    `end-client ${id}: ${engage ? "killed" : "revived"}`,
  );
}

function handleAddEndClient({ id }) {
  handleToggleEndClient({ id, engage: true });
}

async function handleRunEod() {
  try {
    const report = await runEod(session.backend, session.token);
    state.setEodReport({ ranAt: Date.now(), report });
    adminUi.setAdminFeedback("EOD report generated", "ok");
  } catch (err) {
    if (err.status === 401) { logout(); return; }
    if (err.status === 409) { adminUi.setAdminFeedback("persistence disabled — EOD unavailable", "error"); return; }
    adminUi.setAdminFeedback(err.message || "EOD failed", "error");
  }
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
