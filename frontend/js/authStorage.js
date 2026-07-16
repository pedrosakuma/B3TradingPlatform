export const SESSION_KEY = "b3tp.session";
export const AUTH_BACKEND_KEY = "b3tp.auth.backend";
export const LOGOUT_EVENT_KEY = "b3tp.auth.logout";

function safeGet(store, key) {
  try { return store?.getItem?.(key) ?? null; } catch { return null; }
}
function safeSet(store, key, value) {
  try { store?.setItem?.(key, value); } catch { /* private mode */ }
}
function safeRemove(store, key) {
  try { store?.removeItem?.(key); } catch { /* private mode */ }
}

export function readStoredSession(store, now = Date.now()) {
  try {
    const raw = store?.getItem?.(SESSION_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (!parsed?.token || !parsed?.expiresAt) return null;
    if (new Date(parsed.expiresAt).getTime() <= now) return null;
    return parsed;
  } catch {
    return null;
  }
}

export function readInternalSession({ authMode = "Local", sessionStorage, localStorage, now = Date.now() }) {
  if (authMode === "Entra") {
    safeRemove(localStorage, SESSION_KEY);
    const fromTab = readStoredSession(sessionStorage, now);
    if (fromTab?.authMode === "Entra") {
      return { session: fromTab, preferredStore: "sessionStorage" };
    }
    if (fromTab) safeRemove(sessionStorage, SESSION_KEY);
    return { session: null, preferredStore: "sessionStorage" };
  }

  const fromTab = readStoredSession(sessionStorage, now);
  if (fromTab) return { session: fromTab, preferredStore: "sessionStorage" };

  const fromBoot = readStoredSession(localStorage, now);
  if (!fromBoot) return { session: null, preferredStore: "sessionStorage" };
  safeSet(sessionStorage, SESSION_KEY, JSON.stringify(fromBoot));
  return { session: fromBoot, preferredStore: fromBoot.remember ? "localStorage" : "sessionStorage" };
}

export function writeInternalSession(session, { authMode = "Local", sessionStorage, localStorage }) {
  const record = authMode === "Entra" ? { ...session, remember: false, authMode: "Entra" } : session;
  safeSet(sessionStorage, SESSION_KEY, JSON.stringify(record));
  if (authMode === "Entra") safeRemove(localStorage, SESSION_KEY);
  if (authMode !== "Entra" && record.remember) safeSet(localStorage, SESSION_KEY, JSON.stringify(record));
  return authMode !== "Entra" && record.remember ? "localStorage" : "sessionStorage";
}

export function clearInternalSession({ sessionStorage, localStorage }) {
  safeRemove(sessionStorage, SESSION_KEY);
  safeRemove(localStorage, SESSION_KEY);
}

export function rememberAuthBackend(backend, { sessionStorage }) {
  if (backend) safeSet(sessionStorage, AUTH_BACKEND_KEY, backend);
}

export function readAuthBackend({ sessionStorage }) {
  return safeGet(sessionStorage, AUTH_BACKEND_KEY) || "";
}

export function clearAuthBackend({ sessionStorage }) {
  safeRemove(sessionStorage, AUTH_BACKEND_KEY);
}

export function createLogoutChannel({ name = "b3tp.auth", win = globalThis.window } = {}) {
  let bc = null;
  const listeners = new Set();
  const onMessage = () => {
    for (const listener of listeners) listener();
  };

  if (typeof win?.BroadcastChannel === "function") {
    try {
      bc = new win.BroadcastChannel(name);
      bc.onmessage = onMessage;
    } catch { bc = null; }
  }

  const storageHandler = (event) => {
    if (event?.key === LOGOUT_EVENT_KEY) onMessage();
  };
  try { win?.addEventListener?.("storage", storageHandler); } catch { /* ignore */ }

  return {
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    broadcast() {
      try { bc?.postMessage?.({ type: "logout", at: Date.now() }); } catch { /* ignore */ }
      try {
        win?.localStorage?.setItem?.(LOGOUT_EVENT_KEY, String(Date.now()));
      } catch { /* ignore */ }
    },
    close() {
      try { bc?.close?.(); } catch { /* ignore */ }
      try { win?.removeEventListener?.("storage", storageHandler); } catch { /* ignore */ }
      listeners.clear();
    },
  };
}
