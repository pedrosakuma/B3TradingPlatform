// Q4.14 (#314). Compliance role / UI bundle — unit coverage.
//
// Drives complianceUi.js + the protocol.js helpers against the
// hand-rolled DOM stub used by every other frontend test. The goal
// is to lock the four invariants the integration relies on:
//
//   1. tabsForRole returns the right nav-tab set per JWT role
//      (admin sees everything; compliance sees ONLY the console;
//      plain user never sees admin / compliance).
//   2. The drop-copy feed buffer caps at COMPLIANCE_FEED_CAP and
//      drops the oldest entries first (newest-N preserved).
//   3. Audit-form opts → /api/admin/audit URL building uses the
//      documented query-parameter shape (omitted fields are not
//      sent; ISO timestamps for since/until).
//   4. CVM download URL builds correctly per model + date and
//      that the date-string filename derivation matches the issue
//      spec (cvm_<model>_<yyyymmdd>.xml).

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

import { installDomStub } from "./dom-stub.mjs";

const { elements } = installDomStub({
  ids: {
    "compliance-view":              { tag: "section", hidden: true },
    "compliance-feed-body":         { tag: "tbody" },
    "compliance-feed-pause":        { tag: "button" },
    "compliance-feed-clear":        { tag: "button" },
    "compliance-feed-connection":   { tag: "span" },
    "compliance-feed-status":       { tag: "span" },
    "compliance-audit-form":        { tag: "form" },
    "compliance-audit-since":       { tag: "input" },
    "compliance-audit-until":       { tag: "input" },
    "compliance-audit-user":        { tag: "input" },
    "compliance-audit-type":        { tag: "input" },
    "compliance-audit-outcome":     { tag: "select" },
    "compliance-audit-feedback":    { tag: "p", hidden: true },
    "compliance-audit-body":        { tag: "tbody" },
    "compliance-audit-next":        { tag: "button", hidden: true },
    "compliance-touch-form":        { tag: "form" },
    "compliance-touch-id":          { tag: "input" },
    "compliance-touch-feedback":    { tag: "p", hidden: true },
    "compliance-touch-output":      { tag: "pre", hidden: true },
    "compliance-cvm-date":          { tag: "input" },
    "compliance-cvm-35":            { tag: "button" },
    "compliance-cvm-505":           { tag: "button" },
    "compliance-cvm-feedback":      { tag: "p", hidden: true },
  },
});

// Capture fetch calls. complianceUi.js doesn't talk to the network
// directly — protocol.js does, exercised through helper imports
// below.
const fetchCalls = [];
let fetchResponse = null;
globalThis.fetch = async (url, init) => {
  fetchCalls.push({ url: String(url), init });
  return fetchResponse ?? {
    ok: true,
    status: 200,
    headers: { get: () => null },
    text: async () => "{}",
    json: async () => ({ entries: [], nextCursor: null }),
    blob: async () => ({ size: 0, type: "" }),
  };
};

const compliance = await import("../js/complianceUi.js");
const protocol = await import("../js/protocol.js");
const state = await import("../js/state.js");
const envelopes = JSON.parse(readFileSync(
  new URL("./fixtures/dropcopy-envelopes.json", import.meta.url),
  "utf8",
));
compliance.bindComplianceUi();

test("tabsForRole gates the nav tabs per JWT role", () => {
  // Fase 1 (#397): primary tablist is Trading / Algos / History /
  // Settings (+ Admin / Compliance for admin; Compliance + History
  // for the compliance role). `bot-credentials` is no longer a
  // primary tab — it's reached from inside Settings.
  assert.deepEqual(compliance.tabsForRole("user"),
    ["trader", "algos", "history", "settings"]);
  assert.deepEqual(compliance.tabsForRole("admin"),
    ["trader", "algos", "history", "settings", "admin", "compliance"]);
  assert.deepEqual(compliance.tabsForRole("compliance"),
    ["compliance", "history"]);
  // Unknown role defaults to plain-user surface (least privilege).
  assert.deepEqual(compliance.tabsForRole(undefined),
    ["trader", "algos", "history", "settings"]);
});

test("defaultViewForRole lands compliance on its own console", () => {
  assert.equal(compliance.defaultViewForRole("compliance"), "compliance");
  assert.equal(compliance.defaultViewForRole("admin"), "trader");
  assert.equal(compliance.defaultViewForRole("user"), "trader");
});

