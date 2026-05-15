// App entry point: wires login → worker → state → UI together.

import { defaultBackend, defaultMarketDataUrl, login, signup, submitOrder, cancelOrder, modifyOrder, getAdminFirms,
         validateSession,
         getKillStatus, killFirm, reviveFirm, killEndClient, reviveEndClient,
         getHaltStatus, haltSymbol, resumeSymbol,
         runEod,
         listUserBotCredentials, createUserBotCredential, deleteUserBotCredential } from "./protocol.js";
import { claimsFromToken } from "./jwt.js";
import { validateOrder, pretradeWarnings, payloadKey } from "./validation.js";
import * as state from "./state.js";
import { isTerminalOrderStatus } from "./state.js";
import * as ui from "./ui.js";
import * as adminUi from "./adminUi.js";
import * as botCredentialsUi from "./botCredentialsUi.js";
import { FLAGS } from "./mdProtocol.js";

const SESSION_KEY = "b3tp.session";
const MD_KEY = "b3tp.md";
const BLOTTER_FILTER_KEY = "b3tp.blotter.filter";
const DEFAULT_WATCHLIST = ["PETR4", "VALE3"];
const FIRMS_POLL_INTERVAL_MS = 5_000;
// /health is unauthenticated and cheap, so a tighter cadence than the
// admin-only /admin/firms poll is fine. Drives the gateway badge that
// every logged-in user sees in the header.
const GATEWAY_POLL_INTERVAL_MS = 5_000;

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
let gatewayPollTimer = null;

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
  botCredentialsUi.bindBotCredentialsUi();
  ui.setHandlers({
    onSubmitOrder: handleSubmitOrder,
    onCancelOrder: handleCancelOrder,
    onCancelAll: handleCancelAll,
    onModifyOrder: handleModifyOrder,
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
  botCredentialsUi.setBotCredentialsHandlers({
    onOpenView: () => handleSwitchView("bot-credentials"),
    onBack:     () => handleSwitchView("trader"),
    onRefresh:  refreshBotCredentials,
    onCreate:   handleCreateBotCredential,
    onRevoke:   handleRevokeBotCredential,
  });

  // Q1.6 (#258). Whenever the watchlist or auctionPanelSymbol slice
  // changes, re-diff the public WS subscription set so phase badges
  // stay in sync with the watchlist and auction.${symbol} is only
  // subscribed while the panel is open.
  state.subscribe((slice) => {
    if (slice === "watchlist" || slice === "auctionPanelSymbol" || slice === "all") {
      syncPublicChannels();
    }
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
  state.setGatewayHealth(null);
  state.setKillStatus(null);
  state.setHaltStatus(null);
  state.setEodReport(null);
  state.setCurrentView("trader");
  ui.showTrader();

  startWorker();
  startMdWorker();
  startFirmsPoll();
  startGatewayPoll();
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
  // Q1.6 (#258). Push the current public-channel set immediately so the
  // worker has it queued by the time the WS opens. Idempotent — same
  // hook fires on watchlist / auctionPanelSymbol slice changes below.
  syncPublicChannels();
}

// Q1.6 (#258). Build the public-channel set the worker should be
// subscribed to right now: phases.${symbol} for every watchlist
// symbol (so badges populate immediately), plus auction.${symbol}
// only while the panel is open (cost control on WS fan-out).
function syncPublicChannels() {
  if (!worker) return;
  const st = state.getState();
  const channels = [];
  for (const sym of st.watchlist) channels.push("phases." + sym);
  if (st.auctionPanelSymbol) channels.push("auction." + st.auctionPanelSymbol);
  try { worker.postMessage({ type: "setPublicChannels", channels }); }
  catch { /* worker not ready yet — replayed by next slice notify */ }
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
    case "phases.frame":        state.applyPhaseFrame(msg.data); break;
    case "auction.frame":       state.applyAuctionFrame(msg.data); break;
    case "error":
      // A frame-level error from the server (e.g., unknown_channel).
      // Surface in the executions log to keep it visible without a toast.
      console.warn("[ws]", msg);
      break;
  }
}

function formatPretradeWarning(w) {
  switch (w.kind) {
    case "fat_finger": {
      const pct = (w.deviation * 100).toFixed(1);
      return `fat-finger: price deviates ${pct}% from last trade ${ui.fmtPx(w.lastPrice)}`;
    }
    case "qty":
      return `large quantity: ${w.qty.toLocaleString("pt-BR")} > ${w.multiple}× lot (${w.threshold.toLocaleString("pt-BR")})`;
    case "market_notional": {
      const fmt = (n) => `R$ ${n.toLocaleString("pt-BR", { maximumFractionDigits: 0 })}`;
      return `market notional ≈ ${fmt(w.notional)} ≥ ${fmt(w.threshold)}`;
    }
    default:
      return "advisory warning";
  }
}

async function handleSubmitOrder(payload) {
  if (!session) return;

  const error = validateOrder(payload);
  if (error) return ui.setTicketFeedback(error.message, "error");  // Pre-trade advisory guards (fat-finger, soft quantity cap, market
  // notional). The first attempt with one or more warnings is rejected
  // with a combined message; the same exact payload re-submitted within
  // the pending window goes through. We TTL the pending guard at 15s so
  // a trader who walks away and returns later doesn't have an old
  // "armed" override silently apply to a fresh ticket.
  const FAT_FINGER_TTL_MS = 15_000;
  const lastPrice = state.getState().marketData.get(payload.symbol)?.lastPrice;
  const warnings = pretradeWarnings(payload, lastPrice);
  const key = payloadKey(payload);
  let pending = state.getState().pendingFatFinger;
  if (pending && pending.setAt && (Date.now() - pending.setAt) > FAT_FINGER_TTL_MS) {
    state.setPendingFatFinger(null);
    pending = null;
  }
  if (warnings.length > 0 && (!pending || pending.key !== key)) {
    state.setPendingFatFinger(payload, key);
    const msg = warnings.map(w => formatPretradeWarning(w)).join(" · ");
    ui.setTicketFeedback(`${msg}. Click Submit again to override.`, "warn");
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
  if (order && (isTerminalOrderStatus(order.status) || order.status === "PendingCancel")) return;
  // Slice 3 of #132. Stale orders are gated client-side: the backend
  // would 409 with reason "order is marked stale", but skipping the
  // round-trip keeps the UX honest (the row already shows the badge
  // and a disabled button — the keyboard Del shortcut routes here too,
  // so this is the canonical safety point).
  if (order && order.isStale) {
    ui.setTicketFeedback(`cannot cancel — order ${clOrdId} is marked stale`, "warn");
    return;
  }
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

// T3 — bulk cancel-all panic action. Bursts cancellations with a
// concurrency cap so we don't flood the host with N parallel HTTP
// calls (a fat-finger may have left dozens of working orders). The
// modal already gated on the trader typing CANCEL, so this routine
// trusts the caller and just executes. Each per-order cancel still
// flips inflightCancels so the blotter rows visibly transition;
// terminal/PendingCancel orders that slipped into the snapshot are
// silently filtered out. 401 logs out (same surface as single
// cancel). Other failures are counted and reported in the modal.
const CANCEL_ALL_CONCURRENCY = 8;

async function handleCancelAll(clOrdIds) {
  if (!session || !Array.isArray(clOrdIds) || clOrdIds.length === 0) return;
  // Re-validate against current state — the snapshot was taken when
  // the modal opened; an order may have filled since.
  const st = state.getState();
  const queue = clOrdIds.filter(id => {
    if (st.inflightCancels && st.inflightCancels.has(id)) return false;
    const o = st.orders.get(id);
    if (!o) return false;
    if (o.isStale) return false; // slice 3 of #132 — gated client-side too
    return !(isTerminalOrderStatus(o.status) || o.status === "PendingCancel");
  });
  const total = queue.length;
  if (total === 0) {
    ui.setCancelAllProgress({ done: 0, failed: 0, total: 0, finished: true });
    return;
  }
  let done = 0;
  let failed = 0;
  let unauthorized = false;
  let cursor = 0;

  ui.setCancelAllProgress({ done, failed, total, finished: false });

  async function worker() {
    while (true) {
      if (unauthorized) return;
      const idx = cursor++;
      if (idx >= queue.length) return;
      const clOrdId = queue[idx];
      state.markCancelInflight(clOrdId, true);
      try {
        await cancelOrder(session.backend, session.token, clOrdId);
        done += 1;
      } catch (err) {
        if (err.status === 401) { unauthorized = true; return; }
        failed += 1;
      } finally {
        state.markCancelInflight(clOrdId, false);
        ui.setCancelAllProgress({ done, failed, total, finished: false });
      }
    }
  }

  const pool = Array.from(
    { length: Math.min(CANCEL_ALL_CONCURRENCY, queue.length) },
    () => worker(),
  );
  await Promise.all(pool);

  if (unauthorized) { logout(); return; }
  ui.setCancelAllProgress({ done, failed, total, finished: true });
}

// Slice 5 of #122. Routes the blotter "Modify" intent through
// PUT /orders/{clOrdId}. The modal already validated qty/price and
// is responsible for showing the inline error; here we only translate
// transport failures into modal errors and toggle inflight state.
async function handleModifyOrder(clOrdId, payload) {
  if (!session) return;
  const st = state.getState();
  if (st.inflightModifies && st.inflightModifies.has(clOrdId)) return;
  const order = st.orders.get(clOrdId);
  if (!order || isTerminalOrderStatus(order.status)) {
    ui.setModifyModalError("Order is no longer modifiable.");
    return;
  }
  if (order.isStale) {
    // Slice 3 of #132. Modal is open at this point — surface the
    // reason inline so the trader sees why the submit was blocked.
    ui.setModifyModalError(`Order is marked stale${order.staleReason ? ` (${order.staleReason})` : ""} — modify disabled.`);
    return;
  }
  state.markModifyInflight(clOrdId, true);
  ui.setModifyModalSubmitting(true);
  try {
    await modifyOrder(session.backend, session.token, clOrdId, payload);
    // Close the modal on accept; the new ClOrdID will arrive via the
    // orders.me stream and surface in the blotter — no need for a
    // toast since the trader can see the row appear.
    ui.closeModifyModal();
  } catch (err) {
    if (err.status === 401) { logout(); return; }
    // Backend error bodies vary across status codes (Conflict /
    // BadRequest / UnprocessableEntity / BadGateway / etc). The
    // shared shape is `{ error: "..." }` for the structured cases —
    // fall back to err.message for transport errors.
    const reason = (err.body && err.body.error) || err.message || "modify failed";
    ui.setModifyModalError(reason);
  } finally {
    state.markModifyInflight(clOrdId, false);
    ui.setModifyModalSubmitting(false);
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
  if (!order || isTerminalOrderStatus(order.status)) return;
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
  stopGatewayPoll();
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
  state.setGatewayHealth(null);
  state.setKillStatus(null);
  state.setHaltStatus(null);
  state.setEodReport(null);
  state.setCurrentView("trader");
  botCredentialsUi.clearBotCredentials();
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

// ── Gateway health poll ────────────────────────────────────────────
// Polls /health (unauthenticated, cheap) so every logged-in user sees
// an honest exchange-gateway badge in the header. /health.exchange.firms
// is populated only when the host wires IFirmSessionStatusProvider
// (Real mode); in Mock/Stub/Unavailable hosts the badge stays hidden
// rather than guessing at a state we don't have. Failure to fetch is
// treated as "unknown" — we deliberately don't logout on 401 here
// (the endpoint requires no auth, so anything other than network/5xx
// is unexpected and shouldn't kick the user out of the session).

function startGatewayPoll() {
  stopGatewayPoll();
  if (!session) return;
  pollGatewayOnce();
  gatewayPollTimer = setInterval(pollGatewayOnce, GATEWAY_POLL_INTERVAL_MS);
}

function stopGatewayPoll() {
  if (gatewayPollTimer) { clearInterval(gatewayPollTimer); gatewayPollTimer = null; }
}

async function pollGatewayOnce() {
  if (!session) return;
  try {
    const resp = await fetch(`${session.backend}/health`, {
      headers: { Accept: "application/json" },
      cache: "no-store",
    });
    if (!resp.ok) {
      // Only flag as fetch-error on a non-2xx — leaves the existing badge
      // alone for transient blips (304 isn't possible with no-store) so
      // a single bad response doesn't paint the badge red while the
      // session is otherwise fine.
      state.setGatewayHealth({ error: `http_${resp.status}`, fetchedAt: Date.now() });
      return;
    }
    const body = await resp.json();
    const ex = body?.exchange ?? null;
    if (!ex) {
      state.setGatewayHealth(null);
      return;
    }
    state.setGatewayHealth({
      mode: ex.mode,
      readyForOrders: !!ex.readyForOrders,
      firmCount: ex.firmCount ?? 0,
      firms: Array.isArray(ex.firms) ? ex.firms : null,
      fetchedAt: Date.now(),
    });
  } catch (err) {
    state.setGatewayHealth({ error: err?.message || "fetch_failed", fetchedAt: Date.now() });
  }
}

// ── Admin view + actions ───────────────────────────────────────────

function handleSwitchView(view) {
  if (view === "admin" && session?.role !== "admin") return; // safety
  state.setCurrentView(view);
  if (view === "admin") refreshAdminData();
  if (view === "bot-credentials") refreshBotCredentials();
}

async function refreshAdminData() {
  await pollFirmsOnce();
}

// ── User-bot credentials (sub-issue #169) ──────────────────────────
//
// All three operations rely on the JWT — the backend scopes by `sub`,
// so we never pass a user id. The plaintext PAT returned by POST is
// handed straight to the modal and dropped from memory once the user
// dismisses it; nothing is persisted.

async function refreshBotCredentials() {
  if (!session) return;
  const captured = session;
  botCredentialsUi.setBotCredentialsLoading(true);
  botCredentialsUi.setBotCredentialsFeedback(null);
  try {
    const rows = await listUserBotCredentials(captured.backend, captured.token);
    // Discard the response if the session changed under us (logout, or
    // another user signed in on the same tab). Otherwise we'd write
    // the previous session's credential metadata into the DOM after
    // logout, or leak it across user switches.
    if (session !== captured) return;
    botCredentialsUi.setBotCredentialsRows(rows ?? []);
  } catch (err) {
    if (session !== captured) return;
    if (err?.status === 401) { logout(); return; }
    botCredentialsUi.setBotCredentialsRows([]);
    botCredentialsUi.setBotCredentialsFeedback(
      err?.message || "Failed to load credentials.", "error");
  }
}

async function handleCreateBotCredential({ label }) {
  if (!session) return;
  const captured = session;
  botCredentialsUi.setCreateSubmitting(true);
  botCredentialsUi.setBotCredentialsFeedback(null);
  try {
    const created = await createUserBotCredential(captured.backend, captured.token, label);
    // Critical: if the user logged out (or a new user signed in) while
    // the POST was in flight, drop the plaintext PAT on the floor —
    // surfacing it to whoever is now in front of the browser would
    // violate the "shown once, to the issuing user only" invariant.
    if (session !== captured) return;
    botCredentialsUi.resetCreateForm();
    // The plaintext secret only lives inside the modal's input element.
    // Do not log it, do not stash it on `session`, do not put it in state.
    botCredentialsUi.openBotCredentialsSecretModal({
      label: created?.label ?? label,
      plainSecret: created?.plainSecret ?? "",
    });
    botCredentialsUi.setBotCredentialsFeedback(
      `Credential "${created?.label ?? label}" created.`, "ok");
    // Refresh asynchronously — do not await; the modal stays up while
    // the table updates underneath.
    refreshBotCredentials();
  } catch (err) {
    if (session !== captured) return;
    if (err?.status === 401) { logout(); return; }
    botCredentialsUi.setBotCredentialsFeedback(
      err?.message || "Failed to create credential.", "error");
  } finally {
    if (session === captured) {
      botCredentialsUi.setCreateSubmitting(false);
    }
  }
}

async function handleRevokeBotCredential({ id, label }) {
  if (!session) return;
  const captured = session;
  botCredentialsUi.setBotCredentialsFeedback(null);
  try {
    await deleteUserBotCredential(captured.backend, captured.token, id);
    if (session !== captured) return;
    botCredentialsUi.setBotCredentialsFeedback(
      `Credential "${label}" revoked.`, "ok");
    await refreshBotCredentials();
  } catch (err) {
    if (session !== captured) return;
    if (err?.status === 401) { logout(); return; }
    botCredentialsUi.setBotCredentialsFeedback(
      err?.message || "Failed to revoke credential.", "error");
  }
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
