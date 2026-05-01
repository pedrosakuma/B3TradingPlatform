// Web Worker: owns the WebSocket, drives reconnect, applies snapshots
// and deltas, and forwards normalized events back to the main thread.
//
// Protocol (matches docs/WEBSOCKET-PROTOCOL.md):
//   inbound (server → us):
//     { type: "snapshot", channel, seq: 0, data: [...] }
//     { type: "delta",    channel, seq,     data: <row> }
//     { type: "error",    channel?, code, message }
//   outbound (us → server):
//     { type: "subscribe",   channels: [...] }
//     { type: "unsubscribe", channels: [...] }
//
// Reconnect: exponential backoff capped at 15s. v1 has no replay buffer,
// so on every (re)connect we drop our caches and let the snapshots refill
// them. The main thread mirrors that via `clear` messages.

let ws = null;
let backendUrl = null;
let token = null;
let attempt = 0;
let reconnectTimer = null;
let stopped = false;

const CHANNELS = ["orders.me", "executions.me", "positions.me"];

function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

function post(message) { self.postMessage(message); }

function scheduleReconnect() {
  if (stopped) return;
  const delay = Math.min(15_000, 500 * Math.pow(2, attempt++));
  reconnectTimer = setTimeout(connect, delay);
  post({ type: "status", value: "connecting" });
}

function wsUrl() {
  // backendUrl is HTTP(S); convert scheme. Append the JWT as
  // ?access_token= because browsers can't set Authorization on a WS
  // handshake. WEBSOCKET-PROTOCOL.md flags this as a log-hygiene risk
  // — operators must redact `access_token=` from access logs.
  const u = new URL("/ws", backendUrl);
  u.protocol = u.protocol === "https:" ? "wss:" : "ws:";
  u.searchParams.set("access_token", token);
  return u.toString();
}

function connect() {
  if (stopped) return;
  reconnectTimer = null;

  // Drop any stale cached state on each fresh connect.
  post({ type: "clear" });

  let socket;
  try { socket = new WebSocket(wsUrl()); }
  catch (err) {
    post({ type: "error", message: String(err) });
    scheduleReconnect();
    return;
  }
  ws = socket;

  socket.onopen = () => {
    attempt = 0;
    post({ type: "status", value: "connected" });
    send({ type: "subscribe", channels: CHANNELS });
  };

  socket.onmessage = (ev) => {
    let frame;
    try { frame = JSON.parse(ev.data); }
    catch { return; }
    handleFrame(frame);
  };

  socket.onclose = () => {
    ws = null;
    post({ type: "status", value: "disconnected" });
    scheduleReconnect();
  };

  socket.onerror = () => {
    // onclose runs after onerror; let it drive the reconnect.
  };
}

function handleFrame(frame) {
  if (frame.type === "error") {
    post({ type: "error", code: frame.code, message: frame.message, channel: frame.channel });
    return;
  }
  if (frame.type !== "snapshot" && frame.type !== "delta") return;

  switch (frame.channel) {
    case "orders.me":
      post({ type: frame.type === "snapshot" ? "orders.snapshot" : "orders.delta", data: frame.data });
      break;
    case "positions.me":
      post({ type: frame.type === "snapshot" ? "positions.snapshot" : "positions.delta", data: frame.data });
      break;
    case "executions.me":
      post({ type: frame.type === "snapshot" ? "executions.snapshot" : "executions.delta", data: frame.data });
      break;
  }
}

self.onmessage = (ev) => {
  const msg = ev.data || {};
  switch (msg.type) {
    case "start":
      backendUrl = msg.backend;
      token = msg.token;
      stopped = false;
      attempt = 0;
      connect();
      break;
    case "stop":
      stopped = true;
      if (reconnectTimer) clearTimeout(reconnectTimer);
      reconnectTimer = null;
      if (ws) {
        try { ws.close(1000, "client logout"); } catch { /* swallow */ }
      }
      ws = null;
      break;
  }
};