test("session renewal only reopens drop-copy for authorized roles", () => {
  const calls = [];
  const reconcile = (role, currentView) => compliance.reconcileComplianceRenewal({
    role,
    currentView,
    onReopen: () => calls.push("reopen"),
    onLeave: () => calls.push("leave"),
  });

  assert.equal(reconcile("compliance", "compliance"), "reopen");
  assert.equal(reconcile("admin", "compliance"), "reopen");
  assert.equal(reconcile("user", "compliance"), "leave");
  assert.equal(reconcile(undefined, "compliance"), "leave");
  assert.equal(reconcile("user", "trader"), "unchanged");
  assert.deepEqual(calls, ["reopen", "reopen", "leave", "leave"]);
});

test("drop-copy feed buffer caps at COMPLIANCE_FEED_CAP and keeps newest", () => {
  state.resetComplianceFeed();
  const cap = state.COMPLIANCE_FEED_CAP;
  // Overflow the ring by 50 — the oldest 50 must fall off the head.
  for (let i = 0; i < cap + 50; i++) {
    state.appendComplianceFeed({ seq: i, type: "fill", symbol: "PETR4" });
  }
  const { entries } = state.getState().complianceFeed;
  assert.equal(entries.length, cap);
  // Oldest retained entry is seq=50 (we dropped 0..49); newest is seq=cap+49.
  assert.equal(entries[0].seq, 50);
  assert.equal(entries[entries.length - 1].seq, cap + 49);
});

test("paused feed drops appended frames on the floor", () => {
  state.resetComplianceFeed();
  state.setComplianceFeedPaused(true);
  state.appendComplianceFeed({ seq: 1, type: "fill" });
  state.appendComplianceFeed({ seq: 2, type: "fill" });
  assert.equal(state.getState().complianceFeed.entries.length, 0);
  state.setComplianceFeedPaused(false);
  state.appendComplianceFeed({ seq: 3, type: "fill" });
  assert.equal(state.getState().complianceFeed.entries.length, 1);
});

test("session reset clears pause while Clear preserves it", () => {
  state.resetComplianceFeed();
  state.appendComplianceFeed({ seq: 1, type: "fill" });
  state.setComplianceFeedPaused(true);

  state.clearComplianceFeed();
  assert.deepEqual(state.getState().complianceFeed, { paused: true, entries: [] });
  state.appendComplianceFeed({ seq: 2, type: "fill" });
  assert.equal(state.getState().complianceFeed.entries.length, 0);

  state.resetComplianceFeed();
  assert.deepEqual(state.getState().complianceFeed, { paused: false, entries: [] });
  state.appendComplianceFeed({ seq: 3, type: "fill" });
  assert.equal(state.getState().complianceFeed.entries.length, 1);
});

test("searchAuditLog only sends populated query args + ISO timestamps", async () => {
  fetchCalls.length = 0;
  fetchResponse = {
    ok: true, status: 200,
    headers: { get: () => null },
    text: async () => '{"entries":[],"nextCursor":null}',
    json: async () => ({ entries: [], nextCursor: null }),
  };
  await protocol.searchAuditLog("http://api", "tok", {
    since: "2025-01-15T00:00:00.000Z",
    user: "alice",
    outcome: "denied",
    limit: 50,
  });
  fetchResponse = null;
  assert.equal(fetchCalls.length, 1);
  const u = new URL(fetchCalls[0].url);
  assert.equal(u.pathname, "/api/admin/audit");
  assert.equal(u.searchParams.get("since"),   "2025-01-15T00:00:00.000Z");
  assert.equal(u.searchParams.get("user"),    "alice");
  assert.equal(u.searchParams.get("outcome"), "denied");
  assert.equal(u.searchParams.get("limit"),   "50");
  // Untouched fields must NOT be on the wire — the backend has its
  // own defaults (last 24h, limit 100) and we don't want to override
  // them with empty strings.
  assert.equal(u.searchParams.has("until"),  false);
  assert.equal(u.searchParams.has("type"),   false);
  assert.equal(u.searchParams.has("cursor"), false);
  assert.equal(fetchCalls[0].init.headers.Authorization, "Bearer tok");
});

test("downloadCvmReport builds /api/reports/cvm/{model}/{date} and saves cvm_<m>_<yyyymmdd>", async () => {
  fetchCalls.length = 0;
  fetchResponse = {
    ok: true, status: 200,
    headers: {
      get: (name) => (name.toLowerCase() === "content-type" ? "application/xml" : null),
    },
    text: async () => "",
    blob: async () => ({ size: 1, type: "application/xml" }),
  };
  const { filename } = await protocol.downloadCvmReport("http://api", "tok", 35, "2025-01-15");
  fetchResponse = null;
  assert.equal(fetchCalls.length, 1);
  assert.equal(fetchCalls[0].url, "http://api/api/reports/cvm/35/2025-01-15");
  assert.equal(filename, "cvm_35_20250115.xml");

  // Model 505 uses the same URL shape.
  fetchCalls.length = 0;
  fetchResponse = {
    ok: true, status: 200,
    headers: {
      get: (name) => (name.toLowerCase() === "content-type" ? "application/xml" : null),
    },
    text: async () => "",
    blob: async () => ({ size: 1, type: "application/xml" }),
  };
  const r2 = await protocol.downloadCvmReport("http://api", "tok", 505, "2025-02-03");
  fetchResponse = null;
  assert.equal(fetchCalls[0].url, "http://api/api/reports/cvm/505/2025-02-03");
  assert.equal(r2.filename, "cvm_505_20250203.xml");
});

