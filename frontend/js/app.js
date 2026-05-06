// App entry point: wires login → worker → state → UI together.

import { defaultBackend, defaultMarketDataUrl, login, signup, submitOrder, cancelOrder, getAdminFirms,
         validateSession,
         getKillStatus, killFirm, reviveFirm, killEndClient, reviveEndClient,
         getHaltStatus, haltSymbol, resumeSymbol,
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
  const signupBackendInput = document.getElementById("signup-backend");
  if (signupBackendInput) signupBackendInput.placeholder = defaultBackend();
  const signupForm = document.getElementById("signup-form");
  if (signupForm) signupForm.addEventListener("submit", onSignup);
  document.getElementById("login-go-signup")?.addEventListener("click", () => showSignupCard(true));
  document.getElementById("signup-go-login")?.addEventListener("click", () => showSignupCard(false));
  ui.bindUi();
  adminUi.bindAdminUi();
  ui.setHandlers({
    onSubmitOrder: handleSubmitOrder,
    onCancelOrder: handleCancelOrder,
    onLogout: logout,
    onApplyMd: handleApplyMd,
    onSwitchView: handleSwitchView,
    onBlotterFilter: handleBlotterFilter,
    onBlotterPage: handleBlotterPage,
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
    onToggleHalt:      handleToggleHalt,
    onAddHalt:         handleAddHalt,
    onRunEod:          handleRunEod,
    onRefresh:         refreshAdminData,
  });

  const stored = readSession();
  if (stored) {
    // Boot guard: if the stored session is already inside the warning
    // window (or past its expiry), don't even attempt to adopt it.
    // Showing the "session expiring" modal as the very first thing the
    // user sees on page load is confusing — they have no context. Treat
    // it as expired and require a fresh login.
    const expiresAtMs = Date.parse(stored.expiresAt || "");
    const remaining = Number.isFinite(expiresAtMs) ? expiresAtMs - Date.now() : -1;
    if (remaining <= SESSION_WARNING_LEAD_MS) {
      clearSession();
      ui.showLogin();
      return;
    }
    // Probe before we commit: a token that survived a host signing-key
    // rotation will look fresh client-side (expiresAt in the future)
    // but be rejected by the backend on the very first WS upgrade.
    // Showing the "session expiring" modal in that case is confusing
    // because the user never logged in this run. Drop silently and
    // fall back to login if the probe fails. On network error we ALSO
    // drop — the optimistic path used to fall through here, but if the
    // stored backend URL is stale/wrong the user lands in a half-open
    // UI with no way out. Re-login is cheap; bias toward correctness.
    validateSession(stored.backend, stored.token)
      .then((ok) => {
        if (ok) startSession(stored);
        else { clearSession(); ui.showLogin(); }
      })
      .catch(() => { clearSession(); ui.showLogin(); });
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
  ui.setLoginSubmitting(true);
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
  } finally {
    ui.setLoginSubmitting(false);
  }
}

function showSignupCard(show) {
  const loginCard = document.getElementById("login-form");
  const signupCard = document.getElementById("signup-form");
  if (!signupCard || !loginCard) return;
  loginCard.hidden = !!show;
  signupCard.hidden = !show;
  setSignupError(null);
  if (show) document.getElementById("signup-username")?.focus();
  else document.getElementById("login-username")?.focus();
}

function setSignupError(msg) {
  const el = document.getElementById("signup-error");
  if (!el) return;
  if (!msg) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false;
  el.textContent = msg;
}

