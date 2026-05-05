// App entry point: wires login → worker → state → UI together.

import { defaultBackend, login, submitOrder, cancelOrder, getAdminFirms,
         validateSession,
         getKillStatus, killFirm, reviveFirm, killEndClient, reviveEndClient,
         runEod } from "./protocol.js";
import { claimsFromToken } from "./jwt.js";
import { validateOrder, fatFingerCheck, payloadKey } from "./validation.js";
import * as state from "./state.js";
import * as ui from "./ui.js";
import * as adminUi from "./adminUi.js";
import { FLAGS } from "./mdProtocol.js";

const SESSION_KEY = "b3tp.session";
const MD_KEY = "b3tp.md";
const BLOTTER_FILTER_KEY = "b3tp.blotter.filter";
const DEFAULT_WATCHLIST = ["PETR4", "VALE3"];
const FIRMS_POLL_INTERVAL_MS = 5_000;

// ─────────────────────────────────────────────────────────────────
// Session storage strategy:
//   - sessionStorage (default): cleared when the tab/window closes,
//     mitigates token theft if the workstation walks away.
//   - localStorage ("Remember me" checkbox): persists across browser
//     restarts, so a refresh after coffee keeps the operator logged
//     in. Trade-off: a token sitting in localStorage is reachable by
//     any later XSS bug for as long as the JWT TTL allows.
//   We always read from both and prefer the freshest valid record so
//   a logout from one tab doesn't leave stale state in the other.
// ─────────────────────────────────────────────────────────────────

let sessionStore = sessionStorage; // mutates if "remember me" was selected at login
let renewInflight = false;
let warningShown = false;

const SESSION_WARNING_LEAD_MS = 60_000;

let worker = null;
let mdWorker = null;
let session = null;          // { token, expiresAt, username, backend, role, firm, remember }
let mdConfig = null;         // { url, symbols }
let expiryTimer = null;
let warningTimer = null;
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
    onBlotterFilter: handleBlotterFilter,
    onSelectOrder: handleSelectOrder,
    onKeyboardCancel: handleKeyboardCancel,
    onSelectChartResolution: state.setChartResolution,
    onSelectSymbol: handleSelectSymbol,
    onToggleTapeShowAll: state.setTapeShowAll,
  });
  adminUi.setAdminHandlers({
    onToggleFirm:      handleToggleFirm,
    onToggleEndClient: handleToggleEndClient,
    onAddEndClient:    handleAddEndClient,
    onRunEod:          handleRunEod,
    onRefresh:         refreshAdminData,
  });

  const stored = readSession();
  if (stored) {
    // Probe before we commit: a token that survived a host signing-key
    // rotation will look fresh client-side (expiresAt in the future)
    // but be rejected by the backend on the very first WS upgrade.
    // Showing the "session expiring" modal in that case is confusing
    // because the user never logged in this run. Drop silently and
    // fall back to login if the probe fails.
    validateSession(stored.backend, stored.token)
      .then((ok) => {
        if (ok) startSession(stored);
        else { clearSession(); ui.showLogin(); }
      })
      .catch(() => {
        // Network error — give the optimistic path a chance; if the
        // token is genuinely bad, /orders / WS will surface it later.
        startSession(stored);
      });
  } else {
    ui.showLogin();
  }
}

async function onLogin(e) {
  e.preventDefault();
  ui.setLoginError(null);
  const backend = (document.getElementById("login-backend").value || defaultBackend()).replace(/\/+$/, "");
  const username = document.getElementById("login-username").value.trim();
  const password = document.getElementById("login-password").value;
  const remember = !!document.getElementById("login-remember")?.checked;
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
      remember,
    };
    sessionStore = remember ? localStorage : sessionStorage;
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
  if (expiryTimer) { clearTimeout(expiryTimer); expiryTimer = null; }
  if (warningTimer) { clearTimeout(warningTimer); warningTimer = null; }
  if (!session?.expiresAt) return;
  const remaining = new Date(session.expiresAt).getTime() - Date.now();
  if (remaining <= 0) { logout(); return; }

  // Warning fires SESSION_WARNING_LEAD_MS before the hard expiry. If
  // the lead time has already passed (short-lived tokens), fire it
  // immediately. The hard expiry timer is kept so logout always wins
  // even if the user walks away from the warning prompt.
  const warnIn = Math.max(0, remaining - SESSION_WARNING_LEAD_MS);
  warningTimer = setTimeout(showSessionWarning, warnIn);
  expiryTimer  = setTimeout(logout, Math.max(1_000, remaining));
}

