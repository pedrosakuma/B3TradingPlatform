// App entry point: wires login → worker → state → UI together.

import { defaultBackend, defaultMarketDataUrl, login, signup, submitOrder, cancelOrder, modifyOrder, getAdminFirms,
         validateSession, getRiskPolicy,
         getKillStatus, killFirm, reviveFirm, killEndClient, reviveEndClient,
         getHaltStatus, haltSymbol, resumeSymbol,
         runEod,
         listUserBotCredentials, createUserBotCredential, setUserBotCredentialCertBinding, deleteUserBotCredential,
         getOrdersHistory, getExecutionsHistory, getPnlToday,
         getStatement, downloadStatementCsv,
         searchAuditLog, getFillTouch, downloadCvmReport, buildDropCopyWebSocketUrl,
         verifyTotp, enrollTotp, disableTotp, getTotpStatus,
         listAlgos, createAlgo, cancelAlgo, modifyAlgo,
         getInstruments } from "./protocol.js";
import { validateCreateAlgo } from "./validation.js";
import * as algosUi from "./algosUi.js";
import * as settingsUi from "./settingsUi.js";
import * as traderUi from "./traderUi.js";
import * as preferencesUi from "./preferencesUi.js";
import { bindKeyboardShortcuts } from "./keyboard.js";
import {
  parseHashRoute,
  hashForView,
  SETTINGS_SUB_TABS,
  TRADER_SUB_TABS,
} from "./hashRouter.js";
import { claimsFromToken } from "./jwt.js";
import { validateOrder, pretradeWarnings, payloadKey } from "./validation.js";
import * as state from "./state.js";
import { isTerminalOrderStatus } from "./state.js";
import * as ui from "./ui.js";
import * as adminUi from "./adminUi.js";
import * as botCredentialsUi from "./botCredentialsUi.js";
import * as historyUi from "./historyUi.js";
import * as complianceUi from "./complianceUi.js";
import { tabsForRole, defaultViewForRole } from "./complianceUi.js";
import { FLAGS } from "./mdProtocol.js";
import { renderQrInto, clearQr } from "./qrRender.js";
import { applyRiskPolicyFetch } from "./riskPolicy.js";

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
// #303. State for the in-flight 2FA challenge between /auth/login and
// /auth/2fa/verify. Cleared on success / cancel / refresh.
let pendingTotp = null; // { backend, username, remember, totpChallengeToken }
let securityStatusRefreshSeq = 0;