test("downloadCvmReport surfaces a structured error on 404/429/503", async () => {
  for (const status of [404, 429, 503]) {
    fetchCalls.length = 0;
    fetchResponse = {
      ok: false, status,
      headers: { get: () => null },
      text: async () => "",
    };
    await assert.rejects(
      () => protocol.downloadCvmReport("http://api", "tok", 35, "2025-01-15"),
      (err) => err.status === status,
    );
  }
  fetchResponse = null;
});

test("downloadCvmReport rejects a 200 HTML fallback response", async () => {
  let blobCalled = false;
  fetchCalls.length = 0;
  fetchResponse = {
    ok: true, status: 200,
    headers: {
      get: (name) => (name.toLowerCase() === "content-type" ? "text/html; charset=utf-8" : null),
    },
    text: async () => "",
    blob: async () => { blobCalled = true; return { size: 1, type: "text/html" }; },
  };
  await assert.rejects(
    () => protocol.downloadCvmReport("http://api", "tok", 35, "2025-01-15"),
    /expected XML/i,
  );
  assert.equal(blobCalled, false);
  fetchResponse = null;
});

test("downloadCvmReport rejects invalid model numbers up-front", async () => {
  await assert.rejects(
    () => protocol.downloadCvmReport("http://api", "tok", 99, "2025-01-15"),
    /model/,
  );
});

test("buildDropCopyWebSocketUrl produces ws:// with ?access_token=", () => {
  const url = protocol.buildDropCopyWebSocketUrl("http://api.example", "abc.def.ghi");
  const u = new URL(url);
  assert.equal(u.protocol, "ws:");
  assert.equal(u.pathname, "/ws/dropcopy");
  assert.equal(u.searchParams.get("access_token"), "abc.def.ghi");
});

