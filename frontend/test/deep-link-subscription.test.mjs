import { test } from "node:test";
import assert from "node:assert/strict";

const sent = [];
let socket;
class FakeWebSocket {
  static OPEN = 1;
  constructor() { socket = this; this.readyState = 0; }
  send(value) { sent.push(JSON.parse(value)); }
  open() { this.readyState = FakeWebSocket.OPEN; this.onopen?.(); }
}
globalThis.WebSocket = FakeWebSocket;
globalThis.self = { postMessage() {}, onmessage: null };
await import("../js/worker.js");

test("deep-link dynamic subscriptions are present on the first websocket open", () => {
  self.onmessage({
    data: {
      type: "start",
      backend: "http://host",
      token: "tok",
      pnlSubscribed: true,
      algoSubscribed: true,
    },
  });
  socket.open();
  assert.ok(sent.some((frame) => frame.channels?.includes("pnl.me")));
  assert.ok(sent.some((frame) => frame.channels?.includes("algo.me")));
});
