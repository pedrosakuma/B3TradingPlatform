// Fase 3 (#399). Pure hash → route parser, isolated so tests can
// import it without dragging app.js's session / storage / worker
// side effects.

const HASH_FOR_VIEW = Object.freeze({
  trader: "#trading",
  algos: "#algos",
  history: "#history",
  settings: "#settings",
  admin: "#admin",
  compliance: "#compliance",
});

const VIEW_FOR_HASH = Object.freeze({
  "#trading": "trader",
  "#trader": "trader",
  "#algos": "algos",
  "#history": "history",
  "#settings": "settings",
  "#admin": "admin",
  "#compliance": "compliance",
});

export const SETTINGS_SUB_TABS = new Set([
  "bot-credentials",
  "security",
  "market-data",
  "preferences",
]);

// Hash schema:
//   #settings                       → settings, default sub-tab
//   #settings/<sub>                 → settings, named sub-tab
//   #bot-credentials                → settings, bot-credentials sub-tab (legacy)
// Anything that doesn't match returns { view: null, subTab: null }.
export function parseHashRoute(hash) {
  if (typeof hash !== "string" || hash.length === 0) return { view: null, subTab: null };
  if (hash === "#bot-credentials") {
    return { view: "settings", subTab: "bot-credentials" };
  }
  if (hash.startsWith("#settings")) {
    const rest = hash.slice("#settings".length);
    if (rest === "") return { view: "settings", subTab: null };
    if (rest.startsWith("/")) {
      const sub = rest.slice(1);
      if (SETTINGS_SUB_TABS.has(sub)) return { view: "settings", subTab: sub };
    }
    return { view: null, subTab: null };
  }
  const view = VIEW_FOR_HASH[hash];
  return { view: view || null, subTab: null };
}

export function hashForView(view, subTab) {
  let hash = HASH_FOR_VIEW[view];
  if (!hash) return null;
  if (view === "settings" && subTab && SETTINGS_SUB_TABS.has(subTab)) {
    hash = `${hash}/${subTab}`;
  }
  return hash;
}

export { HASH_FOR_VIEW, VIEW_FOR_HASH };
