// Fase 5 (#401). Global keyboard shortcuts.
//
// All shortcuts live in one place so the bindings are discoverable
// (and documentable — see docs/frontend/keyboard.md). The matcher
// is pure: pass an event-like object and it returns the action name
// (or null). Production wiring lives in bindKeyboardShortcuts; the
// pure resolver is exported separately so it can be unit-tested
// without a real DOM.
//
// Design rules:
//   * Plain letters / "/" only fire when the focused element is NOT
//     a text-editing surface (input / textarea / contenteditable).
//     Otherwise B/S would intercept the trader's typing.
//   * Alt-combos always fire (even from inputs) — the trader needs
//     to switch tabs without having to blur the symbol selector.
//   * Esc always fires.

// Maps a normalised key descriptor to an action name. The descriptor
// is `${alt?"A":""}${shift?"S":""}${ctrl?"C":""}${meta?"M":""}:${key}`.
const SHORTCUTS = Object.freeze({
  // Primary tabs.
  "A:1": "tab:trader",
  "A:2": "tab:algos",
  "A:3": "tab:history",
  "A:4": "tab:settings",
  "A:5": "tab:admin",
  "A:6": "tab:compliance",
  // Trader sub-tabs.
  "AS:1": "trader-sub:markets",
  "AS:2": "trader-sub:watchlist",
  "AS:3": "trader-sub:auctions",
  // Trader bottom-band toggle (Working orders / Executions).
  "A:b": "trader-bottom:blotter",
  "A:e": "trader-bottom:executions",
  // Text-only shortcuts (suppressed when typing in a field).
  ":/": "focus:symbol",
  ":B": "ticket:buy",
  ":S": "ticket:sell",
  // Always-on.
  ":Escape": "modal:close",
});

const ALWAYS_ON = new Set(["modal:close"]);

function isTextEditing(el) {
  if (!el) return false;
  if (el.isContentEditable) return true;
  const tag = (el.tagName || "").toUpperCase();
  if (tag === "TEXTAREA") return true;
  if (tag === "SELECT") return true;
  if (tag === "INPUT") {
    const type = (el.type || "text").toLowerCase();
    // Pure-text typing surfaces. Buttons, checkboxes, ranges, etc.
    // can keep their letter-shortcut bindings (the user can still
    // tab away if they want).
    return ["text", "search", "email", "tel", "url", "password",
            "number", "date", "datetime-local", "time", "month", "week"].includes(type);
  }
  return false;
}

// Normalise an event-like object to the lookup descriptor + a "letter"
// key (case-preserving for B / S so we can match Shift+B vs B). The
// resolver intentionally ignores Caps Lock — modifier flags from the
// event are the source of truth.
function descriptorFor(ev) {
  const key = ev.key;
  if (key == null || key === "") return null;
  const alt = !!ev.altKey;
  let shift = !!ev.shiftKey;
  const ctrl = !!ev.ctrlKey;
  const meta = !!ev.metaKey;
  // For single-letter keys the case already encodes the Shift state
  // (B vs b). Strip the redundant Shift modifier so Shift+B resolves
  // the same shortcut as plain "B".
  if (key.length === 1 && /[A-Za-z]/.test(key)) shift = false;
  // Letter keys preserve case for the un-modified shortcut table
  // (so plain B is distinct from anything Shift-combined elsewhere).
  // Digit keys come through as "1".."6".
  const norm = `${alt ? "A" : ""}${shift ? "S" : ""}${ctrl ? "C" : ""}${meta ? "M" : ""}:${key}`;
  return norm;
}

export function resolveAction(ev) {
  const desc = descriptorFor(ev);
  if (!desc) return null;
  const action = SHORTCUTS[desc];
  if (!action) {
    // Letter keys are upper-case in the table; if the user pressed
    // lowercase without Shift, try the upper-case lookup so "/", "b",
    // "s" all work without forcing the user to know the convention.
    const desc2 = desc.replace(/:([a-z])$/, (_, c) => `:${c.toUpperCase()}`);
    return SHORTCUTS[desc2] || null;
  }
  return action;
}

// `target` is the EventTarget of the keydown (used to suppress letter
// shortcuts while the user is typing). Returns the action, or null
// if the event should propagate untouched.
export function dispatchKey(ev, target) {
  const action = resolveAction(ev);
  if (!action) return null;
  if (!ALWAYS_ON.has(action) && isTextEditing(target)) {
    // Alt-combos are still fine while typing — they don't collide
    // with any standard editing shortcut.
    if (!ev.altKey) return null;
  }
  return action;
}

export function bindKeyboardShortcuts(handlers) {
  if (typeof window === "undefined") return;
  function onKeyDown(ev) {
    const action = dispatchKey(ev, ev.target);
    if (!action) return;
    const fn = handlers[action];
    if (typeof fn !== "function") return;
    // The shortcut consumed the keystroke — prevent the default so
    // "/" doesn't open Firefox's quick-find, Alt+digit doesn't move
    // the browser tab focus, etc.
    ev.preventDefault();
    fn(ev);
  }
  window.addEventListener("keydown", onKeyDown);
  return () => window.removeEventListener("keydown", onKeyDown);
}

export { SHORTCUTS };
