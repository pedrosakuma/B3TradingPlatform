// Fase 5 (#401). Preferences sub-tab — density toggle.
//
// The Settings → Preferences sub-tab was a placeholder until this
// landed. It now hosts the UI density switch (comfortable / compact),
// which is reflected on the document root as `data-density` so the
// CSS can scale rem-based metrics in one place.

import { getState, setDensity, subscribe } from "./state.js";

const DENSITY_VALUES = ["comfortable", "compact"];

function applyDensity(name) {
  const target = DENSITY_VALUES.includes(name) ? name : "comfortable";
  if (typeof document !== "undefined" && document.documentElement) {
    document.documentElement.dataset.density = target;
  }
  const buttons = document.querySelectorAll("#density-toggle .density-option");
  buttons.forEach((btn) => {
    const active = btn.dataset.density === target;
    btn.classList.toggle("active", active);
    btn.setAttribute("aria-pressed", active ? "true" : "false");
  });
}

export function bindPreferencesUi() {
  document.querySelectorAll("#density-toggle .density-option").forEach((btn) => {
    btn.addEventListener("click", () => {
      const name = btn.dataset.density;
      if (!DENSITY_VALUES.includes(name)) return;
      setDensity(name);
    });
  });
  subscribe((slice) => {
    if (slice === "density" || slice === "all") {
      applyDensity(getState().density);
    }
  });
  applyDensity(getState().density);
}