async function onSignup(e) {
  e.preventDefault();
  setSignupError(null);
  const backend = (document.getElementById("signup-backend").value || defaultBackend()).replace(/\/+$/, "");
  const username = document.getElementById("signup-username").value.trim();
  const password = document.getElementById("signup-password").value;
  const confirm = document.getElementById("signup-password-confirm").value;
  if (!username || !password) { setSignupError("Preencha username e password."); return; }
  if (password !== confirm) { setSignupError("As senhas não coincidem."); return; }
  const submitBtn = document.getElementById("signup-submit");
  if (submitBtn) submitBtn.disabled = true;
  try {
    const resp = await signup(backend, username, password);
    const claims = claimsFromToken(resp.token);
    const next = {
      token: resp.token,
      expiresAt: resp.expiresAt,
      username,
      backend,
      role: claims.role,
      firm: claims.firm,
      remember: false,
    };
    sessionStore = sessionStorage;
    writeSession(next);
    startSession(next);
  } catch (err) {
    setSignupError(err.message || "Signup falhou");
  } finally {
    if (submitBtn) submitBtn.disabled = false;
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
  state.setHaltStatus(null);
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
  // Optional external B3MarketDataPlatform WS. Defaults to the dev port
  // 8081 on localhost so the docker-compose stack works out of the box;
  // returns empty for non-localhost deployments where the operator must
  // configure an explicit endpoint via the Market Data panel.
  return defaultMarketDataUrl();
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
  // Subscribe with MBP from the start. DOB is always rendered, so the
  // book channel needs to populate without waiting for the user to
  // explicitly pick a symbol from the topbar selector. Keeping MBP off
  // by default would leave the DOB stuck on "awaiting book snapshot…"
  // for users who accept the default-selected symbol.
  const flags = FLAGS.TRADES | FLAGS.INFO | FLAGS.MBP;
  mbpEnabled = true;
  mdWorker.postMessage({
    type: "start",
    url: mdConfig.url,
    symbols: mdConfig.symbols,
    flags,
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
let lastAutoFilledTicketSymbol = null;
let _successToastTimer = null;

function handleSelectSymbol(symbol) {
  // Single global selector drives DOB, chart and tape. As soon as the
  // user picks anything we promote the MD subscription to MBP so the
  // book panel can render depth (the default is TRADES|INFO only).
  state.setSelectedSymbol(symbol || null);
  // Auto-fill the ticket-symbol input when it's empty or still tracking
  // a previously auto-filled symbol. We never clobber a value the trader
  // is actively editing for a different name — wrong-symbol orders are
  // the worst class of mistake we can prevent here.
  if (symbol) {
    const sym = document.getElementById("ticket-symbol");
    if (sym) {
      const cur = (sym.value || "").trim().toUpperCase();
      if (!cur || cur === lastAutoFilledTicketSymbol) {
        sym.value = symbol;
        lastAutoFilledTicketSymbol = symbol;
      }
    }
  }
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
  // We TTL the pending guard at 15s so a trader who walks away and
  // returns later doesn't have an old "armed" override silently apply
  // to a fresh ticket.
  const FAT_FINGER_TTL_MS = 15_000;
  const lastPrice = state.getState().marketData.get(payload.symbol)?.lastPrice;
  const ff = fatFingerCheck(payload, lastPrice);
  const key = payloadKey(payload);
  let pending = state.getState().pendingFatFinger;
  if (pending && pending.setAt && (Date.now() - pending.setAt) > FAT_FINGER_TTL_MS) {
    state.setPendingFatFinger(null);
    pending = null;
  }
  if (ff && (!pending || pending.key !== key)) {
    state.setPendingFatFinger(payload, key);
    const pct = (ff.deviation * 100).toFixed(1);
    ui.setTicketFeedback(
      `fat-finger guard: price deviates ${pct}% from last trade ${ui.fmtPx(ff.lastPrice)}. Click Submit again to override.`,
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
    const msg = `accepted: ${resp.clOrdId}${resp.status ? ` (${resp.status})` : ""}`;
    ui.setTicketFeedback(msg, "ok");
    ui.clearTicket();
    // Auto-dismiss the success toast after 5s, but only if the message
    // hasn't been replaced (e.g. by a later submit's warning/error).
    if (_successToastTimer) clearTimeout(_successToastTimer);
    _successToastTimer = setTimeout(() => {
      _successToastTimer = null;
      ui.setTicketFeedbackIfMatches(msg, null);
    }, 5000);
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
  const st = state.getState();
  // Don't prompt twice / send duplicate DELETEs while one is in flight,
  // and skip orders that already finished or have a cancel acked.
  if (st.inflightCancels && st.inflightCancels.has(clOrdId)) return;
  const order = st.orders.get(clOrdId);
  if (order && ["Filled", "Cancelled", "Rejected", "PendingCancel"].includes(order.status)) return;
  // Both the mouse (blotter Cancel button) and the keyboard (Del)
  // routes funnel through here; confirmation is centralised so the two
  // paths can't drift in safety.
  if (!window.confirm(`Cancel order ${clOrdId}?`)) return;
  state.markCancelInflight(clOrdId, true);
  try {
    await cancelOrder(session.backend, session.token, clOrdId);
  } catch (err) {
    if (err.status === 401) { logout(); return; }
    ui.setTicketFeedback(`cancel failed: ${err.message}`, "error");
  } finally {
    state.markCancelInflight(clOrdId, false);
  }
}

// ── Blotter UX ─────────────────────────────────────────────────────

function handleBlotterFilter(filter) {
  state.setBlotterFilter(filter);
  writeBlotterFilter(filter);
}

// `delta` is +1 / -1 from the prev/next buttons. The setter clamps
// at 1; the renderer clamps at totalPages, so out-of-range requests
// here are harmless.
function handleBlotterPage(delta) {
  const current = state.getState().blotterPage ?? 1;
  state.setBlotterPage(current + Number(delta || 0));
}

function handleSelectOrder(clOrdId) {
  state.setSelectedOrder(clOrdId);
}

function handleKeyboardCancel() {
  const id = state.getState().selectedClOrdId;
  if (!id) return;
  const order = state.getState().orders.get(id);
  if (!order || ["Filled", "Cancelled", "Rejected"].includes(order.status)) return;
  // Confirmation lives inside handleCancelOrder so mouse and keyboard
  // routes share the same safety prompt.
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
  state.setHaltStatus(null);
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
    const [firms, kill, halts] = await Promise.all([
      getAdminFirms(session.backend, session.token),
      getKillStatus(session.backend, session.token),
      getHaltStatus(session.backend, session.token),
    ]);
    state.setFirmsHealth({ ...firms, fetchedAt: Date.now() });
    state.setKillStatus({
      firms: kill?.Firms ?? [],
      endClients: kill?.EndClients ?? [],
      fetchedAt: Date.now(),
    });
    state.setHaltStatus({
      symbols: halts?.Symbols ?? [],
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

function handleToggleHalt({ symbol, halt }) {
  withAdminCall(
    () => (halt ? haltSymbol : resumeSymbol)(session.backend, session.token, symbol),
    `symbol ${symbol}: ${halt ? "halted" : "resumed"}`,
  );
}

function handleAddHalt({ symbol }) {
  handleToggleHalt({ symbol, halt: true });
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

function readStoredSession(store) {
  try {
    const raw = store.getItem(SESSION_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (!parsed.token || !parsed.expiresAt) return null;
    if (new Date(parsed.expiresAt).getTime() <= Date.now()) return null;
    return parsed;
  } catch {
    return null;
  }
}

function readSession() {
  // sessionStorage is the per-tab anchor: once a tab has its own
  // session pinned there, no other tab can hijack it via localStorage.
  // localStorage is consulted only as a "boot seed" for fresh tabs
  // that don't yet have their own pinned session — that's how
  // "Remember me" survives a full browser close. Issue #104:
  // previously we picked the freshest of either store, which let a
  // remember-me login in tab B silently take over tab A on reload.
  const fromTab = readStoredSession(sessionStorage);
  if (fromTab) {
    sessionStore = sessionStorage;
    return fromTab;
  }
  const fromBoot = readStoredSession(localStorage);
  if (fromBoot) {
    // Pin the boot seed into this tab so subsequent reloads/writes
    // stay isolated from other tabs.
    try { sessionStorage.setItem(SESSION_KEY, JSON.stringify(fromBoot)); } catch { /* swallow */ }
    sessionStore = fromBoot.remember ? localStorage : sessionStorage;
    return fromBoot;
  }
  return null;
}
function writeSession(s) {
  // Always pin in sessionStorage so the tab owns its identity going
  // forward. Mirror to localStorage only when remember-me is on, so
  // a fresh browser launch can recover the session via the boot seed
  // path in readSession(). We deliberately do NOT clear the "other"
  // store here: another tab may legitimately have a remember-me
  // session in localStorage that this tab's write must not erase
  // (issue #104).
  try { sessionStorage.setItem(SESSION_KEY, JSON.stringify(s)); } catch { /* swallow */ }
  if (s.remember) {
    try { localStorage.setItem(SESSION_KEY, JSON.stringify(s)); } catch { /* swallow */ }
  }
  sessionStore = s.remember ? localStorage : sessionStorage;
}
function clearSession() {
  try { localStorage.removeItem(SESSION_KEY); } catch { /* swallow */ }
  try { sessionStorage.removeItem(SESSION_KEY); } catch { /* swallow */ }
}

init();
