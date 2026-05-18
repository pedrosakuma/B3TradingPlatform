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
//   3. Audit-form opts → /admin/audit URL building uses the
//      documented query-parameter shape (omitted fields are not
//      sent; ISO timestamps for since/until).
//   4. CVM download URL builds correctly per model + date and
//      that the date-string filename derivation matches the issue
//      spec (cvm_<model>_<yyyymmdd>.xml).

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({
  ids: {
    "compliance-view":              { tag: "section", hidden: true },
    "compliance-feed-body":         { tag: "tbody" },
    "compliance-feed-pause":        { tag: "button" },
    "compliance-feed-clear":        { tag: "button" },
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

test("tabsForRole gates the nav tabs per JWT role", () => {
  assert.deepEqual(compliance.tabsForRole("user"),
    ["trader", "bot-credentials"]);
  assert.deepEqual(compliance.tabsForRole("admin"),
    ["trader", "admin", "bot-credentials", "compliance"]);
  assert.deepEqual(compliance.tabsForRole("compliance"),
    ["compliance"]);
  // Unknown role defaults to plain-user surface (least privilege).
  assert.deepEqual(compliance.tabsForRole(undefined),
    ["trader", "bot-credentials"]);
});

test("defaultViewForRole lands compliance on its own console", () => {
  assert.equal(compliance.defaultViewForRole("compliance"), "compliance");
  assert.equal(compliance.defaultViewForRole("admin"), "trader");
  assert.equal(compliance.defaultViewForRole("user"), "trader");
});

test("drop-copy feed buffer caps at COMPLIANCE_FEED_CAP and keeps newest", () => {
  state.clearComplianceFeed();
  state.setComplianceFeedPaused(false);
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
  state.clearComplianceFeed();
  state.setComplianceFeedPaused(true);
  state.appendComplianceFeed({ seq: 1, type: "fill" });
  state.appendComplianceFeed({ seq: 2, type: "fill" });
  assert.equal(state.getState().complianceFeed.entries.length, 0);
  state.setComplianceFeedPaused(false);
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
  assert.equal(u.pathname, "/admin/audit");
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

test("downloadCvmReport builds /reports/cvm/{model}/{date} and saves cvm_<m>_<yyyymmdd>", async () => {
  fetchCalls.length = 0;
  fetchResponse = {
    ok: true, status: 200,
    headers: { get: () => null },
    text: async () => "",
    blob: async () => ({ size: 1, type: "application/xml" }),
  };
  const { filename } = await protocol.downloadCvmReport("http://api", "tok", 35, "2025-01-15");
  fetchResponse = null;
  assert.equal(fetchCalls.length, 1);
  assert.equal(fetchCalls[0].url, "http://api/reports/cvm/35/2025-01-15");
  assert.equal(filename, "cvm_35_20250115.xml");

  // Model 505 uses the same URL shape.
  fetchCalls.length = 0;
  fetchResponse = {
    ok: true, status: 200,
    headers: { get: () => null },
    text: async () => "",
    blob: async () => ({ size: 1, type: "application/xml" }),
  };
  const r2 = await protocol.downloadCvmReport("http://api", "tok", 505, "2025-02-03");
  fetchResponse = null;
  assert.equal(fetchCalls[0].url, "http://api/reports/cvm/505/2025-02-03");
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

test("normaliseDropCopyMessage flattens the WS frame into a row entry", () => {
  const row = compliance.normaliseDropCopyMessage({
    type: "fill",
    payload: {
      timestamp: "2025-01-15T12:00:00Z",
      user: "alice", symbol: "PETR4", side: "Buy",
      lastQty: 100, lastPx: 30.5, clOrdId: "42",
    },
  });
  assert.equal(row.type, "fill");
  assert.equal(row.user, "alice");
  assert.equal(row.symbol, "PETR4");
  assert.equal(row.qty, 100);
  assert.equal(row.price, 30.5);
  assert.equal(row.clOrdId, "42");
});

test("normaliseDropCopyMessage drops heartbeats / snapshot boundaries", () => {
  assert.equal(compliance.normaliseDropCopyMessage({ type: "heartbeat" }), null);
  assert.equal(compliance.normaliseDropCopyMessage({ type: "snapshot.start" }), null);
  assert.equal(compliance.normaliseDropCopyMessage({}), null);
  assert.equal(compliance.normaliseDropCopyMessage(null), null);
});

test("yesterdayBrt returns a yyyy-MM-dd string for the day before in UTC-3", () => {
  const got = compliance.yesterdayBrt();
  assert.match(got, /^\d{4}-\d{2}-\d{2}$/);
  // It must be a real, past date (≤ today's UTC date).
  const todayUtc = new Date().toISOString().slice(0, 10);
  assert.ok(got <= todayUtc, `expected ${got} <= ${todayUtc}`);
});
