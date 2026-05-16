// Q2.6 (#273). P2 regression: pnl.me must NOT be a static subscription
// — it should be subscribed on P&L view enter and unsubscribed on view
// leave so the per-fill fan-out doesn't run for traders parked on
// other views.
//
// The view-switch path in app.js calls worker.postMessage({ type:
// "setPnlSubscribed", value }). This test drives the worker module
// directly under a fake `self` + `WebSocket` to assert the resulting
// subscribe / unsubscribe frames are sent.
//
// Implementation note: worker.js resolves as CJS (no package.json with
// `"type": "module"`) and Node's CJS loader ignores `?bust=` query
// strings, so we can only import the worker module ONCE per process.
// All assertions are therefore folded into a single shared-setup file
// driving one worker instance through subscribe → unsubscribe →
// reconnect-replay → idempotency.

import { test } from "node:test";
import assert from "node:assert/strict";

const sent = [];
const posted = [];
let socket = null;

class FakeWS {
  static OPEN = 1;
  constructor() {
    this.readyState = 0;
    this.onopen = null; this.onmessage = null; this.onclose = null; this.onerror = null;
    socket = this;
  }
  send(s) { sent.push(JSON.parse(s)); }
  close() { this.readyState = 3; if (this.onclose) this.onclose({}); }
  open() { this.readyState = FakeWS.OPEN; if (this.onopen) this.onopen({}); }
}

const fakeSelf = {
  onmessage: null,
  postMessage: (m) => posted.push(m),
};

globalThis.self = fakeSelf;
globalThis.WebSocket = FakeWS;

// Load the worker module once and bind to fakeSelf.
await import("../js/worker.js");

const drive = (msg) => fakeSelf.onmessage({ data: msg });
const openWs = () => socket && socket.open();

// Bring the worker up so subsequent (un)subscribe frames have a live
// fake WS to ship through.
drive({ type: "start", backend: "http://x", token: "tok" });
openWs();

test("pnl.me is NOT in the static subscribe set on connect", () => {
  const subs = sent.filter(f => f.type === "subscribe");
  assert.ok(subs.length >= 1, "at least one initial subscribe frame");
  const initial = subs[0];
  assert.ok(initial.channels.includes("orders.me"));
  assert.ok(initial.channels.includes("executions.me"));
  assert.ok(initial.channels.includes("positions.me"));
  assert.ok(!initial.channels.includes("pnl.me"),
    "pnl.me must NOT be in the static channel set — it is subscribed dynamically on view enter");
});

test("setPnlSubscribed(true) sends subscribe pnl.me; setPnlSubscribed(false) sends unsubscribe", () => {
  // Navigate to the P&L view → subscribe.
  const before = sent.length;
  drive({ type: "setPnlSubscribed", value: true });
  const afterSub = sent.slice(before);
  const subFrame = afterSub.find(f => f.type === "subscribe" && f.channels.includes("pnl.me"));
  assert.ok(subFrame, "subscribe frame for pnl.me must be sent on view enter");

  // Navigate away → unsubscribe.
  const beforeUnsub = sent.length;
  drive({ type: "setPnlSubscribed", value: false });
  const afterUnsub = sent.slice(beforeUnsub);
  const unsubFrame = afterUnsub.find(f => f.type === "unsubscribe" && f.channels.includes("pnl.me"));
  assert.ok(unsubFrame, "unsubscribe frame for pnl.me must be sent on view leave");
});

test("setPnlSubscribed is idempotent — repeated calls with same value are no-ops", () => {
  // Worker is currently in "unsubscribed" state from the prior test.
  drive({ type: "setPnlSubscribed", value: true });   // flip true
  const after1 = sent.length;
  drive({ type: "setPnlSubscribed", value: true });   // no-op
  assert.equal(sent.length, after1, "duplicate subscribe must not re-send");

  drive({ type: "setPnlSubscribed", value: false });  // flip false
  const after2 = sent.length;
  drive({ type: "setPnlSubscribed", value: false });  // no-op
  assert.equal(sent.length, after2, "duplicate unsubscribe must not re-send");
});

test("pnl.me subscription is replayed on reconnect when the view is still open", () => {
  // Bring wantPnl back to true so the reconnect replay has something
  // to do.
  drive({ type: "setPnlSubscribed", value: true });

  // Simulate a reconnect: re-fire onopen on the existing fake socket.
  // The worker holds the wantPnl flag across reconnects and replays
  // it from onopen alongside the static CHANNELS.
  const before = sent.length;
  openWs();
  const replay = sent.slice(before);
  assert.ok(
    replay.some(f => f.type === "subscribe" && f.channels.includes("pnl.me")),
    "pnl.me must be re-subscribed on reconnect when wantPnl is true",
  );
});
