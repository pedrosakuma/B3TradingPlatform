import { formatCurrency, formatPrice, formatUtcDateTime } from "./formatters.js";

const $ = (id) => document.getElementById(id);
const STALE_AFTER_MS = 60_000;

let role = "user";
let handlers = {};
let subAccounts = resource([]);
let phase = resource(null);
let risk = resource(null);
let references = resource(null);
let staleTimer = null;

function resource(data) {
  return { status: "idle", data, error: null, fetchedAt: null };
}

export function setOperationsHandlers(next) {
  handlers = { ...handlers, ...next };
}

export function setOperationsRole(nextRole) {
  role = nextRole ?? "user";
  renderSubAccounts();
}

export function resetOperations() {
  if (staleTimer) clearTimeout(staleTimer);
  staleTimer = null;
  // Identity boundary: never carry a prior user's firm subaccount into the
  // next session. In-session refreshes use setSubAccountsResource() and keep
  // unavailable selections visible/blocked instead.
  const select = $("ticket-subaccount");
  if (select) select.value = "";
  subAccounts = resource([]);
  phase = resource(null);
  risk = resource(null);
  references = resource(null);
  renderAll();
}

export function setSubAccountsResource(next) {
  subAccounts = { ...subAccounts, ...next };
  renderSubAccounts();
  scheduleStaleRender();
}

export function setPhaseResource(next) {
  phase = { ...phase, ...next };
  renderPhase();
  scheduleStaleRender();
}

export function setRiskResource(next) {
  risk = { ...risk, ...next };
  renderRisk();
  scheduleStaleRender();
}

export function setReferenceResource(next) {
  references = { ...references, ...next };
  renderReferences();
  scheduleStaleRender();
}

export function bindOperationsUi() {
  $("ticket-subaccount")?.addEventListener("change", renderSubAccounts);
  $("ticket-subaccount-refresh")?.addEventListener("click", () =>
    handlers.onRefreshSubAccounts?.());
  $("subaccount-create-form")?.addEventListener("submit", (event) => {
    event.preventDefault();
    runMutation(event.currentTarget, () => handlers.onCreateSubAccount?.({
      id: $("subaccount-id")?.value.trim(),
      displayName: $("subaccount-name")?.value.trim() || null,
    }));
  });
  $("subaccount-rows")?.addEventListener("click", (event) => {
    const button = event.target.closest(".subaccount-deactivate");
    if (!button) return;
    if (!window.confirm(`Deactivate subaccount ${button.dataset.id}? Existing history remains, but new orders will be rejected.`)) return;
    runMutation(button, () => handlers.onDeactivateSubAccount?.(button.dataset.id));
  });
  $("session-phase-form")?.addEventListener("submit", (event) => {
    event.preventDefault();
    const symbol = $("session-phase-symbol")?.value.trim().toUpperCase() || "platform default";
    const value = $("session-phase-value")?.value;
    if (!window.confirm(`Set ${symbol} session phase to ${value}?`)) return;
    runMutation(event.currentTarget, () => handlers.onSetPhase?.({
      symbol: symbol === "platform default" ? null : symbol,
      phase: value,
    }));
  });
  $("session-phase-clear")?.addEventListener("click", (event) => {
    const symbol = $("session-phase-symbol")?.value.trim().toUpperCase();
    if (!symbol) return setFeedback("Enter a symbol override to clear.", "error");
    runMutation(event.currentTarget, () => handlers.onClearPhase?.(symbol));
  });
  $("risk-query-form")?.addEventListener("submit", (event) => {
    event.preventDefault();
    handlers.onLoadRisk?.({
      endClient: $("risk-end-client")?.value.trim(),
      firmId: $("risk-firm")?.value.trim(),
      symbol: $("risk-symbol")?.value.trim().toUpperCase(),
    });
  });
  $("risk-reload")?.addEventListener("click", (event) =>
    runMutation(event.currentTarget, () => handlers.onReloadRisk?.()));
  $("reference-price-form")?.addEventListener("submit", (event) => {
    event.preventDefault();
    handlers.onLoadReferences?.($("reference-symbols")?.value.trim());
  });
  $("cash-form")?.addEventListener("submit", (event) => {
    event.preventDefault();
    const kind = $("cash-kind")?.value;
    const amount = Number($("cash-amount")?.value);
    const endclient = $("cash-end-client")?.value.trim();
    if (!window.confirm(`${kind} ${formatCurrency(amount)} for ${endclient}? This writes the audited cash ledger.`)) return;
    runMutation(event.currentTarget, () => handlers.onCash?.({
      endclient,
      kind,
      amount,
      currency: "BRL",
      reference: $("cash-reference")?.value.trim() || null,
    }), "cash-result");
  });
  $("stale-order-form")?.addEventListener("submit", (event) => {
    event.preventDefault();
    const stale = $("stale-action")?.value === "mark";
    const clOrdId = $("stale-clordid")?.value.trim();
    if (!window.confirm(`${stale ? "Mark" : "Clear"} stale status for order ${clOrdId}?`)) return;
    runMutation(event.currentTarget, () => handlers.onSetOrderStale?.({
      firmId: $("stale-firm")?.value.trim(),
      clOrdId,
      stale,
      reason: $("stale-reason")?.value.trim() || null,
    }));
  });
  renderAll();
}