test("buildDropCopyWebSocketUrl upgrades https → wss", () => {
  const url = protocol.buildDropCopyWebSocketUrl("https://api.example", "tok");
  assert.match(url, /^wss:\/\//);
});

test("normaliseDropCopyEnvelope expands backend snapshot arrays", () => {
  const rows = compliance.normaliseDropCopyEnvelope(envelopes.ordersSnapshot);
  assert.equal(rows.length, 2);
  const row = rows[0];
  assert.equal(row.type, "Order");
  assert.equal(row.status, "Working");
  assert.equal(row.symbol, "PETR4");
  assert.equal(row.qty, 100);
  assert.equal(row.price, 30.5);
  assert.equal(row.clOrdId, "0B52A6F9D43E7C10");
  assert.equal(row.channel, "dropcopy.orders");
  assert.equal(row.seq, 0);
});

test("normaliseDropCopyEnvelope routes fill and cancel deltas by channel", () => {
  const [fill] = compliance.normaliseDropCopyEnvelope(envelopes.fillDelta);
  assert.equal(fill.type, "Fill");
  assert.equal(fill.status, "Filled");
  assert.equal(fill.qty, 100);
  assert.equal(fill.price, 30.5);
  assert.equal(fill.timestamp, "2026-07-16T17:00:00+00:00");

  const [cancel] = compliance.normaliseDropCopyEnvelope(envelopes.cancelDelta);
  assert.equal(cancel.type, "Canceled");
  assert.equal(cancel.status, "Cancelled");
  assert.equal(cancel.qty, 0);
  assert.equal(cancel.channel, "dropcopy.cancels");
});

test("normaliseDropCopyEnvelope rejects fabricated and malformed shapes", () => {
  assert.equal(compliance.normaliseDropCopyEnvelope({ type: "fill", payload: {} }), null);
  assert.equal(compliance.normaliseDropCopyEnvelope({ type: "snapshot", channel: "dropcopy.orders", data: {} }), null);
  assert.equal(compliance.normaliseDropCopyEnvelope({}), null);
  assert.equal(compliance.normaliseDropCopyEnvelope(null), null);
});

test("drop-copy reconnect is single, bounded, resnapshots, and stops cleanly", () => {
  class FakeWebSocket {
    static instances = [];
    constructor(url) {
      this.url = url;
      this.listeners = new Map();
      this.closeCalls = [];
      FakeWebSocket.instances.push(this);
    }
    addEventListener(type, fn) {
      if (!this.listeners.has(type)) this.listeners.set(type, []);
      this.listeners.get(type).push(fn);
    }
    emit(type, event = {}) {
      for (const fn of this.listeners.get(type) ?? []) fn(event);
    }
    sendJson(value) {
      this.emit("message", { data: JSON.stringify(value) });
    }
    close(code, reason) {
      this.closeCalls.push({ code, reason });
      this.emit("close");
    }
  }

  const originalWebSocket = globalThis.WebSocket;
  const originalSetTimeout = globalThis.setTimeout;
  const originalClearTimeout = globalThis.clearTimeout;
  const timers = [];
  globalThis.WebSocket = FakeWebSocket;
  globalThis.setTimeout = (fn, delay) => {
    const timer = { fn, delay, cancelled: false };
    timers.push(timer);
    return timer;
  };
  globalThis.clearTimeout = (timer) => { timer.cancelled = true; };

  try {
    state.clearComplianceFeed();
    state.setComplianceFeedPaused(false);
    const url = "wss://api.example/ws/dropcopy?access_token=token";
    const first = compliance.openDropCopyFeed(url);
    assert.equal(FakeWebSocket.instances.length, 1);
    assert.equal(state.getState().complianceConnection.status, "connecting");

    first.emit("open");
    assert.equal(state.getState().complianceConnection.status, "connected");
    assert.equal(elements.get("compliance-feed-connection").textContent, "Connected");

    first.sendJson(envelopes.ordersSnapshot);
    first.sendJson(envelopes.fillsSnapshot);
    first.sendJson(envelopes.cancelsSnapshot);
    first.sendJson(envelopes.fillDelta);
    assert.equal(state.getState().complianceFeed.entries.length, 3);
    assert.match(elements.get("compliance-feed-body").innerHTML, /Working/);
    assert.match(elements.get("compliance-feed-body").innerHTML, /Filled/);

    state.clearAll();
    assert.equal(state.getState().complianceFeed.entries.length, 3);
    assert.equal(state.getState().complianceConnection.status, "connected");

    first.emit("error");
    assert.equal(state.getState().complianceConnection.status, "error");
    assert.equal(elements.get("compliance-feed-connection").textContent, "Connection error");
    first.emit("close");
    first.emit("close");
    compliance.openDropCopyFeed(url);
    assert.equal(timers.filter((timer) => !timer.cancelled).length, 1);
    assert.equal(FakeWebSocket.instances.length, 1);
    assert.equal(timers[0].delay, 1_000);
    assert.equal(state.getState().complianceConnection.status, "reconnecting");

    timers[0].fn();
    const second = FakeWebSocket.instances[1];
    assert.ok(second);
    second.emit("open");
    second.sendJson({
      ...envelopes.ordersSnapshot,
      data: [envelopes.ordersSnapshot.data[1]],
    });
    second.sendJson(envelopes.fillsSnapshot);
    second.sendJson(envelopes.cancelsSnapshot);
    assert.equal(state.getState().complianceFeed.entries.length, 1);
    assert.equal(state.getState().complianceFeed.entries[0].clOrdId, "4F81C2D93A607BE5");

    compliance.closeDropCopyFeed();
    assert.equal(second.closeCalls.length, 1);
    assert.equal(state.getState().complianceConnection.status, "disconnected");
    assert.equal(elements.get("compliance-feed-connection").textContent, "Disconnected");
    assert.equal(timers.filter((timer) => !timer.cancelled).length, 1);
  } finally {
    compliance.closeDropCopyFeed();
    globalThis.WebSocket = originalWebSocket;
    globalThis.setTimeout = originalSetTimeout;
    globalThis.clearTimeout = originalClearTimeout;
  }
});

test("drop-copy reconnect backoff is exponential and capped", () => {
  assert.equal(compliance.dropCopyReconnectDelayMs(0), 1_000);
  assert.equal(compliance.dropCopyReconnectDelayMs(4), 16_000);
  assert.equal(compliance.dropCopyReconnectDelayMs(5), 30_000);
  assert.equal(compliance.dropCopyReconnectDelayMs(100), 30_000);
});

test("yesterdayBrt returns a yyyy-MM-dd string for the day before in UTC-3", () => {
  const got = compliance.yesterdayBrt();
  assert.match(got, /^\d{4}-\d{2}-\d{2}$/);
  // It must be a real, past date (≤ today's UTC date).
  const todayUtc = new Date().toISOString().slice(0, 10);
  assert.ok(got <= todayUtc, `expected ${got} <= ${todayUtc}`);
});