function init() {
  document.getElementById("login-backend").placeholder = defaultBackend();
  document.getElementById("login-form").addEventListener("submit", onLogin);
  const signupBackendInput = document.getElementById("signup-backend");
  if (signupBackendInput) signupBackendInput.placeholder = defaultBackend();
  const signupForm = document.getElementById("signup-form");
  if (signupForm) signupForm.addEventListener("submit", onSignup);
  document.getElementById("login-go-signup")?.addEventListener("click", () => showSignupCard(true));
  document.getElementById("signup-go-login")?.addEventListener("click", () => showSignupCard(false));
  // #303. 2FA second-factor step + Security panel.
  document.getElementById("totp-form")?.addEventListener("submit", onTotpSubmit);
  document.getElementById("totp-cancel")?.addEventListener("click", () => {
    pendingTotp = null;
    showTotpCard(false);
  });
  // Fase 3 (#399). The legacy Security button + modal collapsed into
  // the Settings > Security sub-tab. `openSecurityPanel` is now a
  // sub-tab navigation; the close/reset behaviour that used to live
  // on the modal's × button has no analogue (the sub-tab is reset
  // every time it's re-entered via openSecurityPanel).
  document.getElementById("security-enroll-begin")?.addEventListener("click", onSecurityEnrollBegin);
  document.getElementById("security-recovery-ack")?.addEventListener("change", (e) => {
    document.getElementById("security-confirm").disabled = !e.target.checked;
  });
  document.getElementById("security-confirm")?.addEventListener("click", onSecurityEnrollConfirm);
  document.getElementById("security-disable")?.addEventListener("click", onSecurityDisable);
  ui.bindUi();
  adminUi.bindAdminUi();
  botCredentialsUi.bindBotCredentialsUi();
  historyUi.bindHistoryUi();
  complianceUi.bindComplianceUi();
  // Fase 3 (#399). Settings sub-tab navigation. Must run after the
  // DOM is parsed; bindUi is enough of a guard since this script is
  // loaded at the end of <body>.
  settingsUi.bindSettingsUi();
  // Fase 4 (#400). Trader sub-tab + lower-band + ticket-advanced.
  traderUi.bindTraderUi();
  // Fase 5 (#401). Preferences sub-tab (density toggle).
  preferencesUi.bindPreferencesUi();
  // Restore density preference before any view renders so the login
  // screen itself respects the saved choice. Persisted in localStorage
  // (per-trader preference, not per-tab state).
  try {
    const dens = localStorage.getItem(DENSITY_KEY);
    if (dens === "compact" || dens === "comfortable") state.setDensity(dens);
  } catch { /* private mode */ }
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
    onOpenView: () => handleSwitchView("settings", "bot-credentials"),
    onBack:     () => handleSwitchView("trader"),
    onRefresh:  refreshBotCredentials,
    onCreate:   handleCreateBotCredential,
    onSetCertBinding: handleSetCertBinding,
    onRevoke:   handleRevokeBotCredential,
  });
  historyUi.setHistoryHandlers({
    onOpenView:       () => handleSwitchView("history"),
    onBack:           () => handleSwitchView("trader"),
    onRefresh:        refreshHistoryAll,
    onApplyFilters:   handleHistoryApplyFilters,
    onLoadMoreOrders: () => loadMoreHistoryOrders(false),
    onLoadMoreExecs:  () => loadMoreHistoryExecutions(false),
    onDownloadCsv:    handleStatementDownload,
    onViewJson:       handleStatementViewJson,
  });
  complianceUi.setComplianceHandlers({
    onAuditSearch:     handleAuditSearch,
    onAuditNext:       handleAuditSearch,
    onFillTouchLookup: handleFillTouchLookup,
    onCvmDownload:     handleCvmDownload,
  });

  // FE-OPT-2 (#498). Chain picker load button — needs session for API call.
  document.getElementById("chain-load-btn")?.addEventListener("click", handleLoadChain);

  // Fase 5 (#401). Global keyboard shortcuts. Handlers gate on
  // `session` so they no-op while the user is on the login screen.
  bindKeyboardShortcuts({
    "tab:trader":     () => session && handleSwitchView("trader"),
    "tab:algos":      () => session && handleSwitchView("algos"),
    "tab:history":    () => session && handleSwitchView("history"),
    "tab:settings":   () => session && handleSwitchView("settings"),
    "tab:admin":      () => session && handleSwitchView("admin"),
    "tab:compliance": () => session && handleSwitchView("compliance"),
    "trader-sub:markets":   () => session && handleSwitchView("trader", "markets"),
    "trader-sub:watchlist": () => session && handleSwitchView("trader", "watchlist"),
    "trader-sub:auctions":  () => session && handleSwitchView("trader", "auctions"),
    "trader-bottom:blotter":    () => state.setTraderBottomTab("blotter"),
    "trader-bottom:executions": () => state.setTraderBottomTab("executions"),
    "focus:symbol": () => {
      const el = document.getElementById("ticket-symbol")
              || document.getElementById("selected-symbol");
      if (el && typeof el.focus === "function") el.focus();
    },
    "ticket:buy":  () => {
      const sel = document.getElementById("ticket-side");
      if (sel) { sel.value = "Buy";  sel.dispatchEvent(new Event("change", { bubbles: true })); }
    },
    "ticket:sell": () => {
      const sel = document.getElementById("ticket-side");
      if (sel) { sel.value = "Sell"; sel.dispatchEvent(new Event("change", { bubbles: true })); }
    },
    "modal:close": () => {
      // Order-detail modal is the only modal still in use post-Fase-3;
      // its overlay listens for Esc on its own backdrop, so the global
      // shortcut just calls into the same close path when present.
      const backdrop = document.getElementById("order-detail-backdrop");
      if (backdrop && !backdrop.hidden) {
        backdrop.dispatchEvent(new MouseEvent("click", { bubbles: true }));
      }
    },
  });

  // Q1.6 (#258). Whenever the watchlist or auctionPanelSymbol slice
  // changes, re-diff the public WS subscription set so phase badges
  // stay in sync and auction.${symbol} is only subscribed while the
  // panel is open. (Depth comes from the mdWorker MBP path now, not
  // a trading-host channel — see #394.)
  state.subscribe((slice) => {
    if (slice === "watchlist" || slice === "auctionPanelSymbol" || slice === "all") {
      syncPublicChannels();
    }
    // Fase 4 (#400). Persist trader UI state so a reload restores it.
    // The sub-tab is also persisted by handleSwitchView (for the hash
    // sync path), but lower-band / ticket-advanced toggles never route
    // through handleSwitchView, so they get their persistence here.
    if (slice === "traderSubTab" || slice === "all") {
      persistTraderSubTab(state.getState().traderSubTab);
    }
    if (slice === "traderBottomTab" || slice === "all") {
      persistTraderBottomTab(state.getState().traderBottomTab);
    }
    if (slice === "ticketAdvancedOpen" || slice === "all") {
      persistTicketAdvancedOpen(state.getState().ticketAdvancedOpen);
    }
    if (slice === "density" || slice === "all") {
      persistDensity(state.getState().density);
    }
    if (slice === "currentView" || slice === "settingsSubTab" || slice === "all") {
      const current = state.getState();
      const securityVisible = current.currentView === "settings" && current.settingsSubTab === "security";
      if (securityVisible) refreshSecurityPanel();
      else if (slice !== "all") closeSecurityPanel();
    }
  });

  // Fase 1 (#397). URL hash navigation. The tablist click handler
  // itself lives in ui.js (which already dispatches through the
  // shared `onSwitchView` handler we wired below); here we just keep
  // the browser back/forward buttons walking the tab history.
  // Avoid loops: handleSwitchView only pushes a hash entry when the
  // active view actually changes, so reapplying the current hash
  // here is a no-op.
  window.addEventListener("popstate", () => {
    if (!session) return;
    const route = tabFromHash();
    if (route?.view) handleSwitchView(route.view, route.subTab);
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
    // #303. Server may demand a TOTP code or a forced first-time
    // enrollment before issuing a JWT. Stash context and switch cards.
    if (resp && resp.requires2fa && resp.totpChallengeToken) {
      pendingTotp = { backend, username, remember, totpChallengeToken: resp.totpChallengeToken };
      showTotpCard(true);
      return;
    }
    if (resp && resp.requires2faEnrollment && resp.enrollmentToken) {
      // Force-enroll path: open enrollment immediately. We don't have a
      // JWT yet, so we pass the enrollment token through to /auth/2fa/enroll.
      pendingTotp = { backend, username, remember, enrollmentToken: resp.enrollmentToken };
      await beginForcedEnrollment();
      return;
    }
    finishLoginWithToken(resp, { backend, username, remember });
  } catch (err) {
    ui.setLoginError(err.message || "Login failed");
  } finally {
    ui.setLoginSubmitting(false);
  }
}

function finishLoginWithToken(resp, { backend, username, remember }) {
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
}

function showTotpCard(show) {
  const loginCard = document.getElementById("login-form");
  const signupCard = document.getElementById("signup-form");
  const totpCard = document.getElementById("totp-form");
  if (!totpCard) return;
  if (loginCard) loginCard.hidden = !!show;
  if (signupCard) signupCard.hidden = true;
  totpCard.hidden = !show;
  const errEl = document.getElementById("totp-error");
  if (errEl) { errEl.hidden = true; errEl.textContent = ""; }
  if (show) document.getElementById("totp-code")?.focus();
  else {
    const codeEl = document.getElementById("totp-code");
    if (codeEl) codeEl.value = "";
  }
}

async function onTotpSubmit(e) {
  e.preventDefault();
  if (!pendingTotp || !pendingTotp.totpChallengeToken) return;
  const code = document.getElementById("totp-code").value.trim();
  const errEl = document.getElementById("totp-error");
  if (errEl) { errEl.hidden = true; errEl.textContent = ""; }
  const submitBtn = document.getElementById("totp-submit");
  if (submitBtn) submitBtn.disabled = true;
  try {
    const resp = await verifyTotp(pendingTotp.backend, {
      code,
      totpChallengeToken: pendingTotp.totpChallengeToken,
    });
    const ctx = pendingTotp;
    pendingTotp = null;
    showTotpCard(false);
    finishLoginWithToken(resp, { backend: ctx.backend, username: ctx.username, remember: ctx.remember });
  } catch (err) {
    if (errEl) { errEl.hidden = false; errEl.textContent = err.message || "Invalid code"; }
  } finally {
    if (submitBtn) submitBtn.disabled = false;
  }
}