export async function runMutation(control, action, outputId = null) {
  if (!action) return;
  const feedbackId = outputId ?? "operations-feedback";
  const elements = control?.tagName === "FORM"
    ? [...control.querySelectorAll("button, input, select")]
    : [control];
  for (const el of elements) el.disabled = true;
  setFeedback("Operation pending…", "info", feedbackId);
  try {
    const result = await action();
    if (result !== undefined) {
      setFeedback(
        typeof result === "string" ? result : JSON.stringify(result),
        "ok",
        feedbackId,
      );
    }
  } catch (error) {
    setFeedback(error?.message || "Operation failed.", "error", feedbackId);
  } finally {
    for (const el of elements) el.disabled = false;
  }
}

export function setFeedback(message, kind = "info", id = "operations-feedback") {
  const el = $(id);
  if (!el) return;
  el.hidden = !message;
  el.textContent = message ?? "";
  el.className = `feedback ${kind === "ok" ? "ok" : kind === "error" ? "error" : ""}`;
}

function renderAll() {
  renderSubAccounts();
  renderPhase();
  renderRisk();
  renderReferences();
}

function renderSubAccounts() {
  const select = $("ticket-subaccount");
  const rows = Array.isArray(subAccounts.data) ? subAccounts.data : [];
  if (select) {
    const previous = select.value;
    const activeRows = rows.filter((row) => row.active);
    const previousIsActive = previous === "" || activeRows.some((row) => row.id === previous);
    const unavailable = previous && !previousIsActive
      ? `<option value="${escapeHtml(previous)}" disabled>${escapeHtml(previous)} (unavailable)</option>`
      : "";
    select.innerHTML = `<option value="">Master account</option>${unavailable}${activeRows
      .map((row) => `<option value="${escapeHtml(row.id)}">${escapeHtml(row.displayName || row.id)}</option>`)
      .join("")}`;
    select.value = previous;
    const sourceAvailable = subAccounts.status === "ready" && !isStale(subAccounts);
    select.disabled = !sourceAvailable;
    // A successful refresh may reveal that the previously selected account
    // was deactivated. Keep that value visible and blocked until the trader
    // explicitly switches to Master or another active account.
    select.dataset.available = sourceAvailable && previousIsActive ? "1" : "0";
    select.setAttribute("aria-busy", subAccounts.status === "loading" ? "true" : "false");
  }
  const hint = $("subaccount-ticket-hint");
  if (hint) {
    hint.textContent = subAccounts.status === "loading"
      ? "Loading firm subaccounts…"
      : subAccounts.status === "error"
        ? `Subaccounts unavailable: ${subAccounts.error || "request failed"}`
        : isStale(subAccounts)
          ? "Subaccount list is stale; refresh before changing account."
        : select?.value && select.dataset.available === "0"
          ? `Selected subaccount ${select.value} is unavailable; choose Master or another active account.`
        : rows.filter((row) => row.active).length === 0
          ? "No active subaccounts; orders will use the master account."
          : "Orders are booked to the selected firm subaccount.";
  }
  const refresh = $("ticket-subaccount-refresh");
  if (refresh) refresh.disabled = subAccounts.status === "loading";
  const body = $("subaccount-rows");
  if (!body) return;
  const stateRow = resourceRow(subAccounts, 4, "No subaccounts for this firm.");
  if (stateRow) { body.innerHTML = stateRow; return; }
  body.innerHTML = rows.map((row) => `<tr>
    <td><code>${escapeHtml(row.id)}</code></td>
    <td>${escapeHtml(row.displayName || "—")}</td>
    <td>${row.active ? "Active" : "Deactivated"}</td>
    <td>${role === "admin" && row.active
      ? `<button type="button" class="subaccount-deactivate btn btn-danger btn-sm" data-id="${escapeHtml(row.id)}">Deactivate</button>`
      : ""}</td>
  </tr>`).join("");
}

