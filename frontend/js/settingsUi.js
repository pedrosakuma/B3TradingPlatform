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
let enabledSubTabs = [...SUB_TABS];

function isValid(name) {
  return enabledSubTabs.includes(name);
}

function applySubTab(name) {
  const target = isValid(name) ? name : enabledSubTabs[0];
  for (const sub of SUB_TABS) {
    const panel = document.getElementById(`settings-panel-${sub}`);
    if (panel) panel.hidden = sub !== target || !enabledSubTabs.includes(sub);
  }
  const tabs = document.querySelectorAll("#settings-subtabs .settings-subtab");
  tabs.forEach((tab) => {
    const enabled = enabledSubTabs.includes(tab.dataset.settingsSubtab);
    const active = enabled && tab.dataset.settingsSubtab === target;
    tab.hidden = !enabled;
    tab.disabled = !enabled;
    tab.classList.toggle("active", active);
    tab.setAttribute("aria-selected", active ? "true" : "false");
    tab.setAttribute("aria-disabled", enabled ? "false" : "true");
    tab.tabIndex = active ? 0 : -1;
  });
}

export function configureSettingsSubTabs({ securityEnabled = true } = {}) {
  enabledSubTabs = SUB_TABS.filter((sub) => securityEnabled || sub !== "security");
}

export function isSettingsSubTabEnabled(name) {
  return isValid(name);
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