// ── Security panel (TOTP enrollment / disable) — #303 ───────────────
// Fase 3 (#399). What used to be a modal is now the Settings > Security
// sub-tab. `openSecurityPanel` navigates to that sub-tab and resets the
// transient inputs; `closeSecurityPanel` (still called on logout /
// session reset) wipes the recovery codes + QR so secret material
// leaves the DOM as soon as the sub-tab is dismissed.
function openSecurityPanel() {
  if (!session) return;
  handleSwitchView("settings", "security");
}

function closeSecurityPanel() {
  securityStatusRefreshSeq += 1;
  // Wipe the recovery codes from the DOM as soon as the user dismisses.
  const pre = document.getElementById("security-recovery-codes");
  if (pre) pre.textContent = "";
  const otpauthUri = document.getElementById("security-otpauth-uri");
  if (otpauthUri) otpauthUri.value = "";
  const secret = document.getElementById("security-secret");
  if (secret) secret.value = "";
  const ack = document.getElementById("security-recovery-ack");
  if (ack) ack.checked = false;
  const confirm = document.getElementById("security-confirm");
  if (confirm) confirm.disabled = true;
  const confirmCode = document.getElementById("security-confirm-code");
  if (confirmCode) confirmCode.value = "";
  const disableCode = document.getElementById("security-disable-code");
  if (disableCode) disableCode.value = "";
  // #320: drop the rendered QR too — it encodes the otpauth secret.
  clearQr(document.getElementById("security-qr"));
  pendingEnrollSecret = null;
  setSecurityError(null);
}

function setSecurityStatus(state) {
  const el = document.getElementById("security-status");
  if (!el) return;
  el.classList.remove(
    "security-status-enrolled",
    "security-status-not-enrolled",
    "security-status-pending",
    "security-status-unavailable",
  );
  if (state === "enrolled") {
    el.textContent = "Enrolled";
    el.classList.add("security-status-enrolled");
  } else if (state === "not-enrolled") {
    el.textContent = "Not enrolled";
    el.classList.add("security-status-not-enrolled");
  } else if (state === "pending") {
    el.textContent = "Pending confirmation";
    el.classList.add("security-status-pending");
  } else if (state === "unavailable") {
    el.textContent = "Unavailable";
    el.classList.add("security-status-unavailable");
  } else {
    el.textContent = "Checking…";
  }
}

function renderSecurityPanel({ enrolled, pending }) {
  document.getElementById("security-enroll-start").hidden = !!enrolled || !!pending;
  document.getElementById("security-enroll-show").hidden = !pending;
  document.getElementById("security-enrolled").hidden = !enrolled || !!pending;
  setSecurityStatus(pending ? "pending" : (enrolled ? "enrolled" : "not-enrolled"));
}

async function refreshSecurityPanel() {
  if (!session) return;
  const refreshSeq = ++securityStatusRefreshSeq;
  setSecurityStatus("checking");
  try {
    const status = await getTotpStatus(session.backend, session.token);
    if (refreshSeq !== securityStatusRefreshSeq) return;
    renderSecurityPanel({ enrolled: !!status?.enrolled, pending: !!pendingEnrollSecret });
  } catch (err) {
    if (refreshSeq !== securityStatusRefreshSeq) return;
    if (err?.status === 401) { logout(); return; }
    setSecurityStatus("unavailable");
    setSecurityError(err?.message || "Unable to load 2FA status");
  }
}

function setSecurityError(msg) {
  const el = document.getElementById("security-error");
  if (!el) return;
  if (!msg) { el.hidden = true; el.textContent = ""; return; }
  el.hidden = false; el.textContent = msg;
}

let pendingEnrollSecret = null; // base32, only kept until confirm/cancel

async function onSecurityEnrollBegin() {
  if (!session) return;
  setSecurityError(null);
  try {
    const resp = await enrollTotp(session.backend, session.token);
    pendingEnrollSecret = resp.secret;
    document.getElementById("security-otpauth-uri").value = resp.otpauthUri;
    document.getElementById("security-secret").value = resp.secret;
    document.getElementById("security-recovery-codes").textContent = resp.recoveryCodes.join("\n");
    // #320: render the otpauth URI as a scannable QR.
    renderQrInto(document.getElementById("security-qr"), resp.otpauthUri);
    document.getElementById("security-enroll-start").hidden = true;
    document.getElementById("security-enroll-show").hidden = false;
    document.getElementById("security-enrolled").hidden = true;
    document.getElementById("security-recovery-ack").checked = false;
    document.getElementById("security-confirm").disabled = true;
    document.getElementById("security-confirm-code").value = "";
    setSecurityStatus("pending");
  } catch (err) {
    setSecurityError(err.message || "Enrollment failed");
    if (err?.status === 401) { logout(); return; }
    refreshSecurityPanel();
  }
}

async function onSecurityEnrollConfirm() {
  if (!session) return;
  const code = document.getElementById("security-confirm-code").value.trim();
  if (!code) { setSecurityError("Code required"); return; }
  setSecurityError(null);
  try {
    await verifyTotp(session.backend, { code, token: session.token });
    document.getElementById("security-enroll-show").hidden = true;
    document.getElementById("security-enrolled").hidden = false;
    document.getElementById("security-disable-code").value = "";
    // #320: secret was committed — drop the QR (encodes the seed).
    clearQr(document.getElementById("security-qr"));
    pendingEnrollSecret = null;
    setSecurityStatus("enrolled");
  } catch (err) {
    setSecurityError(err.message || "Verification failed");
  }
}

async function onSecurityDisable() {
  if (!session) return;
  const code = document.getElementById("security-disable-code").value.trim();
  if (!code) { setSecurityError("Code required"); return; }
  setSecurityError(null);
  try {
    await disableTotp(session.backend, session.token, code);
    document.getElementById("security-disable-code").value = "";
    renderSecurityPanel({ enrolled: false, pending: false });
  } catch (err) {
    setSecurityError(err.message || "Disable failed");
  }
}