function showSessionWarning() {
  if (!session || warningShown) return;
  warningShown = true;
  ui.openSessionModal({
    onRenew: handleRenewSession,
    onLogout: logout,
  });
}

async function handleRenewSession(password) {
  if (!session || renewInflight) return;
  renewInflight = true;
  try {
    const resp = await login(session.backend, session.username, password);
    const claims = claimsFromToken(resp.token);
    session = {
      ...session,
      token: resp.token,
      expiresAt: resp.expiresAt,
      role: claims.role ?? session.role,
      firm: claims.firm ?? session.firm,
    };
    writeSession(session);
    state.setUser({
      username: session.username,
      expiresAt: session.expiresAt,
      backend:   session.backend,
      role:      session.role,
      firm:      session.firm,
    });
    // Restart the WS worker so subsequent reconnects use the new token
    // (the existing socket keeps working until the OLD JWT is rejected
    // server-side; restart guarantees the next reconnect is clean).
    restartWorker();
    warningShown = false;
    scheduleExpiry();
    ui.closeSessionModal();
  } catch (err) {
    ui.setSessionModalError(err.message || "renew failed");
  } finally {
    renewInflight = false;
  }
}

function startWorker() {
  worker = new Worker(new URL("./worker.js", import.meta.url), { type: "module" });
  worker.onmessage = (ev) => onWorkerMessage(ev.data);
  worker.postMessage({ type: "start", backend: session.backend, token: session.token });
}

