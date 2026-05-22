// Fase 4 (#400). Trader-view sub-navigation.
//
// The Trader view is now a shell with three sub-tabs (Markets, Watchlist,
// Auctions) plus a tabbed lower band (Working orders / Executions) and a
// compact Quick-ticket with an "Advanced" expander.
//
// Each sub-tab and the lower band are siblings inside `trader-view`; this
// module wires the sub-tablist / bottom-tab clicks + the ticket advanced
// toggle to dedicated state slices and re-renders panel visibility on
// every change to those slices.

import {
  getState,
  setTraderSubTab,
  setTraderBottomTab,
  setTicketAdvancedOpen,
  subscribe,
} from "./state.js";

const SUB_TABS = ["markets", "watchlist", "auctions"];
const BOTTOM_TABS = ["blotter", "executions"];

function isValidSub(name) {
  return SUB_TABS.includes(name);
}
function isValidBottom(name) {
  return BOTTOM_TABS.includes(name);
}

function applySubTab(name) {
  const target = isValidSub(name) ? name : "markets";
  for (const sub of SUB_TABS) {
    const panel = document.getElementById(`trader-panel-${sub}`);
    if (panel) panel.hidden = sub !== target;
  }
  const tabs = document.querySelectorAll("#trader-subtabs .trader-subtab");
  tabs.forEach((tab) => {
    const active = tab.dataset.traderSubtab === target;
    tab.classList.toggle("active", active);
    tab.setAttribute("aria-selected", active ? "true" : "false");
    tab.tabIndex = active ? 0 : -1;
  });
}

function applyBottomTab(name) {
  const target = isValidBottom(name) ? name : "blotter";
  for (const sub of BOTTOM_TABS) {
    const panel = document.getElementById(`trader-bottom-panel-${sub}`);
    if (panel) panel.hidden = sub !== target;
  }
  const tabs = document.querySelectorAll("#trader-bottom-tabs .trader-bottom-tab");
  tabs.forEach((tab) => {
    const active = tab.dataset.traderBottomTab === target;
    tab.classList.toggle("active", active);
    tab.setAttribute("aria-selected", active ? "true" : "false");
    tab.tabIndex = active ? 0 : -1;
  });
}

function applyTicketAdvanced(open) {
  const advanced = document.getElementById("ticket-advanced");
  if (advanced) advanced.hidden = !open;
  const toggle = document.getElementById("ticket-advanced-toggle");
  if (toggle) {
    toggle.setAttribute("aria-expanded", open ? "true" : "false");
    toggle.classList.toggle("active", !!open);
    // Caret glyph mirrors the auction-toggle visual ▸ / ▾
    const caret = toggle.querySelector(".ticket-advanced-caret");
    if (caret) caret.textContent = open ? "▾" : "▸";
  }
}

export function bindTraderUi() {
  document.querySelectorAll("#trader-subtabs .trader-subtab").forEach((tab) => {
    tab.addEventListener("click", () => {
      const name = tab.dataset.traderSubtab;
      if (!isValidSub(name)) return;
      setTraderSubTab(name);
    });
  });
  document.querySelectorAll("#trader-bottom-tabs .trader-bottom-tab").forEach((tab) => {
    tab.addEventListener("click", () => {
      const name = tab.dataset.traderBottomTab;
      if (!isValidBottom(name)) return;
      setTraderBottomTab(name);
    });
  });
  const advancedToggle = document.getElementById("ticket-advanced-toggle");
  if (advancedToggle) {
    advancedToggle.addEventListener("click", () => {
      setTicketAdvancedOpen(!getState().ticketAdvancedOpen);
    });
  }
  subscribe((slice) => {
    if (slice === "traderSubTab" || slice === "all") {
      applySubTab(getState().traderSubTab);
    }
    if (slice === "traderBottomTab" || slice === "all") {
      applyBottomTab(getState().traderBottomTab);
    }
    if (slice === "ticketAdvancedOpen" || slice === "all") {
      applyTicketAdvanced(getState().ticketAdvancedOpen);
    }
  });
  // Initial render so defaults are reflected the first time the trader
  // view is mounted, even before any state change fires.
  applySubTab(getState().traderSubTab);
  applyBottomTab(getState().traderBottomTab);
  applyTicketAdvanced(getState().ticketAdvancedOpen);
}

export { SUB_TABS as TRADER_SUB_TABS, BOTTOM_TABS as TRADER_BOTTOM_TABS };
