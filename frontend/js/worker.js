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

// Q2.6 (#273). pnl.me joins the static per-account channel set so the
// P&L panel always sees a snapshot + every fill-driven delta without
// the caller having to explicitly subscribe. Same shape as the other
// account-scoped channels (snapshot at seq=0, deltas thereafter); the
// delta payload carries the full re-projected PnlTodayDto.
const CHANNELS = ["orders.me", "executions.me", "positions.me", "pnl.me"];

// Q1.6 (#258). Wanted set of public per-symbol channels that should be
// subscribed any time the WS is connected. Drives diff (un)subscribes
// when the main thread calls setPublicChannels, and is replayed on
// (re)connect after the static CHANNELS go out. Held across reconnects
// so a flap doesn't lose the watchlist subscriptions.
const wantedPublic = new Set();

function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

function post(message) { self.postMessage(message); }

function scheduleReconnect() {
  if (stopped) return;
  const delay = Math.min(15_000, 500 * Math.pow(2, attempt++));
  reconnectTimer = setTimeout(connect, delay);
  post({ type: "status", value: "connecting" });
  // Surface the next attempt timestamp so the UI can show a countdown.
  post({ type: "reconnect.scheduled", nextAt: Date.now() + delay });
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
    post({ type: "reconnect.scheduled", nextAt: null });
    send({ type: "subscribe", channels: CHANNELS });
    // Q1.6 (#258). Replay the public-channel set the main thread had
    // configured so the watchlist phase badges + auction panel keep
    // working across reconnects without a re-set from app.js.
    if (wantedPublic.size > 0) {
      send({ type: "subscribe", channels: [...wantedPublic] });
    }
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
    case "pnl.me":
      // Q2.6 (#273). Both snapshot and delta payloads are the full
      // PnlTodayDto — the main thread reducer treats them identically,
      // so we forward a single event type per direction.
      post({ type: frame.type === "snapshot" ? "pnl.snapshot" : "pnl.delta", data: frame.data });
      break;
    default:
      // Q1.6 (#258). Public per-symbol channels — phases.${symbol} and
      // auction.${symbol}. Snapshot and delta share the same payload
      // shape (the state setters merge both); collapse to a single
      // event type per channel kind to keep the main-thread router
      // simple. Anything else is an unknown channel — drop silently.
      if (typeof frame.channel !== "string") return;
      if (frame.channel.startsWith("phases.")) {
        post({ type: "phases.frame", data: frame.data });
      } else if (frame.channel.startsWith("auction.")) {
        post({ type: "auction.frame", data: frame.data });
      }
      break;
  }
}

// Q1.6 (#258). Diff the wanted public-channel set against the new one
// and send subscribe/unsubscribe deltas. Idempotent — repeated calls
// with the same set are no-ops. Gracefully handles the disconnected
// state: the wanted set is recorded and replayed on next onopen.
function setPublicChannels(channels) {
  const next = new Set();
  for (const c of channels || []) {
    if (typeof c === "string" && c.length > 0) next.add(c);
  }
  const toAdd = [];
  const toRemove = [];
  for (const c of next)         if (!wantedPublic.has(c)) toAdd.push(c);
  for (const c of wantedPublic) if (!next.has(c))         toRemove.push(c);
  wantedPublic.clear();
  for (const c of next) wantedPublic.add(c);
  if (toAdd.length    > 0) send({ type: "subscribe",   channels: toAdd });
  if (toRemove.length > 0) send({ type: "unsubscribe", channels: toRemove });
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
      wantedPublic.clear();
      break;
    case "setPublicChannels":
      setPublicChannels(msg.channels);
      break;
  }
};