function renderPhase() {
  const output = $("session-phase-output");
  if (!output) return;
  const stateText = resourceText(phase, "No phase configuration returned.");
  if (stateText) { output.textContent = stateText; return; }
  const overrides = phase.data?.overrides ?? {};
  output.textContent = (isStale(phase) ? "Stale data — refresh required.\n" : "")
    + `Default: ${phase.data?.default ?? "—"}\n`
    + (Object.keys(overrides).length
      ? Object.entries(overrides).map(([symbol, value]) => `${symbol}: ${value}`).join("\n")
      : "Overrides: none");
}

function renderRisk() {
  const output = $("risk-output");
  if (!output) return;
  const stateText = resourceText(risk, "No limits returned.");
  output.textContent = stateText || (isStale(risk) ? "Stale data — refresh required.\n" : "")
    + JSON.stringify(risk.data, null, 2);
}

function renderReferences() {
  const body = $("reference-price-rows");
  if (!body) return;
  const items = references.data?.symbols ?? [];
  const stateRow = resourceRow({ ...references, data: items }, 5, "No reference prices configured.");
  if (stateRow) { body.innerHTML = stateRow; return; }
  body.innerHTML = items.map((row) => `<tr>
    <td><code>${escapeHtml(row.symbol)}</code></td>
    <td class="num">${formatPrice(row.effectivePrice)}</td>
    <td>${escapeHtml(row.effectiveSource ?? "Missing")}</td>
    <td>${escapeHtml(row.live
      ? `${formatPrice(row.live.price)} @ ${formatUtcDateTime(row.live.updatedUtc, { fallback: "unknown time" })}`
      : "—")}</td>
    <td class="num">${formatPrice(row.fallbackPrice)}</td>
  </tr>`).join("");
}

function resourceText(value, emptyText) {
  if (value.status === "loading") return "Loading…";
  if (value.status === "error") return `Error: ${value.error || "request failed"}`;
  if (value.status === "idle") return "Not loaded.";
  return value.data == null ? emptyText : "";
}

function resourceRow(value, colspan, emptyText) {
  if (value.status === "loading") return row(colspan, "Loading…", "loading");
  if (value.status === "error") return row(colspan, `Error: ${value.error || "request failed"}`, "error");
  if (value.status === "idle") return row(colspan, "Not loaded.", "idle");
  if (isStale(value)) return row(colspan, "Stale data — refresh required.", "stale");
  if (!Array.isArray(value.data) || value.data.length === 0) return row(colspan, emptyText, "empty");
  return "";
}

function row(colspan, text, state) {
  return `<tr data-state="${state}"><td colspan="${colspan}" class="muted">${escapeHtml(text)}</td></tr>`;
}

function isStale(value) {
  return value.status === "ready" && value.fetchedAt != null
    && Date.now() - value.fetchedAt > STALE_AFTER_MS;
}

function scheduleStaleRender() {
  if (staleTimer) clearTimeout(staleTimer);
  const now = Date.now();
  const times = [subAccounts, phase, risk, references]
    .filter((value) => value.status === "ready" && value.fetchedAt != null)
    .map((value) => value.fetchedAt + STALE_AFTER_MS)
    .filter((time) => time > now);
  if (times.length === 0) { staleTimer = null; return; }
  const delay = Math.max(1, Math.min(...times) - now + 1);
  staleTimer = setTimeout(() => {
    staleTimer = null;
    renderAll();
    scheduleStaleRender();
  }, delay);
  staleTimer?.unref?.();
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, (char) => (
    { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[char]
  ));
}
