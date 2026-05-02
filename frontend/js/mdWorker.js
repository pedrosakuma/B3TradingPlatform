// Web Worker: owns the SECOND WebSocket — the one that talks directly
// to B3MarketDataPlatform for live trade prints + info snapshots.
//
// Decoupled from worker.js (which owns the trading-host WS) because:
//   - different protocol (binary little-endian vs JSON)
//   - different lifecycle (no auth; reconnect-on-drop with no replay)
//   - different failure surface (server may NOT_READY at startup)
//
// Inputs (main thread → us):
//   { type: "start", url, symbols: [string] }
//   { type: "stop" }
//   { type: "setSymbols", symbols: [string] }   // diff applied
//
// Outputs (us → main thread):
//   { type: "md.status", value: "connecting"|"connected"|"disconnected"|"not_ready" }
//   { type: "md.trade",    symbol, price, qty, tradeId }
//   { type: "md.info",     symbol, fields }     // raw InfoSnapshot fields
//   { type: "md.bust",     symbol, tradeId }
//   { type: "md.subError", symbol, errorName }
//   { type: "md.clear" }                        // dropped; main thread should reset cache
//   { type: "md.error",    message }

import { buildSubscribe, buildUnsubscribe, parseFrames } from './mdProtocol.js';

let ws = null;
let url = null;
let stopped = false;
let attempt = 0;
let reconnectTimer = null;
let serverReady = false;

// Subscribed symbols. Keys are normalized (UPPERCASE, trimmed) so set
// arithmetic is straightforward. Value is the resolved securityId once
// the server replies SubscribeOk; null while pending.
const subscriptions = new Map();
// Reverse index for events that arrive keyed by securityId only.
const securityIdToSymbol = new Map();

const MAX_RECONNECT_DELAY = 15_000;

function post(message) { self.postMessage(message); }

function safeSend(buf) {
  if (ws && ws.readyState === WebSocket.OPEN) {
    try { ws.send(buf); } catch (err) { post({ type: 'md.error', message: String(err) }); }
  }
}

function scheduleReconnect() {
  if (stopped) return;
  const delay = Math.min(MAX_RECONNECT_DELAY, 500 * Math.pow(2, attempt++));
  reconnectTimer = setTimeout(connect, delay);
  post({ type: 'md.status', value: 'connecting' });
}

function connect() {
  if (stopped || !url) return;
  reconnectTimer = null;
  serverReady = false;

  // Drop resolved securityIds — the server reassigns them per session.
  // Keep the symbol set so we re-subscribe on (re)open.
  securityIdToSymbol.clear();
  for (const sym of subscriptions.keys()) subscriptions.set(sym, null);
  post({ type: 'md.clear' });

  let socket;
  try { socket = new WebSocket(url); }
  catch (err) {
    post({ type: 'md.error', message: String(err) });
    scheduleReconnect();
    return;
  }
  socket.binaryType = 'arraybuffer';
  ws = socket;

  socket.onopen = () => {
    attempt = 0;
    post({ type: 'md.status', value: 'connected' });
    // Subscriptions wait for ServerStatus(ready=1). The server emits
    // one immediately on connect; resending now would be racy.
  };

  socket.onmessage = (ev) => {
    if (!(ev.data instanceof ArrayBuffer)) return; // text frames not used
    let frames;
    try { frames = parseFrames(ev.data); }
    catch (err) { post({ type: 'md.error', message: String(err) }); return; }
    for (const f of frames) handleFrame(f);
  };

  socket.onclose = () => {
    ws = null;
    serverReady = false;
    post({ type: 'md.status', value: 'disconnected' });
    scheduleReconnect();
  };

  socket.onerror = () => {
    // onclose runs after onerror; let it drive the reconnect path.
  };
}

function handleFrame(f) {
  switch (f.type) {
    case 'ServerStatus':
      serverReady = f.ready;
      if (f.ready) {
        post({ type: 'md.status', value: 'connected' });
        flushSubscribes();
      } else {
        post({ type: 'md.status', value: 'not_ready' });
      }
      return;

    case 'SubscribeOk': {
      const symbol = f.symbol.toUpperCase();
      const id = f.securityId;
      subscriptions.set(symbol, id);
      securityIdToSymbol.set(id.toString(), symbol);
      return;
    }

    case 'SubscribeError': {
      const symbol = f.symbol.toUpperCase();
      // Drop from the active set so we don't keep retrying every
      // reconnect; UI is informed and the user can re-add.
      subscriptions.delete(symbol);
      post({ type: 'md.subError', symbol, errorName: f.errorName });
      return;
    }

    case 'Trade': {
      const symbol = securityIdToSymbol.get(f.securityId.toString());
      if (!symbol) return; // arrived before SubscribeOk landed; drop
      post({ type: 'md.trade', symbol, price: f.price, qty: f.qty, tradeId: f.tradeId });
      return;
    }

    case 'TradeBust': {
      const symbol = securityIdToSymbol.get(f.securityId.toString());
      if (!symbol) return;
      post({ type: 'md.bust', symbol, tradeId: f.tradeId });
      return;
    }

    case 'InfoSnapshot': {
      const symbol = securityIdToSymbol.get(f.securityId.toString());
      if (!symbol) return;
      post({ type: 'md.info', symbol, fields: f.fields });
      return;
    }
  }
}

function flushSubscribes() {
  for (const [symbol, id] of subscriptions) {
    if (id !== null) continue; // already resolved
    try { safeSend(buildSubscribe(symbol)); }
    catch (err) {
      subscriptions.delete(symbol);
      post({ type: 'md.subError', symbol, errorName: String(err.message || err) });
    }
  }
}

function applySymbolDiff(next) {
  const wanted = new Set(next.map(s => s.toUpperCase().trim()).filter(Boolean));
  // Remove dropped subscriptions.
  for (const [symbol, id] of [...subscriptions]) {
    if (wanted.has(symbol)) continue;
    if (id !== null) safeSend(buildUnsubscribe(id));
    subscriptions.delete(symbol);
    securityIdToSymbol.delete(id?.toString() ?? '');
    post({ type: 'md.removed', symbol });
  }
  // Add new ones.
  for (const symbol of wanted) {
    if (subscriptions.has(symbol)) continue;
    subscriptions.set(symbol, null);
    if (serverReady) {
      try { safeSend(buildSubscribe(symbol)); }
      catch (err) {
        subscriptions.delete(symbol);
        post({ type: 'md.subError', symbol, errorName: String(err.message || err) });
      }
    }
  }
}

self.onmessage = (ev) => {
  const msg = ev.data || {};
  switch (msg.type) {
    case 'start':
      url = msg.url;
      stopped = false;
      attempt = 0;
      subscriptions.clear();
      securityIdToSymbol.clear();
      for (const s of msg.symbols || []) {
        const sym = String(s).toUpperCase().trim();
        if (sym) subscriptions.set(sym, null);
      }
      connect();
      break;

    case 'stop':
      stopped = true;
      if (reconnectTimer) clearTimeout(reconnectTimer);
      reconnectTimer = null;
      if (ws) {
        try { ws.close(1000, 'client logout'); } catch { /* swallow */ }
      }
      ws = null;
      subscriptions.clear();
      securityIdToSymbol.clear();
      break;

    case 'setSymbols':
      applySymbolDiff(msg.symbols || []);
      break;
  }
};
