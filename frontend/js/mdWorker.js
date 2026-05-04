// Web Worker: owns the SECOND WebSocket — the one that talks directly
// to B3MarketDataPlatform for live trade prints + info snapshots.
//
// Decoupled from worker.js (which owns the trading-host WS) because:
//   - different protocol (binary little-endian vs JSON)
//   - different lifecycle (no auth; reconnect-on-drop with no replay)
//   - different failure surface (server may NOT_READY at startup)
//
// Inputs (main thread → us):
//   { type: "start", url, symbols: [string], flags?: number }
//   { type: "stop" }
//   { type: "setSymbols", symbols: [string] }   // diff applied
//   { type: "setFlags", flags: number }         // re-subscribe with new bitmask
//
// Outputs (us → main thread):
//   { type: "md.status", value: "connecting"|"connected"|"disconnected"|"not_ready" }
//   { type: "md.trade",    symbol, price, qty, tradeId }
//   { type: "md.info",     symbol, fields }     // raw InfoSnapshot fields
//   { type: "md.bust",     symbol, tradeId }
//   { type: "md.book.snapshot",     symbol }    // marker; levels follow
//   { type: "md.book.cleared",      symbol, side }
//   { type: "md.level.snapshot",    symbol, bids: [{price,qty,count}], asks: [...] }
//   { type: "md.level.update",      symbol, side, price, qty, count }
//   { type: "md.level.deleted",     symbol, side, price }
//   { type: "md.candle.snapshot",   symbol, resolution, candles, isFirst, isLast }
//   { type: "md.candle.update",     symbol, resolution, candle }
//   { type: "md.subError", symbol, errorName }
//   { type: "md.clear" }                        // posted on (re)connect; reset all derived caches
//   { type: "md.error",    message }

import { buildSubscribe, buildUnsubscribe, parseFrames, FLAGS } from './mdProtocol.js';

let ws = null;
let url = null;
let stopped = false;
let attempt = 0;
let reconnectTimer = null;
let serverReady = false;
let subscribeFlags = FLAGS.TRADES | FLAGS.INFO;

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
      // Defensive: drop server-misdelivered subscriptions for symbols
      // we never asked for. Keeps the local map honest and prevents
      // forwarding frames the trader UI didn't request.
      if (!subscriptions.has(symbol)) return;
      const id = f.securityId;
      subscriptions.set(symbol, id);
      securityIdToSymbol.set(id, symbol);
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
      const symbol = securityIdToSymbol.get(f.securityId);
      if (!symbol) return; // arrived before SubscribeOk landed; drop
      post({ type: 'md.trade', symbol, price: f.price, qty: f.qty, tradeId: f.tradeId });
      return;
    }

    case 'TradeBust': {
      const symbol = securityIdToSymbol.get(f.securityId);
      if (!symbol) return;
      post({ type: 'md.bust', symbol, tradeId: f.tradeId });
      return;
    }

    case 'InfoSnapshot': {
      const symbol = securityIdToSymbol.get(f.securityId);
      if (!symbol) return;
      post({ type: 'md.info', symbol, fields: f.fields });
      return;
    }

    case 'BookSnapshot': {
      const symbol = securityIdToSymbol.get(f.securityId);
      if (!symbol) return;
      post({ type: 'md.book.snapshot', symbol });
      return;
    }

    case 'BookCleared': {
      const symbol = securityIdToSymbol.get(f.securityId);
      if (!symbol) return;
      post({ type: 'md.book.cleared', symbol, side: f.side });
      return;
    }

    case 'LevelSnapshot': {
      const symbol = securityIdToSymbol.get(f.securityId);
      if (!symbol) return;
      post({ type: 'md.level.snapshot', symbol, bids: f.bids, asks: f.asks });
      return;
    }

    case 'LevelUpdate': {
      const symbol = securityIdToSymbol.get(f.securityId);
      if (!symbol) return;
      post({
        type: 'md.level.update',
        symbol,
        side: f.side,
        price: f.price,
        qty: f.qty,
        count: f.count,
      });
      return;
    }

    case 'LevelDeleted': {
      const symbol = securityIdToSymbol.get(f.securityId);
      if (!symbol) return;
      post({ type: 'md.level.deleted', symbol, side: f.side, price: f.price });
      return;
    }

    case 'CandleSnapshot': {
      const symbol = securityIdToSymbol.get(f.securityId);
      if (!symbol) return;
      post({
        type: 'md.candle.snapshot',
        symbol,
        resolution: f.resolution,
        candles: f.candles,
        isFirst: f.isFirst,
        isLast: f.isLast,
      });
      return;
    }

    case 'CandleUpdate': {
      const symbol = securityIdToSymbol.get(f.securityId);
      if (!symbol) return;
      post({
        type: 'md.candle.update',
        symbol,
        resolution: f.resolution,
        candle: f.candle,
      });
      return;
    }
  }
}

function flushSubscribes() {
  for (const [symbol, id] of subscriptions) {
    if (id !== null) continue; // already resolved
    try { safeSend(buildSubscribe(symbol, subscribeFlags)); }
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
    if (id !== null) securityIdToSymbol.delete(id);
    post({ type: 'md.removed', symbol });
  }
  // Add new ones.
  for (const symbol of wanted) {
    if (subscriptions.has(symbol)) continue;
    subscriptions.set(symbol, null);
    if (serverReady) {
      try { safeSend(buildSubscribe(symbol, subscribeFlags)); }
      catch (err) {
        subscriptions.delete(symbol);
        post({ type: 'md.subError', symbol, errorName: String(err.message || err) });
      }
    }
  }
}

function resetSubscriptionsForFlagsChange() {
  // Unsubscribe all currently-resolved symbols and re-subscribe with the
  // new flags. The server resolves a fresh securityId per (symbol, flags)
  // session, so we drop the reverse index and let SubscribeOk repopulate.
  // Frames in flight under the previous flags are intentionally lost
  // during the gap. We do NOT post `md.clear` here — that semantic is
  // reserved for the full reconnect path so the trade-tape / lastPrice
  // cache survives a flag flip (e.g. enabling MBP for a DOB panel).
  // Per-side book/level caches reset naturally as the server resends
  // snapshots after the new SubscribeOk.
  for (const [, id] of subscriptions) {
    if (id !== null) safeSend(buildUnsubscribe(id));
  }
  for (const sym of subscriptions.keys()) subscriptions.set(sym, null);
  securityIdToSymbol.clear();
  if (serverReady) flushSubscribes();
}

self.onmessage = (ev) => {
  const msg = ev.data || {};
  switch (msg.type) {
    case 'start':
      url = msg.url;
      stopped = false;
      attempt = 0;
      if (typeof msg.flags === 'number' && msg.flags > 0) {
        subscribeFlags = msg.flags;
      }
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

    case 'setFlags':
      if (typeof msg.flags === 'number' && msg.flags > 0 && msg.flags !== subscribeFlags) {
        subscribeFlags = msg.flags;
        resetSubscriptionsForFlagsChange();
      }
      break;
  }
};