function restartWorker() {
  if (worker) {
    try { worker.postMessage({ type: "stop" }); } catch { /* swallow */ }
    worker.terminate();
    worker = null;
  }
  state.setStatus("connecting");
  state.setWsReconnect(null);
  startWorker();
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
    state.clearAllBooks();
    state.clearAllCandles();
    state.clearAllTape();
    mbpEnabled = false;
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

// MBP is sticky: once enabled in a session, we leave the flag on even
// if the trader closes the DOB panel. Toggling MBP re-subscribes every
// symbol (server allocates fresh securityIds per flag set), which would
// briefly blank market-data and trade-tape — not worth it.
let mbpEnabled = false;

function handleSelectSymbol(symbol) {
  // Single global selector drives DOB, chart and tape. As soon as the
  // user picks anything we promote the MD subscription to MBP so the
  // book panel can render depth (the default is TRADES|INFO only).
  state.setSelectedSymbol(symbol || null);
  if (!symbol || !mdWorker || mbpEnabled) return;
  const flags = FLAGS.TRADES | FLAGS.INFO | FLAGS.MBP;
  mdWorker.postMessage({ type: "setFlags", flags });
  mbpEnabled = true;
}

function onMdWorkerMessage(msg) {
  switch (msg.type) {
    case "md.status":   state.setMarketDataStatus(msg.value); break;
    case "md.clear":
      state.clearMarketData();
      state.clearAllBooks();
      state.clearAllCandles();
      state.clearAllTape();
      break;
    case "md.trade":    state.applyMdTrade(msg); break;
    case "md.info":     state.applyMdInfo(msg); break;
    case "md.bust":     state.applyMdTradeBust(msg); break;
    case "md.subError":
      ui.setMdFeedback(`subscribe ${msg.symbol}: ${msg.errorName}`, "error");
      state.removeMdSymbol(msg.symbol);
      state.removeBookSymbol(msg.symbol);
      state.removeCandlesSymbol(msg.symbol);
      break;
    case "md.removed":
      state.removeMdSymbol(msg.symbol);
      state.removeBookSymbol(msg.symbol);
      state.removeCandlesSymbol(msg.symbol);
      break;
    case "md.book.snapshot":   state.applyMdBookSnapshot(msg); break;
    case "md.book.cleared":    state.applyMdBookCleared(msg); break;
    case "md.level.snapshot":  state.applyMdLevelSnapshot(msg); break;
    case "md.level.update":    state.applyMdLevelUpdate(msg); break;
    case "md.level.deleted":   state.applyMdLevelDeleted(msg); break;
    case "md.candle.snapshot": state.applyMdCandleSnapshot(msg); break;
    case "md.candle.update":   state.applyMdCandleUpdate(msg); break;
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

  const error = validateOrder(payload);
  if (error) return ui.setTicketFeedback(error.message, "error");

  // Fat-finger guard: first attempt with a >threshold deviation from
  // the last observed trade is rejected with a warning; the same exact
  // payload submitted again within the pending window goes through.
  const lastPrice = state.getState().marketData.get(payload.symbol)?.lastPrice;
  const ff = fatFingerCheck(payload, lastPrice);
  const key = payloadKey(payload);
  const pending = state.getState().pendingFatFinger;
  if (ff && (!pending || pending.key !== key)) {
    state.setPendingFatFinger(payload, key);
    const pct = (ff.deviation * 100).toFixed(1);
    ui.setTicketFeedback(
      `fat-finger guard: price deviates ${pct}% from last trade ${ff.lastPrice}. Click Submit again to override.`,
      "warn",
    );
    return;
  }
  // Clear pending guard once the user confirms or moves on.
  state.setPendingFatFinger(null);

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

// ── Blotter UX ─────────────────────────────────────────────────────

function handleBlotterFilter(filter) {
  state.setBlotterFilter(filter);
  writeBlotterFilter(filter);
}

function handleSelectOrder(clOrdId) {
  state.setSelectedOrder(clOrdId);
}

function handleKeyboardCancel() {
  const id = state.getState().selectedClOrdId;
  if (!id) return;
  const order = state.getState().orders.get(id);
  if (!order || ["Filled", "Cancelled", "Rejected"].includes(order.status)) return;
  if (!window.confirm(`Cancel order ${id}?`)) return;
  handleCancelOrder(id);
}

function readBlotterFilter() {
  try {
    const raw = sessionStorage.getItem(BLOTTER_FILTER_KEY);
    if (!raw) return { text: "", status: "" };
    const parsed = JSON.parse(raw);
    return {
      text:   typeof parsed?.text   === "string" ? parsed.text   : "",
      status: typeof parsed?.status === "string" ? parsed.status : "",
    };
  } catch { return { text: "", status: "" }; }
}
function writeBlotterFilter(f) { sessionStorage.setItem(BLOTTER_FILTER_KEY, JSON.stringify(f)); }

function logout() {
  if (expiryTimer) { clearTimeout(expiryTimer); expiryTimer = null; }
  if (warningTimer) { clearTimeout(warningTimer); warningTimer = null; }
  warningShown = false;
  ui.closeSessionModal();
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
  mbpEnabled = false;
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
  state.setBlotterFilter(readBlotterFilter());
  state.setSelectedOrder(null);
  state.setPendingFatFinger(null);
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
  // Read both stores; pick the freshest non-expired one. If "remember
  // me" was used, it lives in localStorage. Otherwise, sessionStorage.
  const candidates = [];
  for (const store of [localStorage, sessionStorage]) {
    try {
      const raw = store.getItem(SESSION_KEY);
      if (!raw) continue;
      const parsed = JSON.parse(raw);
      if (!parsed.token || !parsed.expiresAt) continue;
      if (new Date(parsed.expiresAt).getTime() <= Date.now()) continue;
      candidates.push({ store, parsed });
    } catch { /* fall through */ }
  }
  if (!candidates.length) return null;
  candidates.sort((a, b) =>
    new Date(b.parsed.expiresAt).getTime() - new Date(a.parsed.expiresAt).getTime());
  const winner = candidates[0];
  sessionStore = winner.store;
  return winner.parsed;
}
function writeSession(s) {
  sessionStore.setItem(SESSION_KEY, JSON.stringify(s));
  // Make sure the other store doesn't shadow our write.
  const other = sessionStore === localStorage ? sessionStorage : localStorage;
  try { other.removeItem(SESSION_KEY); } catch { /* swallow */ }
}
function clearSession() {
  try { localStorage.removeItem(SESSION_KEY); } catch { /* swallow */ }
  try { sessionStorage.removeItem(SESSION_KEY); } catch { /* swallow */ }
}

init();