async function beginForcedEnrollment() {
  if (!pendingTotp || !pendingTotp.enrollmentToken) return;
  try {
    const resp = await enrollTotp(pendingTotp.backend, null, pendingTotp.enrollmentToken);
    alert(
      "Two-factor authentication is required on this account.\n\n" +
      "Set up your authenticator with this URI:\n" + resp.otpauthUri + "\n\n" +
      "Recovery codes (save now — shown ONCE):\n" + resp.recoveryCodes.join("\n") + "\n\n" +
      "Then sign in again — you'll be asked for a 6-digit code."
    );
    pendingTotp = null;
    showTotpCard(false);
  } catch (err) {
    ui.setLoginError(err.message || "Forced enrollment failed");
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
  if (!username || !password) { setSignupError("Provide username and password."); return; }
  if (password !== confirm) { setSignupError("Passwords do not match."); return; }
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
    setSignupError(err.message || "Signup failed");
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
  // Hide login + neighbouring sub-views; applyCurrentView (subscribed
  // to setCurrentView below) then toggles the right view-section
  // visible. showTrader() runs first so the brief flash is the
  // trader scaffold, never login-into-compliance.
  ui.showTrader();
  // Fase 1 (#397). Resolve the landing tab in priority order:
  //   1. URL hash (deep-link / refresh after navigation),
  //   2. sessionStorage (last tab in this browser tab),
  //   3. role default (compliance lands on its own console; everyone
  //      else lands on trader).
  // Each candidate must pass the same role-gate as handleSwitchView,
  // otherwise we fall through to the next candidate.
  // Fase 3 (#399). `tabFromHash` may also surface a Settings sub-tab
  // (`#settings/security`, legacy `#bot-credentials` → settings/
  // bot-credentials); apply it once the view is mounted.
  const allowed = new Set(tabsForRole(next.role));
  const hashRoute = tabFromHash();
  const candidates = [hashRoute?.view, readPersistedTab(), defaultViewForRole(next.role)];
  const initialView = candidates.find((v) => v && allowed.has(v))
    || defaultViewForRole(next.role);
  state.setCurrentView(initialView);
  if (initialView === "settings") {
    let subTab = hashRoute?.subTab;
    if (!subTab) {
      try { subTab = sessionStorage.getItem(SETTINGS_SUB_TAB_KEY); }
      catch { /* private mode */ }
    }
    if (SETTINGS_SUB_TABS.has(subTab)) state.setSettingsSubTab(subTab);
  }
  // Fase 4 (#400). Restore trader sub-tab from hash → sessionStorage.
  if (initialView === "trader") {
    let subTab = hashRoute?.subTab;
    if (!subTab) {
      try { subTab = sessionStorage.getItem(TRADER_SUB_TAB_KEY); }
      catch { /* private mode */ }
    }
    if (TRADER_SUB_TABS.has(subTab)) state.setTraderSubTab(subTab);
  }
  // Lower-band tab + ticket-advanced are persisted globally (no hash
  // routing — they're UI state inside the trader shell, not first-class
  // views).
  try {
    const bottom = sessionStorage.getItem(TRADER_BOTTOM_TAB_KEY);
    if (bottom === "blotter" || bottom === "executions") state.setTraderBottomTab(bottom);
    const adv = sessionStorage.getItem(TICKET_ADVANCED_KEY);
    if (adv === "1") state.setTicketAdvancedOpen(true);
  } catch { /* private mode */ }
  // Fase 5 (#401). Restore density preference (localStorage, not
  // session — survives across browser sessions). Already applied at
  // init() so the login screen respects it; re-apply here so a tab
  // opened with a stale in-memory default catches up.
  try {
    const dens = localStorage.getItem(DENSITY_KEY);
    if (dens === "compact" || dens === "comfortable") state.setDensity(dens);
  } catch { /* private mode */ }
  persistActiveTab(initialView);
  syncUrlHash(initialView, /*replace*/ true,
    initialView === "settings" ? state.getState().settingsSubTab :
    initialView === "trader"   ? state.getState().traderSubTab   :
    undefined);
  if (initialView === "compliance") {
    // Compliance users land directly on the compliance console — no
    // trader view, no admin polls. Open the drop-copy socket so the
    // live feed has data the moment the panel renders.
    openComplianceDropCopy();
  }
  syncPnlSubscription(initialView);
  // Fase 2 (#398). Init the algos UI once per session — it's idempotent
  // but needs the action callbacks so the boleta knows where to POST.
  algosUi.initAlgosUi({
    onSubmitAlgo: handleSubmitAlgo,
    onCancelAlgo: handleCancelAlgo,
    onModifyAlgo: handleModifyAlgo,
  });
  syncAlgoSubscription(initialView);
  if (initialView === "algos") refreshAlgosList();

  startWorker();
  startMdWorker();
  startFirmsPoll();
  startGatewayPoll();
  scheduleExpiry();
  loadRiskPolicy();
}

// Q1.4 (#256). Fetch the effective risk policy on session start so the
// ticket validator's GTD horizon matches the backend cap. Failure is
// silent (single console.warn) — the validator falls back to a 30-day
// cap so the trader is never blocked by a slow/broken policy fetch.
async function loadRiskPolicy() {
  if (!session?.token) return;
  await applyRiskPolicyFetch({
    fetchPolicy: () => getRiskPolicy(session.backend, session.token),
    setRiskPolicy: state.setRiskPolicy,
  });
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
  // #394. Depth ladder no longer flows through the trading-host WS —
  // FE consumes B3MarketDataPlatform directly via mdWorker (MBP path).
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
  // #394. mdWorker is now the sole depth source — trades + info + MBP.
  // The trading-host book.${symbol} fan-out was removed; FE consumes
  // B3MarketDataPlatform directly.
  // `Book` is required too: per B3MarketDataPlatform docs/WEBSOCKET-PROTOCOL.md,
  // the initial CandleSnapshot sequence is only sent "on subscribe with
  // the Book flag" (CandleUpdate itself rides Mbp, but state.js drops
  // every update until a completed snapshot sets `entry.ready`). Without
  // it the chart never leaves "no candle snapshot received" even though
  // trades are executing.
  const flags = FLAGS.TRADES | FLAGS.INFO | FLAGS.MBP | FLAGS.BOOK;
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
    state.clearAllHeatmap();
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

// #394. Depth ladder is fed by the mdWorker MBP stream (every
// watchlist symbol subscribed once at the marketdata WS). No
// per-selection promotion needed; selectedSymbol just drives which
// ladder the DOB renders.
let lastAutoFilledTicketSymbol = null;

function handleSelectSymbol(symbol) {
  // Single global selector drives DOB, chart and tape. The DOB reads
  // the per-symbol entry from state.book that mdWorker already keeps
  // up to date via MBP — no public-channel resync is required here.
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
}

function onMdWorkerMessage(msg) {
  switch (msg.type) {
    case "md.status":   state.setMarketDataStatus(msg.value); break;
    case "md.clear":
      state.clearMarketData();
      state.clearAllBooks();
      state.clearAllCandles();
      state.clearAllTape();
      state.clearAllHeatmap();
      break;
    case "md.trade":    state.applyMdTrade(msg); break;
    case "md.info":     state.applyMdInfo(msg); break;
    case "md.bust":     state.applyMdTradeBust(msg); break;
    case "md.subError":
      ui.setMdFeedback(`subscribe ${msg.symbol}: ${msg.errorName}`, "error");
      state.removeMdSymbol(msg.symbol);
      state.removeCandlesSymbol(msg.symbol);
      break;
    case "md.removed":
      state.removeMdSymbol(msg.symbol);
      state.removeCandlesSymbol(msg.symbol);
      break;
    case "md.candle.snapshot": state.applyMdCandleSnapshot(msg); break;
    case "md.candle.update":   state.applyMdCandleUpdate(msg); break;
    case "md.book.snapshot":   state.applyMdBookSnapshot(msg); break;
    case "md.book.cleared":    state.applyMdBookCleared(msg); break;
    case "md.level.snapshot":  state.applyMdLevelSnapshot(msg); break;
    case "md.level.update":    state.applyMdLevelUpdate(msg); break;
    case "md.level.deleted":   state.applyMdLevelDeleted(msg); break;
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
    case "pnl.snapshot":        state.applyPnlDelta(msg.data); break;
    case "pnl.delta":           state.applyPnlDelta(msg.data); break;
    case "balance.frame":       state.applyBalanceFrame(msg.data); break;
    case "algo.snapshot":       state.applyAlgoSnapshot(msg.data); break;
    case "algo.delta":          state.applyAlgoDelta(msg.data); break;
    case "phases.frame":        state.applyPhaseFrame(msg.data); break;
    case "auction.frame":       state.applyAuctionFrame(msg.data); break;
    case "error":
      // A frame-level error from the server (e.g., unknown_channel,
      // bad subscribe args, channel auth). #342: surface it as a
      // transient toast so the trader notices a quietly-failing
      // subscription without having to open devtools.
      console.warn("[ws]", msg);
      ui.showWsErrorToast(formatWsError(msg));
      break;
  }
}

// #342: render a worker `error` frame as a short human-readable string
// for the WS error toast. The frame is { type:"error", channel?, code?, message? };
// we keep noise low by collapsing missing fields rather than printing
// "undefined" / JSON.
function formatWsError(msg) {
  const channel = msg?.channel ? `[${msg.channel}] ` : "";
  const code    = msg?.code ? `${msg.code}: ` : "";
  const body    = msg?.message ? String(msg.message) : "websocket error";
  return `${channel}${code}${body}`;
}

function formatPretradeWarning(w) {
  switch (w.kind) {
    case "fat_finger": {
      const pct = (w.deviation * 100).toFixed(1);
      return `fat-finger: price deviates ${pct}% from last trade ${ui.fmtPx(w.lastPrice)}`;
    }
    case "qty":
      return `large quantity: ${w.qty.toLocaleString("en-US")} > ${w.multiple}× lot (${w.threshold.toLocaleString("en-US")})`;
    case "market_notional": {
      const fmt = (n) => `R$ ${n.toLocaleString("en-US", { maximumFractionDigits: 0 })}`;
      return `market notional ≈ ${fmt(w.notional)} ≥ ${fmt(w.threshold)}`;
    }
    default:
      return "advisory warning";
  }
}

// FE-OPT-2 (#498). Load option chain from API and build grid in modal.
async function handleLoadChain() {
  if (!session) return;
  const underlying = document.getElementById("chain-underlying")?.value?.trim().toUpperCase();
  if (!underlying) {
    document.getElementById("chain-picker-grid").innerHTML =
      '<p class="chain-placeholder">Enter an underlying symbol (e.g., PETR4)</p>';
    return;
  }
  const grid = document.getElementById("chain-picker-grid");
  if (grid) grid.innerHTML = '<p class="chain-placeholder">Loading…</p>';
  try {
    const instruments = await getInstruments(session.backend, session.token, { underlying });
    if (!instruments || instruments.length === 0) {
      if (grid) grid.innerHTML = '<p class="chain-placeholder">No options found for this underlying</p>';
      return;
    }
    if (grid) grid.innerHTML = ui.buildChainGrid(instruments);
  } catch (err) {
    if (grid) grid.innerHTML = `<p class="chain-placeholder" style="color:#dc2626">Error: ${err.message}</p>`;
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
    // #421: success surface is the standalone toast above the panel
    // grid — easier to notice than the previous inline text under the
    // ticket form, and doesn't fight for space with the next submit.
    const msg = `accepted: ${resp.clOrdId}${resp.status ? ` (${resp.status})` : ""}`;
    ui.showOrderToast(msg, "ok");
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
    if (!raw) return { text: "", status: "", hideTerminal: true };
    const parsed = JSON.parse(raw);
    return {
      text:   typeof parsed?.text   === "string" ? parsed.text   : "",
      status: typeof parsed?.status === "string" ? parsed.status : "",
      // #342: hideTerminal defaults to true for both fresh and pre-existing
      // sessions (no persisted value → treat as on).
      hideTerminal: parsed?.hideTerminal !== false,
    };
  } catch { return { text: "", status: "", hideTerminal: true }; }
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
  session = null;
  mdConfig = null;
  closeComplianceDropCopy();
  clearSession();
  clearMdConfig();
  // Fase 1 (#397). Drop the persisted tab so the next sign-in lands
  // on the role-default rather than reviving the previous user's
  // navigation state.
  try { sessionStorage.removeItem(ACTIVE_TAB_KEY); } catch { /* private mode */ }
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

// ── Primary tab routing ────────────────────────────────────────────
//
// Fase 1 (#397). Single seam for activating any of the top-level
// tabs (`trader`, `algos`, `history`, `settings`, `admin`,
// `compliance`) plus the `bot-credentials` sub-view (still reached
// from inside Settings).
//
// Side-effects:
//   * gates by role via `tabsForRole`;
//   * persists the active tab in sessionStorage (`fe.activeTab`)
//     so a refresh lands on the same tab;
//   * syncs the URL hash via pushState so the browser back/forward
//     buttons walk the tab history;
//   * lifts/drops the per-view subscriptions (drop-copy WS, pnl.me).

const ACTIVE_TAB_KEY = "fe.activeTab";
const SETTINGS_SUB_TAB_KEY = "fe.settingsSubTab";
const TRADER_SUB_TAB_KEY = "fe.traderSubTab";
const TRADER_BOTTOM_TAB_KEY = "fe.traderBottomTab";
const TICKET_ADVANCED_KEY = "fe.ticketAdvancedOpen";
// Fase 5 (#401). Density is a per-trader preference, not per-tab — lives
// in localStorage so a new tab inherits the user's choice.
const DENSITY_KEY = "fe.density";

function tabFromHash() {
  const hash = (typeof window !== "undefined" && window.location?.hash) || "";
  return parseHashRoute(hash);
}

function persistActiveTab(view) {
  try { sessionStorage.setItem(ACTIVE_TAB_KEY, view); } catch { /* private mode */ }
}

function readPersistedTab() {
  try { return sessionStorage.getItem(ACTIVE_TAB_KEY); } catch { return null; }
}

function persistSettingsSubTab(sub) {
  try { sessionStorage.setItem(SETTINGS_SUB_TAB_KEY, sub); } catch { /* private mode */ }
}

function persistTraderSubTab(sub) {
  try { sessionStorage.setItem(TRADER_SUB_TAB_KEY, sub); } catch { /* private mode */ }
}
function persistTraderBottomTab(sub) {
  try { sessionStorage.setItem(TRADER_BOTTOM_TAB_KEY, sub); } catch { /* private mode */ }
}
function persistTicketAdvancedOpen(open) {
  try { sessionStorage.setItem(TICKET_ADVANCED_KEY, open ? "1" : "0"); } catch { /* private mode */ }
}
function persistDensity(name) {
  try { localStorage.setItem(DENSITY_KEY, name); } catch { /* private mode */ }
}

function syncUrlHash(view, replace, subTab) {
  if (typeof window === "undefined" || !window.history) return;
  const hash = hashForView(view, subTab);
  if (!hash) return;
  if (window.location.hash === hash) return;
  const url = window.location.pathname + window.location.search + hash;
  if (replace) window.history.replaceState(null, "", url);
  else window.history.pushState(null, "", url);
}

function handleSwitchView(view, subTab) {
  if (!session) return;
  // Role-gate the target view. Plain users see trader / algos /
  // history / settings; admin sees everything; compliance is pinned
  // to its own console.
  const allowed = tabsForRole(session.role);
  if (!allowed.includes(view)) return;
  // Fase 3 (#399). Settings sub-tab is applied BEFORE setCurrentView
  // so the subscriber that toggles panel visibility sees the right
  // sub-tab on the same render pass that mounts the view.
  if (view === "settings" && subTab && SETTINGS_SUB_TABS.has(subTab)) {
    state.setSettingsSubTab(subTab);
    persistSettingsSubTab(subTab);
  }
  // Fase 4 (#400). Same dance for the trader sub-tab.
  if (view === "trader" && subTab && TRADER_SUB_TABS.has(subTab)) {
    state.setTraderSubTab(subTab);
    persistTraderSubTab(subTab);
  }
  state.setCurrentView(view);
  persistActiveTab(view);
  let effectiveSub;
  if (view === "settings") {
    effectiveSub = SETTINGS_SUB_TABS.has(subTab) ? subTab : state.getState().settingsSubTab;
  } else if (view === "trader") {
    effectiveSub = TRADER_SUB_TABS.has(subTab) ? subTab : state.getState().traderSubTab;
  }
  syncUrlHash(view, /*replace*/ false, effectiveSub);
  // Drop-copy WS lifecycle: open on enter, close on leave. Cheap to
  // re-open; we don't pay the cost of holding the socket while the
  // user is parked on a different view.
  if (view === "compliance") openComplianceDropCopy();
  else closeComplianceDropCopy();
  // Q2.6 (#273). Dynamic pnl.me subscription: only stay subscribed
  // while the history view (which hosts the P&L panel) is mounted, so
  // the per-fill fan-out doesn't run for traders parked on other views.
  syncPnlSubscription(view);
  // Fase 2 (#398). Same dynamic gating for algo.me.
  syncAlgoSubscription(view);
  if (view === "admin") refreshAdminData();
  if (view === "settings" && effectiveSub === "bot-credentials") refreshBotCredentials();
  if (view === "history") refreshHistoryAll();
  if (view === "algos") refreshAlgosList();
}

function syncPnlSubscription(view) {
  if (!worker) return;
  const want = view === "history";
  try { worker.postMessage({ type: "setPnlSubscribed", value: want }); }
  catch { /* worker not ready yet — replayed by next start */ }
}

// Fase 2 (#398). Algo.me is only useful while the Algos view is mounted.
// Outside it the snapshot the user already has is "frozen" — that's OK:
// next entry triggers a re-snapshot via the worker subscribe + a REST
// refresh below.
function syncAlgoSubscription(view) {
  if (!worker) return;
  const want = view === "algos";
  try { worker.postMessage({ type: "setAlgoSubscribed", value: want }); }
  catch { /* worker not ready yet — replayed by next start */ }
}

async function refreshAlgosList() {
  if (!session) return;
  try {
    const rows = await listAlgos(session.backend, session.token, { includeTerminal: false });
    state.applyAlgoSnapshot(Array.isArray(rows) ? rows : []);
  } catch (err) {
    console.warn("[algos] list failed", err);
    algosUi.showBoletaError(`Failed to load algos: ${err?.message || err}`);
  }
}

async function handleSubmitAlgo(payload) {
  if (!session) return;
  const result = validateCreateAlgo(payload);
  if (!result.ok) {
    algosUi.showBoletaError(result.error + (result.detail ? ` (${JSON.stringify(result.detail)})` : ""));
    return;
  }
  try {
    const created = await createAlgo(session.backend, session.token, payload);
    algosUi.showBoletaSuccess(`Algo ${created?.algoId ?? ""} created.`);
    // WS delta normally arrives within ms; fire a defensive refresh in
    // case the user opened the tab and hit submit before the snapshot
    // landed (algo.me subscribe + REST list both repopulate cleanly).
    refreshAlgosList();
  } catch (err) {
    algosUi.showBoletaError(`Error creating algo: ${err?.message || err}`);
  }
}

async function handleCancelAlgo(algoId) {
  if (!session || !algoId) return;
  state.markAlgoCancelInflight(algoId, true);
  try {
    await cancelAlgo(session.backend, session.token, algoId);
  } catch (err) {
    algosUi.showBoletaError(`Error cancelling ${algoId}: ${err?.message || err}`);
  } finally {
    state.markAlgoCancelInflight(algoId, false);
  }
}

async function handleModifyAlgo(algoId, payload) {
  if (!session || !algoId) return;
  state.markAlgoModifyInflight(algoId, true);
  try {
    await modifyAlgo(session.backend, session.token, algoId, payload);
    algosUi.showBoletaSuccess(`Modify sent for ${algoId}.`);
  } catch (err) {
    algosUi.showBoletaError(`Error modifying ${algoId}: ${err?.message || err}`);
  } finally {
    state.markAlgoModifyInflight(algoId, false);
  }
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

async function handleCreateBotCredential({ label, boundCertThumbprint = null }) {
  if (!session) return;
  const captured = session;
  botCredentialsUi.setCreateSubmitting(true);
  botCredentialsUi.setBotCredentialsFeedback(null);
  try {
    const created = await createUserBotCredential(
      captured.backend,
      captured.token,
      label,
      boundCertThumbprint,
    );
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

async function handleSetCertBinding({ id, label, boundCertThumbprint }) {
  if (!session) return;
  const captured = session;
  botCredentialsUi.setBotCredentialsFeedback(null);
  try {
    await setUserBotCredentialCertBinding(
      captured.backend,
      captured.token,
      id,
      boundCertThumbprint,
    );
    if (session !== captured) return;
    botCredentialsUi.setBotCredentialsFeedback(
      boundCertThumbprint
        ? `Credential "${label}" cert pin updated.`
        : `Credential "${label}" cert pin cleared.`,
      "ok");
    await refreshBotCredentials();
  } catch (err) {
    if (session !== captured) return;
    if (err?.status === 401) { logout(); return; }
    botCredentialsUi.setBotCredentialsFeedback(
      err?.message || "Failed to update credential cert pin.", "error");
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

// ── Q2.6 (#273). History / P&L / Statement orchestration ──────────
//
// On entering the History view we:
//   * seed the P&L panel via GET /pnl/today (the `pnl.me` WS channel
//     also keeps the state slice live in the background — subscribed
//     statically from the worker, so no per-view subscribe dance);
//   * load the first page of orders + executions history under the
//     current filters (default: today, all symbols).
//
// `Apply filters` resets both buffers and re-fetches page 1; `Load
// more` pages forward by passing the slice's nextCursor.

function _toIsoFrom(dateStr) {
  // Date inputs surface YYYY-MM-DD; the history endpoints expect an
  // ISO-8601 timestamp. Treat `from` as 00:00:00Z and `to` as 23:59:59Z
  // so a calendar pick of "today" actually returns today's rows in
  // both directions.
  if (!dateStr) return undefined;
  return `${dateStr}T00:00:00Z`;
}

function _toIsoTo(dateStr) {
  if (!dateStr) return undefined;
  // Use .999Z so the final-second events are included: the backend
  // history endpoints filter with `> to` (HistoryEndpoints.cs), so a
  // bare `23:59:59Z` would drop everything in the last second.
  return `${dateStr}T23:59:59.999Z`;
}

function _historyOpts({ withCursor = false, kind } = {}) {
  const f = state.getState().historyFilters || {};
  const opts = {
    from:   _toIsoFrom(f.from),
    to:     _toIsoTo(f.to),
    symbol: f.symbol || undefined,
    limit:  100,
  };
  if (withCursor) {
    const slice = kind === "executions"
      ? state.getState().historyExecutions
      : state.getState().historyOrders;
    if (slice?.nextCursor) opts.cursor = slice.nextCursor;
  }
  return opts;
}

async function refreshHistoryAll() {
  if (!session) return;
  // Bump the history generation BEFORE issuing the fetches so any
  // earlier in-flight refreshHistoryAll / loadMoreHistory{Orders,
  // Executions}(reset=true) call (which captured the previous
  // generation) sees a generation mismatch on resolution and is
  // dropped. Filter-change / clearAll already bump, but an explicit
  // refresh with the same filters would otherwise share a generation
  // with the previous refresh and let an older (slower) response
  // clobber the newer one. Mirrors bumpPnlEpoch() in refreshPnl().
  state.bumpHistoryGeneration();
  // Fetch P&L (REST seed — WS keeps it live afterwards) in parallel
  // with the first page of each history list.
  await Promise.all([
    refreshPnl(),
    loadMoreHistoryOrders(true),
    loadMoreHistoryExecutions(true),
  ]);
}

async function refreshPnl() {
  if (!session) return;
  const captured = session;
  // Bump the pnl epoch BEFORE issuing the fetch so any earlier
  // in-flight refreshPnl() call (which captured the previous epoch)
  // sees an epoch mismatch on resolution and is dropped — guards
  // against REST-vs-REST races where the slower (older) response
  // could otherwise overwrite the newer one. Also covers the
  // WS-delta-mid-flight case: any subsequent delta bumps the epoch
  // again, and our own apply becomes a no-op.
  state.bumpPnlEpoch();
  const epoch = state.getPnlEpoch();
  try {
    const dto = await getPnlToday(captured.backend, captured.token);
    if (session !== captured) return;
    state.applyPnlSnapshot(dto, { ifEpoch: epoch });
  } catch (err) {
    if (session !== captured) return;
    if (err?.status === 401) { logout(); return; }
    historyUi.setHistoryFeedback(err?.message || "Failed to load P&L.", "error");
  }
}

function handleHistoryApplyFilters(filters) {
  state.setHistoryFilters(filters || {});
  loadMoreHistoryOrders(true);
  loadMoreHistoryExecutions(true);
}

async function loadMoreHistoryOrders(reset) {
  if (!session) return;
  const captured = session;
  // Capture the history generation BEFORE awaiting so a filter change
  // (or resetHistory) mid-flight invalidates this call's response.
  const gen = state.getHistoryGeneration();
  const opts = _historyOpts({ withCursor: !reset, kind: "orders" });
  state.setHistoryOrdersLoading(true);
  try {
    const page = await getOrdersHistory(captured.backend, captured.token, opts);
    if (session !== captured) return;
    state.applyHistoryOrdersPage({
      items: page?.items ?? [],
      nextCursor: page?.nextCursor ?? null,
      reset: !!reset,
      ifGeneration: gen,
    });
  } catch (err) {
    if (session !== captured) return;
    state.setHistoryOrdersLoading(false);
    if (err?.status === 401) { logout(); return; }
    historyUi.setHistoryFeedback(err?.message || "Failed to load orders history.", "error");
  }
}

async function loadMoreHistoryExecutions(reset) {
  if (!session) return;
  const captured = session;
  const gen = state.getHistoryGeneration();
  const opts = _historyOpts({ withCursor: !reset, kind: "executions" });
  state.setHistoryExecutionsLoading(true);
  try {
    const page = await getExecutionsHistory(captured.backend, captured.token, opts);
    if (session !== captured) return;
    state.applyHistoryExecutionsPage({
      items: page?.items ?? [],
      nextCursor: page?.nextCursor ?? null,
      reset: !!reset,
      ifGeneration: gen,
    });
  } catch (err) {
    if (session !== captured) return;
    state.setHistoryExecutionsLoading(false);
    if (err?.status === 401) { logout(); return; }
    historyUi.setHistoryFeedback(err?.message || "Failed to load executions history.", "error");
  }
}

async function handleStatementDownload(dayKey) {
  if (!session) return;
  const captured = session;
  state.setStatementBusy(true);
  historyUi.setHistoryFeedback(null);
  try {
    const { blob, filename } = await downloadStatementCsv(captured.backend, captured.token, dayKey);
    if (session !== captured) return;
    historyUi.triggerBlobDownload(blob, filename);
    state.setStatementDownload({ dayKey, filename, bytes: blob?.size ?? null });
    historyUi.setHistoryFeedback(`statement ${filename} downloaded`, "ok");
  } catch (err) {
    if (session !== captured) return;
    if (err?.status === 401) { logout(); return; }
    state.setStatementError(err?.message || "statement download failed");
    historyUi.setHistoryFeedback(err?.message || "statement download failed", "error");
  }
}

async function handleStatementViewJson(dayKey) {
  if (!session) return;
  const captured = session;
  state.setStatementBusy(true);
  try {
    const json = await getStatement(captured.backend, captured.token, dayKey);
    if (session !== captured) return;
    state.setStatementJson(json);
    historyUi.openStatementJsonModal(json);
  } catch (err) {
    if (session !== captured) return;
    if (err?.status === 401) { logout(); return; }
    state.setStatementError(err?.message || "failed to load statement");
    historyUi.setHistoryFeedback(err?.message || "failed to load statement", "error");
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

// ── Q4.14 (#314). Compliance handlers ──────────────────────────────

function openComplianceDropCopy() {
  if (!session?.token || !session?.backend) return;
  try {
    const url = buildDropCopyWebSocketUrl(session.backend, session.token);
    complianceUi.openDropCopyFeed(url);
  } catch (err) {
    console.warn("[compliance/dropcopy] url build failed", err);
  }
}

function closeComplianceDropCopy() {
  complianceUi.closeDropCopyFeed();
}

async function handleAuditSearch(opts) {
  if (!session) return;
  const captured = session;
  complianceUi.setAuditFeedback(null);
  try {
    const page = await searchAuditLog(captured.backend, captured.token, opts);
    if (session !== captured) return;
    complianceUi.setAuditResults(page);
  } catch (err) {
    if (err.status === 401) { logout(); return; }
    if (err.status === 403) {
      complianceUi.setAuditFeedback("forbidden — role lost?", "error");
      return;
    }
    complianceUi.setAuditFeedback(err.message || "audit search failed", "error");
  }
}

async function handleFillTouchLookup(id) {
  if (!session) return;
  const captured = session;
  complianceUi.setTouchFeedback(null);
  complianceUi.setTouchResult(null, null);
  try {
    const dto = await getFillTouch(captured.backend, captured.token, id);
    if (session !== captured) return;
    complianceUi.setTouchResult(id, dto);
  } catch (err) {
    if (err.status === 401) { logout(); return; }
    if (err.status === 404) {
      complianceUi.setTouchFeedback(`No fill ${id} in your firm.`, "error");
      return;
    }
    complianceUi.setTouchFeedback(err.message || "lookup failed", "error");
  }
}

async function handleCvmDownload({ model, date }) {
  if (!session) return;
  const captured = session;
  complianceUi.setCvmFeedback(`Downloading model ${model} for ${date}…`);
  try {
    const { blob, filename } = await downloadCvmReport(captured.backend, captured.token, model, date);
    if (session !== captured) return;
    triggerBlobDownload(blob, filename);
    complianceUi.setCvmFeedback(`Saved ${filename}.`, "ok");
  } catch (err) {
    if (err.status === 401) { logout(); return; }
    if (err.status === 404) {
      complianceUi.setCvmFeedback("No rows for that date.", "error");
      return;
    }
    if (err.status === 429) {
      complianceUi.setCvmFeedback("Rate-limited — retry shortly.", "error");
      return;
    }
    if (err.status === 503) {
      complianceUi.setCvmFeedback("WAL backpressure — retry shortly.", "error");
      return;
    }
    complianceUi.setCvmFeedback(err.message || "download failed", "error");
  }
}

function triggerBlobDownload(blob, filename) {
  if (typeof URL === "undefined" || typeof document === "undefined") return;
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  // Revoke on the next tick so Safari has a chance to start the
  // download before the URL is invalidated.
  setTimeout(() => { try { URL.revokeObjectURL(url); } catch { /* noop */ } }, 0);
}

init();
