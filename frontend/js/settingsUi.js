// Fase 3 (#399). Settings tab sub-navigation.
//
// The Settings view hosts four sub-tabs (Bot credentials, Security,
// Market data, Preferences). Each lives as a sibling <section> inside
// `settings-view`; this module wires the sub-tablist clicks to the
// `settingsSubTab` state slice and re-renders panel visibility on
// every change to that slice.
//
// The previous standalone surfaces (`bot-credentials-view`,
// `security-modal`, `md-settings-modal`) were folded into these panels
// — see the Fase 3 notes in index.html.

import { getState, setSettingsSubTab, subscribe } from "./state.js";

const SUB_TABS = ["bot-credentials", "security", "market-data", "preferences"];

function isValid(name) {
  return SUB_TABS.includes(name);
}

function applySubTab(name) {
  const target = isValid(name) ? name : "bot-credentials";
  for (const sub of SUB_TABS) {
    const panel = document.getElementById(`settings-panel-${sub}`);
    if (panel) panel.hidden = sub !== target;
  }
  const tabs = document.querySelectorAll("#settings-subtabs .settings-subtab");
  tabs.forEach((tab) => {
    const active = tab.dataset.settingsSubtab === target;
    tab.classList.toggle("active", active);
    tab.setAttribute("aria-selected", active ? "true" : "false");
    tab.tabIndex = active ? 0 : -1;
  });
}

export function bindSettingsUi() {
  const tabs = document.querySelectorAll("#settings-subtabs .settings-subtab");
  tabs.forEach((tab) => {
    tab.addEventListener("click", () => {
      const name = tab.dataset.settingsSubtab;
      if (!isValid(name)) return;
      setSettingsSubTab(name);
    });
  });
  subscribe((slice) => {
    if (slice === "settingsSubTab" || slice === "all") {
      applySubTab(getState().settingsSubTab);
    }
  });
  // Initial render so the default sub-tab is visible the first time
  // Settings is mounted, even before any state change fires.
  applySubTab(getState().settingsSubTab);
}

export { SUB_TABS as SETTINGS_SUB_TABS };
