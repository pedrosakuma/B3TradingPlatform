const STORAGE_PREFIX = "b3tp.onboarding.first-order.v1";
const DISMISSED = "dismissed";
const COMPLETED = "completed";

let context = null;
let acceptedClOrdId = null;
let activeUsername = null;
let forcedOpen = false;
let completionAcknowledged = false;

function storageKey(username) {
  return `${STORAGE_PREFIX}:${encodeURIComponent(username)}`;
}

export function readFirstOrderOnboarding(storage, username) {
  if (!storage || !username) return null;
  try {
    const value = storage.getItem(storageKey(username));
    return value === DISMISSED || value === COMPLETED ? value : null;
  } catch {
    return null;
  }
}

export function writeFirstOrderOnboarding(storage, username, value) {
  if (!storage || !username || (value !== DISMISSED && value !== COMPLETED)) return false;
  try {
    storage.setItem(storageKey(username), value);
    return true;
  } catch {
    return false;
  }
}

export function deriveFirstOrderProgress(state, clOrdId = null) {
  const connected = state?.status === "connected";
  const accepted = clOrdId != null && String(clOrdId).length > 0;
  const order = accepted && state?.orders instanceof Map
    ? state.orders.get(String(clOrdId))
    : null;
  const stage = order ? 3 : accepted ? 2 : connected ? 1 : 0;

  if (stage === 3) {
    const terminal = ["Filled", "Cancelled", "Rejected", "Replaced"].includes(order.status);
    if (terminal) {
      return {
        stage,
        message: `Order ${clOrdId} completed with status ${order.status}. Review the execution outcome.`,
        action: "View executions",
        target: "executions",
      };
    }
    return {
      stage,
      message: `Order ${clOrdId} is in Working Orders. Select it to inspect or manage it.`,
      action: "View order",
      target: "blotter",
    };
  }
  if (stage === 2) {
    return {
      stage,
      message: `Order ${clOrdId} was accepted. Waiting for its live order update.`,
      action: "View Working Orders",
      target: "blotter",
    };
  }
  if (stage === 1) {
    return {
      stage,
      message: "Use a limit order, review its estimated notional and any risk message, then submit.",
      action: "Focus the ticket",
      target: "ticket",
    };
  }
  return {
    stage,
    message: "Waiting for the trading connection. You can review the ticket while it connects.",
    action: "Review the ticket",
    target: "ticket",
  };
}

function getElement(id) {
  return document.getElementById(id);
}

function focusTicket() {
  getElement("ticket-symbol")?.focus({ preventScroll: false });
}

function viewOrderOutcome(target, clOrdId) {
  const tab = target === "executions" ? "executions" : "blotter";
  document.querySelector(`[data-trader-bottom-tab="${tab}"]`)?.click();
  const panel = getElement(`trader-bottom-panel-${tab}`);
  panel?.scrollIntoView({ behavior: "smooth", block: "nearest" });
  requestAnimationFrame(() => {
    const selector = tab === "executions"
      ? "#executions-log"
      : clOrdId == null
        ? "#blotter-body"
        : `#blotter-body tr[data-clordid="${CSS.escape(String(clOrdId))}"]`;
    const focusTarget = document.querySelector(selector)
      || getElement(tab === "executions" ? "executions-log" : "blotter-body");
    if (!focusTarget) return;
    focusTarget.setAttribute("tabindex", "-1");
    focusTarget.focus({ preventScroll: true });
  });
}

function setVisible(visible) {
  const guide = getElement("first-order-guide");
  const reopen = getElement("first-order-guide-open");
  if (guide) guide.hidden = !visible;
  if (reopen) reopen.hidden = visible || !activeUsername;
}

function render() {
  if (!context) return;
  const state = context.getState();
  const username = state?.user?.username ?? null;

  if (username !== activeUsername) {
    activeUsername = username;
    acceptedClOrdId = null;
    forcedOpen = false;
    completionAcknowledged = false;
  }

  if (!username) {
    setVisible(false);
    return;
  }

  const persisted = readFirstOrderOnboarding(context.storage, username);
  const shouldShow = forcedOpen
    || persisted == null
    || (persisted === COMPLETED && acceptedClOrdId != null && !completionAcknowledged);
  setVisible(shouldShow);
  if (!shouldShow) return;

  const progress = deriveFirstOrderProgress(state, acceptedClOrdId);
  const guide = getElement("first-order-guide");
  const status = getElement("first-order-guide-status");
  const message = getElement("first-order-guide-message");
  const action = getElement("first-order-guide-action");
  const dismiss = getElement("first-order-guide-dismiss");

  if (guide) guide.dataset.stage = String(progress.stage);
  if (status) status.textContent = progress.stage === 3 ? "Ready" : `Step ${progress.stage + 1} of 3`;
  if (message) message.textContent = progress.message;
  if (action) action.textContent = progress.action;
  if (dismiss) dismiss.textContent = progress.stage === 3 ? "Done" : "Skip guide";

  for (let index = 1; index <= 3; index += 1) {
    const step = getElement(`first-order-step-${index}`);
    const marker = step?.querySelector(".first-order-step-marker");
    if (!step || !marker) continue;
    const complete = index <= progress.stage;
    const current = index === progress.stage + 1 && progress.stage < 3;
    step.classList.toggle("is-complete", complete);
    step.toggleAttribute("aria-current", current);
    marker.textContent = complete ? "✓" : String(index);
  }

  if (progress.stage === 3) {
    writeFirstOrderOnboarding(context.storage, username, COMPLETED);
  }
}

export function bindFirstOrderOnboarding({
  getState,
  subscribe,
  storage = globalThis.localStorage,
} = {}) {
  if (typeof getState !== "function" || typeof subscribe !== "function") return () => {};
  if (!getElement("first-order-guide")) return () => {};

  context = { getState, storage };

  getElement("first-order-guide-action")?.addEventListener("click", () => {
    const progress = deriveFirstOrderProgress(context.getState(), acceptedClOrdId);
    if (progress.stage >= 2) viewOrderOutcome(progress.target, acceptedClOrdId);
    else focusTicket();
  });

  getElement("first-order-guide-dismiss")?.addEventListener("click", () => {
    if (!activeUsername) return;
    const progress = deriveFirstOrderProgress(context.getState(), acceptedClOrdId);
    writeFirstOrderOnboarding(
      context.storage,
      activeUsername,
      progress.stage === 3 ? COMPLETED : DISMISSED,
    );
    completionAcknowledged = progress.stage === 3;
    if (completionAcknowledged) acceptedClOrdId = null;
    forcedOpen = false;
    setVisible(false);
    getElement("first-order-guide-open")?.focus();
  });

  getElement("first-order-guide-open")?.addEventListener("click", () => {
    forcedOpen = true;
    acceptedClOrdId = null;
    completionAcknowledged = false;
    render();
    getElement("first-order-guide-action")?.focus();
  });

  const unsubscribe = subscribe((slice) => {
    if (slice === "user" || slice === "status" || slice === "orders" || slice === "all") render();
  });
  render();

  return () => {
    unsubscribe?.();
    context = null;
  };
}

export function markFirstOrderAccepted(clOrdId) {
  if (!context || clOrdId == null) return;
  acceptedClOrdId = String(clOrdId);
  render();
}
