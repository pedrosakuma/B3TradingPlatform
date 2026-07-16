import test from "node:test";
import assert from "node:assert/strict";

import { SESSION_KEY, readInternalSession, writeInternalSession, clearInternalSession, createLogoutChannel } from "../js/authStorage.js";

function store() {
  const data = new Map();
  return {
    data,
    getItem: (k) => data.has(k) ? data.get(k) : null,
    setItem: (k, v) => data.set(k, String(v)),
    removeItem: (k) => data.delete(k),
  };
}

const future = () => new Date(Date.now() + 60_000).toISOString();

test("Entra mode reads and writes only sessionStorage for internal JWT", () => {
  const ss = store();
  const ls = store();
  ls.setItem(SESSION_KEY, JSON.stringify({ token: "local", expiresAt: future(), remember: true }));
  let result = readInternalSession({ authMode: "Entra", sessionStorage: ss, localStorage: ls });
  assert.equal(result.session, null);
  assert.equal(ls.getItem(SESSION_KEY), null);

  ls.setItem(SESSION_KEY, JSON.stringify({ token: "stale-local", expiresAt: future(), remember: true }));
  writeInternalSession({ token: "entra", expiresAt: future(), remember: true }, {
    authMode: "Entra", sessionStorage: ss, localStorage: ls,
  });
  assert.equal(JSON.parse(ss.getItem(SESSION_KEY)).token, "entra");
  assert.equal(JSON.parse(ss.getItem(SESSION_KEY)).remember, false);
  assert.equal(ls.getItem(SESSION_KEY), null);

  result = readInternalSession({ authMode: "Entra", sessionStorage: ss, localStorage: ls });
  assert.equal(result.session.token, "entra");
});

test("Entra boot purges stale localStorage even when sessionStorage is valid", () => {
  const ss = store();
  const ls = store();
  ss.setItem(SESSION_KEY, JSON.stringify({ token: "entra-tab", expiresAt: future(), authMode: "Entra" }));
  ls.setItem(SESSION_KEY, JSON.stringify({ token: "stale-local", expiresAt: future(), remember: true }));
  const result = readInternalSession({ authMode: "Entra", sessionStorage: ss, localStorage: ls });
  assert.equal(result.session.token, "entra-tab");
  assert.equal(ls.getItem(SESSION_KEY), null);
});

test("Local mode still seeds fresh tabs from remember-me localStorage", () => {
  const ss = store();
  const ls = store();
  ls.setItem(SESSION_KEY, JSON.stringify({ token: "remember", expiresAt: future(), remember: true }));
  const result = readInternalSession({ authMode: "Local", sessionStorage: ss, localStorage: ls });
  assert.equal(result.session.token, "remember");
  assert.equal(JSON.parse(ss.getItem(SESSION_KEY)).token, "remember");
  assert.equal(result.preferredStore, "localStorage");
});

test("clearInternalSession removes both stores", () => {
  const ss = store();
  const ls = store();
  ss.setItem(SESSION_KEY, "{}");
  ls.setItem(SESSION_KEY, "{}");
  clearInternalSession({ sessionStorage: ss, localStorage: ls });
  assert.equal(ss.getItem(SESSION_KEY), null);
  assert.equal(ls.getItem(SESSION_KEY), null);
});

test("logout channel broadcasts without token material", () => {
  const events = new Map();
  const ls = store();
  const win = {
    localStorage: ls,
    addEventListener: (name, fn) => events.set(name, fn),
    removeEventListener: (name) => events.delete(name),
  };
  const channel = createLogoutChannel({ win });
  let count = 0;
  channel.subscribe(() => { count += 1; });
  channel.broadcast();
  events.get("storage")?.({ key: "b3tp.auth.logout" });
  assert.equal(count, 1);
  assert.equal(ls.getItem(SESSION_KEY), null);
});
