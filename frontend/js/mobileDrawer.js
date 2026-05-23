// Mobile navigation drawer (#408).
//
// On viewports <768px the primary `.view-toggle` tablist (Trading /
// Algos / History / Settings / Compliance / Admin) is hidden via CSS
// and replaced by a hamburger trigger in the topbar plus a slide-in
// drawer. The drawer mirrors the same tab buttons (cloned visibility
// rules from the canonical tablist) so per-role gating stays the
// single source of truth in ui.setViewToggleVisible.
//
// This module is framework-free and DOM-isolated so it can be unit
// tested with the same FakeEl pattern as
// order-detail-lifecycle.test.mjs / virtual-list.test.mjs.
//
// Contract:
//   * `bindMobileDrawer({ trigger, drawer, list, backdrop, onSelect })`
//     wires the trigger button, list items, backdrop click and Esc
//     key. Returns `{ open, close, toggle, syncFromTablist, dispose }`.
//   * `syncFromTablist(tablist)` reads the canonical `.view-toggle`
//     buttons and rebuilds the drawer list — same order, same hidden
//     state, same active state. Cheap, idempotent.
//   * Selecting an item closes the drawer and calls `onSelect(view)`.
//   * Esc closes when the drawer is open (ignored otherwise so it
//     doesn't fight existing modal handlers).

const OPEN_CLASS    = "mobile-drawer-open";
const TRIGGER_LABEL = "Open navigation menu";

export function bindMobileDrawer({ trigger, drawer, list, backdrop, onSelect }) {
  if (!trigger || !drawer || !list || typeof onSelect !== "function") {
    throw new Error("bindMobileDrawer: trigger, drawer, list, onSelect are required");
  }

  let isOpen = false;

  function applyOpenState() {
    drawer.hidden = !isOpen;
    if (backdrop) backdrop.hidden = !isOpen;
    drawer.classList?.toggle(OPEN_CLASS, isOpen);
    trigger.setAttribute("aria-expanded", isOpen ? "true" : "false");
    trigger.setAttribute("aria-label", isOpen ? "Close navigation menu" : TRIGGER_LABEL);
  }

  function open() {
    if (isOpen) return;
    isOpen = true;
    applyOpenState();
    // Move focus into the first visible drawer item for keyboard users.
    const firstBtn = list.querySelector?.("button:not([hidden])");
    firstBtn?.focus?.();
  }

  function close() {
    if (!isOpen) return;
    isOpen = false;
    applyOpenState();
    trigger.focus?.();
  }

  function toggle() {
    if (isOpen) close(); else open();
  }

  function onTriggerClick() { toggle(); }
  function onBackdropClick() { close(); }

  function onListClick(e) {
    const btn = e.target?.closest?.("button[data-view]");
    if (!btn) return;
    const view = btn.dataset?.view;
    if (!view) return;
    close();
    onSelect(view);
  }

  function onKeydown(e) {
    if (!isOpen) return;
    if (e.key === "Escape" || e.key === "Esc") {
      e.preventDefault?.();
      close();
    }
  }

  trigger.addEventListener("click", onTriggerClick);
  list.addEventListener("click", onListClick);
  if (backdrop) backdrop.addEventListener("click", onBackdropClick);
  document.addEventListener("keydown", onKeydown);

  // Initial render so attributes are coherent before the first toggle.
  applyOpenState();

  /**
   * Rebuild the drawer's button list from the canonical `.view-toggle`
   * tablist. Preserves hidden + active state so the per-role gating in
   * ui.setViewToggleVisible remains the single source of truth.
   */
  function syncFromTablist(tablist) {
    if (!tablist || typeof tablist.querySelectorAll !== "function") return;
    const buttons = Array.from(tablist.querySelectorAll("button[data-view]"));
    const html = buttons.map((b) => {
      const view   = b.dataset?.view ?? "";
      const label  = (b.textContent ?? "").trim();
      const hidden = b.hidden ? " hidden" : "";
      const active = b.classList?.contains?.("active") ? " active" : "";
      const sel    = b.getAttribute("aria-selected") === "true" ? "true" : "false";
      return `<button type="button" data-view="${escapeAttr(view)}" ` +
             `class="mobile-drawer-item${active}" role="menuitem" ` +
             `aria-selected="${sel}"${hidden}>${escapeText(label)}</button>`;
    }).join("");
    list.innerHTML = html;
  }

  function dispose() {
    trigger.removeEventListener("click", onTriggerClick);
    list.removeEventListener("click", onListClick);
    if (backdrop) backdrop.removeEventListener("click", onBackdropClick);
    document.removeEventListener("keydown", onKeydown);
  }

  return { open, close, toggle, syncFromTablist, dispose, isOpen: () => isOpen };
}

function escapeAttr(s) {
  return String(s).replace(/[&<>"']/g, (c) => (
    { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]
  ));
}
function escapeText(s) {
  return String(s).replace(/[&<>]/g, (c) => (
    { "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]
  ));
}
