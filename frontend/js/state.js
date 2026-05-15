// Main-thread mirror of the worker's authoritative cache. The worker
// holds the source-of-truth Map and posts diff/replace messages; this
// module just stores what arrives and notifies subscribers.

// Q1.4 (#256). Terminal order statuses mirror the backend
// `OrderStatus` enum. NOTE: `Expired` is intentionally absent —
// the GTD-expiry pipeline (#255) emits an `ExecKind.Expired`
// execution event but the order itself terminalises as
// `Cancelled` (the GTD scheduler routes through the cancel
// pipeline). The `Expired` value belongs to the executions log
// only.
const TERMINAL_ORDER_STATUSES = new Set(["Filled", "Cancelled", "Rejected", "Replaced"]);

// Q1.4 (#256). Mirrors of the backend OrderType / TimeInForce enums
// expanded by Q1.1 (#253). The ticket UI exposes every value listed
// here; the helpers below drive the conditional StopPrice + GTD inputs
// and the client-side validation rules.
export const ORDER_TYPES = ["Limit", "Market", "StopLoss", "StopLimit", "MarketWithLeftover"];
export const TIME_IN_FORCES = ["Day", "IOC", "FOK", "GTC", "GTD", "AtClose", "GoodForAuction"];

export function isStopOrderType(type) {
  return type === "StopLoss" || type === "StopLimit";
}

export function isGtdTif(tif) {
  return tif === "GTD";
}

// Type chip abbreviations used by the working-orders table.
export const ORDER_TYPE_CHIP = {
  Limit:               { label: "LIM",  cls: "chip-lim"  },
  Market:              { label: "MKT",  cls: "chip-mkt"  },
  StopLoss:            { label: "STP",  cls: "chip-stp"  },
  StopLimit:           { label: "STPL", cls: "chip-stpl" },
  MarketWithLeftover:  { label: "MWL",  cls: "chip-mwl"  },
};

const listeners = new Set();
const state = {
  orders: new Map(),       // ClOrdID -> OrderDto
  positions: new Map(),    // Symbol  -> PositionDto
  executions: [],          // bounded ring of ExecutionDto
  status: "disconnected",  // disconnected | connecting | connected
  user: null,              // { username, expiresAt, token, backend, firm, role }
  marketData: new Map(),   // Symbol -> { lastPrice, lastQty, lastTradeId, updatedAt, info }
  marketDataStatus: "disconnected", // disconnected | connecting | connected | not_ready
  // Stale-data tracking (T2 of the trader-ui ergonomics review). Stamps
  // the last time we received any data over the trader WS / MD WS.
  // Drives the "stale" overlay that flips on whenever the corresponding
  // WS isn't `connected`, so a trader can't act on rows that look
  // authoritative but are actually frozen during a flap.
  lastWsActivity: null,    // ms epoch | null
  lastMdActivity: null,    // ms epoch | null
  watchlist: [],           // [string] symbols (UPPERCASE)
  // Depth-of-Book slice (T2). One book entry per symbol; sides are
  // Map<priceKey, { qty, count }>. `ready` flips false on book.snapshot
  // marker and true on the following level.snapshot — incremental
  // updates that arrive before a snapshot are dropped to avoid
  // displaying a partial book. Sorting (top-N for render) happens
  // render-side, not in state.
  book: new Map(),         // Symbol -> { bids: Map, asks: Map, ready: bool, updatedAt: number }
  // Candle slice (T3). Server may multiplex multiple resolutions for
  // the same symbol on a single subscription, so we cache them all
  // (Map<symbol, Map<resolutionSec, {bars, ready, updatedAt}>>) and
  // let the UI filter at render time. `ready` flips true only when
  // a snapshot's CANDLE_FLAGS.LAST frame arrives — partial history
  // with no FIRST stays not-ready to avoid presenting a truncated
  // chart as complete.
  candles: new Map(),      // Symbol -> Map<resolutionSec, { bars: [...], ready: bool, updatedAt: number }>
  chartResolution: 60,     // seconds (60=1m, 300=5m, 900=15m)
  // Trade tape slice (T4). Per-symbol ring buffer of recent trades
  // with side inferred from the previous trade's price (uptick=buy,
  // downtick=sell, flat=unknown — TRADE wire frame doesn't carry an
  // aggressor). When `tapeShowAll` is true the render path flattens
  // every symbol and re-sorts by receivedAt; otherwise it scopes to
  // `selectedSymbol`.
  tape: new Map(),         // Symbol -> [{tradeId, price, qty, side, receivedAt, busted}]
  tapeShowAll: true,
  // Single symbol selection shared by DOB / chart / tape (the three
  // panels used to carry their own *Symbol slice; that drift made
  // it easy to look at the wrong instrument across panels). Auto-
  // picks watchlist[0] when null and a watchlist exists.
  selectedSymbol: null,
  // UX-only slices added in the operability pass.
  submitInflight: null,    // { startedAt: ms } | null — true while POST /orders is awaiting response
  wsReconnect: null,       // { nextAt: ms } | null — when worker has scheduled the next attempt
  firmsHealth: null,       // { mode, firms, fetchedAt } | null — admin-only poll of /admin/firms
  // Public, unauthenticated mirror of /health.exchange. Polled for every
  // logged-in user (not just admin) so the gateway badge can stop lying
  // when the FIXP session goes Suspended/Disconnected mid-trading.
  // Shape: { mode, readyForOrders, firmCount, firms?: [{firmId,state,reconnecting,sessionVerId}], fetchedAt } | null
  // firms[] is absent in Mock/Stub/Unavailable hosts; the badge then
  // hides itself rather than guessing.
  gatewayHealth: null,
  killStatus: null,        // { endClients: [], firms: [], fetchedAt } | null — admin-only
  haltStatus: null,        // { symbols: [], fetchedAt } | null — admin-only
  eodReport: null,         // { ranAt, report } | null — last EOD response in this session
  currentView: "trader",   // "trader" | "admin" | "bot-credentials" — which view is mounted
  // Blotter UX (section 3 of #30).
  blotterFilter: { text: "", status: "" }, // { text: substring, status: "" | <OrderStatus> }
  // Per-ClOrdID monotonic arrival sequence. Newly-seen orders get the
  // next number; updates keep the original. Drives the default
  // newest-first sort of the working orders blotter, robust to the
  // order in which the worker delivers snapshot rows.
  orderSeq: new Map(),                     // ClOrdID -> sequence number
  orderSeqCounter: 0,
  // Pagination of the blotter (1-based). Page resets to 1 whenever the
  // filter changes; the renderer re-clamps if the visible page falls
  // off the end (e.g. orders disappear after a snapshot replace).
  blotterPage: 1,
  ordersHighlight: new Map(),              // ClOrdID -> Date.now() of last delta
  selectedClOrdId: null,                   // currently selected blotter row (for keyboard cancel)
  pendingFatFinger: null,                  // { payload, key, setAt } — set when a submit needs override
  // Per-ClOrdID set of cancels currently in flight (DELETE issued, no
  // server ack yet). Used to disable the row's Cancel button so the
  // trader gets immediate visual feedback and can't fire repeat DELETEs.
  inflightCancels: new Set(),
  // Slice 5 of #122. Per-ClOrdID set of modifies currently in flight
  // (PUT /orders issued, no server ack yet). Drives the "Modifying…"
  // state on the row's Modify button so a slow server can't yield two
  // PUTs racing the venue.
  inflightModifies: new Set(),
  // Wall-clock when the active selectedSymbol was set. The DOB renderer
  // uses this to decide when to upgrade the "awaiting book snapshot…"
  // placeholder to a louder "no book — check MD settings ⚙" warning
  // (after ~10s without a snapshot).
  selectedSymbolSetAt: 0,
  // Q1.6 (#258). Per-symbol auction-phase state from the public
  // `phases.${symbol}` WS channel. Populated for every watchlist
  // symbol via auto-subscribe; absent until the first snapshot lands
  // (treated as "Unknown" by readers).
  phaseBySymbol: new Map(),     // Symbol -> phase string ("OpeningCall" | "Open" | ...)
  phaseAtBySymbol: new Map(),   // Symbol -> ISO timestamp of last transition (or null)
  // Q1.6 (#258). Per-symbol auction-state cache from the public
  // `auction.${symbol}` channel. Only populated while the auction
  // panel is open (cost control on WS fan-out). Shape:
  //   { top, indicativeMatchQty, imbalance, imbalanceSide, at,
  //     prevTop, lastPrints: [{kind, price, qty, at}, ...] }
  // `prevTop` retains the previous top so the renderer can draw the
  // up/down trend arrow without a separate per-renderer cache.
  // `lastPrints` is bounded — see AUCTION_PRINT_HISTORY.
  auctionBySymbol: new Map(),
  // Q1.6 (#258). Symbol the auction panel is currently rendering, or
  // null when the panel is collapsed. Used by the WS subscription
  // manager to decide whether to (un)subscribe `auction.${symbol}`.
  auctionPanelSymbol: null,
  // Q1.4 (#256). Effective risk-policy values fetched from
  // `GET /policy/risk` on session start. `null` until the first
  // successful fetch lands; readers fall back to safe client-side
  // defaults (e.g. 30-day GTD cap) so a slow/failed fetch never
  // blocks the ticket. Shape: `{ maxGtdHorizonDays: number }`.
  riskPolicy: null,
};

const EXECUTIONS_CAPACITY = 500;

export function subscribe(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

// Stale-data tracking (T2). Tapped into notify() so any slice update
// covered by WS_NOTIFY_SLICES / MD_NOTIFY_SLICES implicitly stamps
// the last-activity timestamp — keeps the freshness signal tightly
// coupled to the actual data flow without a separate instrumentation
// surface every applyX has to remember to call.
const WS_NOTIFY_SLICES = new Set(["orders", "positions", "executions"]);
const MD_NOTIFY_SLICES = new Set(["marketData", "book", "candles", "tape"]);

function notify(slice) {
  if (WS_NOTIFY_SLICES.has(slice)) state.lastWsActivity = Date.now();
  if (MD_NOTIFY_SLICES.has(slice)) state.lastMdActivity = Date.now();
  for (const fn of listeners) fn(slice);
}

export function getState() { return state; }

export function setUser(user)     { state.user = user;      notify("user"); }
export function setStatus(status) { state.status = status;  notify("status"); }

export function applyOrdersSnapshot(rows) {
  state.orders = new Map(rows.map(r => [r.clOrdId, r]));
  // Assign arrival sequence to any ClOrdID we haven't seen yet.
  // Snapshots replay the full set on reconnect — preserve previously-
  // assigned numbers so the blotter ordering stays stable across
  // reconnects.
  for (const r of rows) {
    if (!state.orderSeq.has(r.clOrdId)) {
      state.orderSeqCounter += 1;
      state.orderSeq.set(r.clOrdId, state.orderSeqCounter);
    }
  }
  notify("orders");
}
export function applyOrdersDelta(row) {
  state.orders.set(row.clOrdId, row);
  if (!state.orderSeq.has(row.clOrdId)) {
    state.orderSeqCounter += 1;
    state.orderSeq.set(row.clOrdId, state.orderSeqCounter);
  }
  // Mark this row as freshly updated so the UI can flash it; the
  // highlight expires naturally — readers compare against now.
  state.ordersHighlight.set(row.clOrdId, Date.now());
  notify("orders");
}

export function applyPositionsSnapshot(rows) {
  state.positions = new Map(rows.map(r => [r.symbol, r]));
  notify("positions");
}
export function applyPositionsDelta(row) {
  if (row.netQuantity === 0) state.positions.delete(row.symbol);
  else state.positions.set(row.symbol, row);
  notify("positions");
}

export function applyExecutionsSnapshot(rows) {
  // Server sends an empty array on initial subscribe (no historical
  // log in v1); keep whatever we accumulated since.
  if (Array.isArray(rows) && rows.length > 0) {
    state.executions = rows.slice(-EXECUTIONS_CAPACITY);
    notify("executions");
  } else {
    // Even an empty snapshot proves the WS is alive — stamp activity
    // explicitly so the staleness overlay clears on subscribe.
    state.lastWsActivity = Date.now();
  }
}
export function applyExecutionsDelta(row) {
  state.executions.push(row);
  if (state.executions.length > EXECUTIONS_CAPACITY) {
    state.executions.splice(0, state.executions.length - EXECUTIONS_CAPACITY);
  }
  notify("executions");
}

export function clearAll() {
  state.orders.clear();
  state.positions.clear();
  state.executions = [];
  state.ordersHighlight.clear();
  state.orderSeq.clear();
  state.orderSeqCounter = 0;
  state.blotterPage = 1;
  state.selectedClOrdId = null;
  state.pendingFatFinger = null;
  state.inflightCancels.clear();
  state.inflightModifies.clear();
  state.lastWsActivity = null;
  state.lastMdActivity = null;
  // Q1.6 (#258). Phase + auction caches survive market-data restarts
  // but die with the trader-WS reconnect (which is what triggers
  // clearAll) — the server replays snapshots after the (re)subscribe.
  state.phaseBySymbol.clear();
  state.phaseAtBySymbol.clear();
  state.auctionBySymbol.clear();
  notify("all");
}

// ── Market data slice ──────────────────────────────────────────────

export function setMarketDataStatus(status) {
  state.marketDataStatus = status;
  notify("marketDataStatus");
}

export function applyMdTrade({ symbol, price, qty, tradeId }) {
  const prev = state.marketData.get(symbol) || {};
  const prevPrice = prev.lastPrice;
  state.marketData.set(symbol, {
    ...prev,
    lastPrice: price,
    lastQty: qty,
    lastTradeId: tradeId,
    updatedAt: Date.now(),
  });
  notify("marketData");
  // Tape: append to the per-symbol ring; infer side from the previous
  // trade's price (TRADE frame has no aggressor field). 'flat' = first
  // trade or unchanged price.
  pushTapeEntry(symbol, {
    tradeId,
    price,
    qty,
    side: prevPrice == null ? "flat"
        : price > prevPrice ? "up"
        : price < prevPrice ? "down" : "flat",
    receivedAt: Date.now(),
    busted: false,
  });
}

export function applyMdInfo({ symbol, fields }) {
  const prev = state.marketData.get(symbol) || {};
  // Seed lastPrice from snapshot if we haven't seen a live trade yet.
  // Otherwise the live tape wins to avoid the snapshot stomping on a
  // newer print that happened to race the periodic snapshot.
  const seed = prev.lastPrice == null
    ? (fields.LastTradePrice ?? fields.TradingReferencePrice ?? null)
    : prev.lastPrice;
  state.marketData.set(symbol, {
    ...prev,
    lastPrice: seed,
    info: fields,
    updatedAt: Date.now(),
  });
  notify("marketData");
}

export function removeMdSymbol(symbol) {
  let touched = state.marketData.delete(symbol);
  if (touched) notify("marketData");
  if (state.tape.delete(symbol)) notify("tape");
}

export function clearMarketData() {
  if (state.marketData.size === 0) return;
  state.marketData.clear();
  notify("marketData");
}

// ── Depth-of-Book slice (T2) ───────────────────────────────────────

// Side encoding from mdWorker (mdProtocol.SIDE): 0=Bid, 1=Ask.
const SIDE_BID = 0;
const SIDE_ASK = 1;

// Decoder produces JS numbers post-PRICE_DIVISOR division. The wire
// price exponent is -4, so toFixed(4) is the canonical bucket key —
// resilient to any future float-rounding drift.
function priceKey(price) { return Number(price).toFixed(4); }

function ensureBook(symbol) {
  let entry = state.book.get(symbol);
  if (!entry) {
    entry = { bids: new Map(), asks: new Map(), ready: false, updatedAt: 0 };
    state.book.set(symbol, entry);
  }
  return entry;
}

function sideMap(entry, side) {
  if (side === SIDE_BID) return entry.bids;
  if (side === SIDE_ASK) return entry.asks;
  return null;
}

export function applyMdBookSnapshot({ symbol }) {
  // Marker that a fresh full snapshot is incoming — the level.snapshot
  // that follows carries the data. Mark not-ready and clear so the UI
  // doesn't render a half-built book during the gap.
  const entry = ensureBook(symbol);
  entry.bids.clear();
  entry.asks.clear();
  entry.ready = false;
  entry.updatedAt = Date.now();
  notify("book");
}

export function applyMdLevelSnapshot({ symbol, bids, asks }) {
  const entry = ensureBook(symbol);
  entry.bids.clear();
  entry.asks.clear();
  for (const lv of bids ?? []) entry.bids.set(priceKey(lv.price), { qty: lv.qty, count: lv.count });
  for (const lv of asks ?? []) entry.asks.set(priceKey(lv.price), { qty: lv.qty, count: lv.count });
  entry.ready = true;
  entry.updatedAt = Date.now();
  notify("book");
}

export function applyMdLevelUpdate({ symbol, side, price, qty, count }) {
  const entry = ensureBook(symbol);
  // Drop incremental updates before the first level.snapshot — they
  // would build a partial / misleading book.
  if (!entry.ready) return;
  const target = sideMap(entry, side);
  if (target === null) return; // defensive: ignore malformed/future sides
  target.set(priceKey(price), { qty, count });
  entry.updatedAt = Date.now();
  notify("book");
}

export function applyMdLevelDeleted({ symbol, side, price }) {
  const entry = state.book.get(symbol);
  if (!entry?.ready) return;
  const target = sideMap(entry, side);
  if (target === null) return;
  if (target.delete(priceKey(price))) {
    entry.updatedAt = Date.now();
    notify("book");
  }
}

export function applyMdBookCleared({ symbol, side }) {
  const entry = state.book.get(symbol);
  if (!entry) return;
  if (side === null || side === undefined) {
    entry.bids.clear();
    entry.asks.clear();
  } else {
    const target = sideMap(entry, side);
    if (target === null) return;
    target.clear();
  }
  entry.updatedAt = Date.now();
  notify("book");
}

export function removeBookSymbol(symbol) {
  if (state.book.delete(symbol)) notify("book");
}

export function clearAllBooks() {
  if (state.book.size === 0) return;
  state.book.clear();
  notify("book");
}

export function setDobSymbol(symbol) {
  // Back-compat shim: DOB selector is now driven by selectedSymbol.
  setSelectedSymbol(symbol);
}

export function setSelectedSymbol(symbol) {
  const next = symbol == null || symbol === "" ? null : symbol;
  if (state.selectedSymbol === next) return;
  state.selectedSymbol = next;
  state.selectedSymbolSetAt = next ? Date.now() : 0;
  notify("selectedSymbol");
}

// ── Candle slice (T3) ──────────────────────────────────────────────

// Bars-per-resolution memory cap. Long histories (e.g. day-long 1m
// chart at 8h session ≈ 480 bars) fit comfortably; beyond that we
// drop the oldest. Snapshot replace and live update both honour this.
const MAX_BARS = 600;

// Supported resolutions in seconds. Frames carrying any other
// resolution are accepted into state (the server may multiplex them)
// but the chart selector exposes only these three.
export const CHART_RESOLUTIONS = [60, 300, 900];

function ensureCandleEntry(symbol, resolution) {
  let perRes = state.candles.get(symbol);
  if (!perRes) {
    perRes = new Map();
    state.candles.set(symbol, perRes);
  }
  let entry = perRes.get(resolution);
  if (!entry) {
    entry = { bars: [], ready: false, updatedAt: 0 };
    perRes.set(resolution, entry);
  }
  return entry;
}

function trimBars(entry) {
  const overflow = entry.bars.length - MAX_BARS;
  if (overflow > 0) entry.bars.splice(0, overflow);
}

export function applyMdCandleSnapshot({ symbol, resolution, candles, isFirst, isLast }) {
  const entry = ensureCandleEntry(symbol, resolution);
  if (isFirst) {
    // Fresh history sequence — restart accumulation.
    entry.bars = candles.slice();
    entry.ready = false;
    entry._startedFromFirst = true;
  } else {
    // Mid/tail frame. If we never saw FIRST (mid-stream join, or the
    // first frame was lost), accumulate but stay not-ready — we'd
    // rather show "awaiting…" than present a truncated history as
    // complete.
    entry.bars.push(...candles);
  }
  trimBars(entry);
  if (isLast && entry._startedFromFirst) {
    entry.ready = true;
    entry._startedFromFirst = false;
  }
  entry.updatedAt = Date.now();
  notify("candles");
}

export function applyMdCandleUpdate({ symbol, resolution, candle }) {
  const perRes = state.candles.get(symbol);
  const entry = perRes?.get(resolution);
  if (!entry?.ready) return; // drop until snapshot completes
  const last = entry.bars[entry.bars.length - 1];
  if (last && last.time === candle.time) {
    entry.bars[entry.bars.length - 1] = candle;
  } else {
    entry.bars.push(candle);
    trimBars(entry);
  }
  entry.updatedAt = Date.now();
  notify("candles");
}

export function removeCandlesSymbol(symbol) {
  if (state.candles.delete(symbol)) notify("candles");
}

export function clearAllCandles() {
  if (state.candles.size === 0) return;
  state.candles.clear();
  notify("candles");
}

export function setChartSymbol(symbol) {
  // Back-compat shim: chart selector is now driven by selectedSymbol.
  setSelectedSymbol(symbol);
}

export function setChartResolution(seconds) {
  const next = Number(seconds);
  if (!CHART_RESOLUTIONS.includes(next)) return;
  if (state.chartResolution === next) return;
  state.chartResolution = next;
  notify("chartResolution");
}

// ── Trade tape slice (T4) ──────────────────────────────────────────

// Per-symbol cap on the tape ring buffer. 200 keeps the cross-symbol
// "all" view bounded as well: even with N watched symbols, total
// memory is N * 200, and the render path slices a top-200 window.
const TAPE_MAX = 200;

function pushTapeEntry(symbol, entry) {
  let arr = state.tape.get(symbol);
  if (!arr) { arr = []; state.tape.set(symbol, arr); }
  arr.push(entry);
  const overflow = arr.length - TAPE_MAX;
  if (overflow > 0) arr.splice(0, overflow);
  notify("tape");
}

export function applyMdTradeBust({ symbol, tradeId }) {
  const arr = state.tape.get(symbol);
  if (!arr) return;
  // Search from the end — busts overwhelmingly target very recent
  // prints, so the linear walk almost always hits in O(1) trips.
  for (let i = arr.length - 1; i >= 0; i--) {
    if (arr[i].tradeId === tradeId) {
      if (arr[i].busted) return; // already marked
      arr[i] = { ...arr[i], busted: true };
      notify("tape");
      return;
    }
  }
}

export function removeTapeSymbol(symbol) {
  if (state.tape.delete(symbol)) notify("tape");
}

export function clearAllTape() {
  if (state.tape.size === 0) return;
  state.tape.clear();
  notify("tape");
}

export function setTapeSymbol(symbol) {
  // Back-compat shim — empty/null means "all", anything else
  // becomes the global selectedSymbol with showAll cleared.
  if (symbol == null || symbol === "") {
    setTapeShowAll(true);
  } else {
    setTapeShowAll(false);
    setSelectedSymbol(symbol);
  }
}

export function setTapeShowAll(showAll) {
  const next = !!showAll;
  if (state.tapeShowAll === next) return;
  state.tapeShowAll = next;
  notify("tapeShowAll");
}

export function setWatchlist(symbols) {
  const next = symbols.slice();
  state.watchlist = next;
  // Drop book + candle + tape caches for symbols no longer watched.
  const wanted = new Set(next);
  for (const sym of [...state.book.keys()]) {
    if (!wanted.has(sym)) state.book.delete(sym);
  }
  for (const sym of [...state.candles.keys()]) {
    if (!wanted.has(sym)) state.candles.delete(sym);
  }
  for (const sym of [...state.tape.keys()]) {
    if (!wanted.has(sym)) state.tape.delete(sym);
  }
  // Reset shared selectedSymbol if the chosen symbol just left the
  // watchlist; auto-pick the first available so DOB/chart/tape don't
  // sit empty when symbols exist. tapeShowAll is preserved.
  if (state.selectedSymbol && !wanted.has(state.selectedSymbol)) {
    state.selectedSymbol = null;
    state.selectedSymbolSetAt = 0;
    notify("selectedSymbol");
  }
  if (state.selectedSymbol === null && next.length > 0) {
    state.selectedSymbol = next[0];
    state.selectedSymbolSetAt = Date.now();
    notify("selectedSymbol");
  }
  notify("watchlist");
  notify("book");
  notify("candles");
  notify("tape");
}

export function isTerminalOrderStatus(status) {
  return TERMINAL_ORDER_STATUSES.has(status);
}

export function setRiskPolicy(policy) {
  state.riskPolicy = policy;
  notify("riskPolicy");
}

// ── UX-only slices (operability pass) ──────────────────────────────

export function setSubmitInflight(value) {
  state.submitInflight = value;
  notify("submitInflight");
}

export function setWsReconnect(value) {
  state.wsReconnect = value;
  notify("wsReconnect");
}

export function setFirmsHealth(value) {
  state.firmsHealth = value;
  notify("firmsHealth");
}

export function setGatewayHealth(value) {
  state.gatewayHealth = value;
  notify("gatewayHealth");
}

export function setKillStatus(value) {
  state.killStatus = value;
  notify("killStatus");
}

export function setHaltStatus(value) {
  state.haltStatus = value;
  notify("haltStatus");
}

export function setEodReport(value) {
  state.eodReport = value;
  notify("eodReport");
}

export function setCurrentView(view) {
  if (state.currentView === view) return;
  state.currentView = view;
  notify("currentView");
}

// ── Blotter UX slices (section 3 of #30) ───────────────────────────

export function setBlotterFilter(filter) {
  state.blotterFilter = {
    text:   typeof filter?.text   === "string" ? filter.text   : "",
    status: typeof filter?.status === "string" ? filter.status : "",
  };
  // Filter changes shrink the visible set; reset pagination so the
  // user lands on results instead of a now-empty page N.
  state.blotterPage = 1;
  notify("blotterFilter");
}

export function setBlotterPage(page) {
  const next = Math.max(1, Math.floor(Number(page) || 1));
  if (state.blotterPage === next) return;
  state.blotterPage = next;
  notify("blotterPage");
}

export function setSelectedOrder(clOrdId) {
  state.selectedClOrdId = clOrdId ?? null;
  notify("selectedOrder");
}

export function setPendingFatFinger(payload, key) {
  state.pendingFatFinger = payload ? { payload, key, setAt: Date.now() } : null;
  notify("pendingFatFinger");
}

// In-flight cancel tracking. The blotter Cancel button calls
// markCancelInflight(clOrdId, true) immediately on click so the row
// renders disabled; once the server ER lands (or the cancel errors)
// the caller toggles it back. Re-entrant safe — Set semantics.
export function markCancelInflight(clOrdId, inflight) {
  if (!clOrdId) return;
  const before = state.inflightCancels.has(clOrdId);
  if (inflight) state.inflightCancels.add(clOrdId);
  else state.inflightCancels.delete(clOrdId);
  if (before !== inflight) notify("orders");
}

// Slice 5 of #122. Modify counterpart of markCancelInflight — flips
// the per-ClOrdID flag the renderer reads to decide whether the row's
// Modify button shows "Modifying…" + disabled.
export function markModifyInflight(clOrdId, inflight) {
  if (!clOrdId) return;
  const before = state.inflightModifies.has(clOrdId);
  if (inflight) state.inflightModifies.add(clOrdId);
  else state.inflightModifies.delete(clOrdId);
  if (before !== inflight) notify("orders");
}

// ── Q1.6 (#258): Auction phase + auction state slices ──────────────
//
// Two public WS channels per symbol:
//   • phases.${symbol}  → { symbol, phase, at }   (snapshot + deltas)
//   • auction.${symbol} → either { symbol, top, indicativeMatchQty,
//                                 imbalance, imbalanceSide, at, kind:null }
//                         (top frame; nullable fields after a print)
//                       or { symbol, kind:"Opening"|"Closing", price,
//                            qty, at }            (cross print delta)
// The two frames are discriminated on the wire by `price` presence:
// only AuctionPrintDto carries `price`. Snapshot vs delta distinction
// is irrelevant on the auction channel — both shapes are merged.

// Phases the order ticket should treat as auction (see #258 §Ticket
// coupling: TIF default → GoodForAuction, Day → soft warning).
const AUCTION_PHASES = new Set(["OpeningCall", "FinalClosingCall"]);

// Cap on the per-symbol AuctionPrint history rendered in the panel.
// Five matches the visible scrollless rows in the panel layout.
const AUCTION_PRINT_HISTORY = 5;

export function applyPhaseFrame(payload) {
  if (!payload || typeof payload.symbol !== "string") return;
  const symbol = payload.symbol;
  const phase = typeof payload.phase === "string" ? payload.phase : "Unknown";
  state.phaseBySymbol.set(symbol, phase);
  state.phaseAtBySymbol.set(symbol, payload.at ?? null);
  notify("phases");
}

export function applyAuctionFrame(payload) {
  if (!payload || typeof payload.symbol !== "string") return;
  const symbol = payload.symbol;
  let entry = state.auctionBySymbol.get(symbol);
  if (!entry) {
    entry = {
      top: null, indicativeMatchQty: null,
      imbalance: null, imbalanceSide: null,
      at: null, prevTop: null, lastPrints: [],
    };
    state.auctionBySymbol.set(symbol, entry);
  }
  // Discriminator: AuctionPrintDto has `price`; AuctionSnapshotDto has
  // `top` (possibly null) and no `price` field.
  if (payload.price !== undefined) {
    entry.lastPrints.unshift({
      kind:  payload.kind ?? null,
      price: payload.price,
      qty:   payload.qty,
      at:    payload.at ?? null,
    });
    if (entry.lastPrints.length > AUCTION_PRINT_HISTORY) {
      entry.lastPrints.length = AUCTION_PRINT_HISTORY;
    }
  } else {
    // Top / imbalance frame. Track previous top BEFORE overwriting so
    // the renderer can draw the up/down trend arrow. Skip the bookkeep
    // when both are null (an "empty" snapshot served on cold subscribe
    // — no trend yet).
    if (entry.top != null && payload.top != null && entry.top !== payload.top) {
      entry.prevTop = entry.top;
    }
    entry.top                = payload.top ?? null;
    entry.indicativeMatchQty = payload.indicativeMatchQty ?? null;
    entry.imbalance          = payload.imbalance ?? null;
    entry.imbalanceSide      = payload.imbalanceSide ?? null;
    entry.at                 = payload.at ?? null;
  }
  notify("auction");
}

export function setAuctionPanelSymbol(symbol) {
  const next = symbol == null || symbol === "" ? null : symbol;
  if (state.auctionPanelSymbol === next) return;
  state.auctionPanelSymbol = next;
  notify("auctionPanelSymbol");
}

export function getPhase(symbol) {
  if (!symbol) return "Unknown";
  return state.phaseBySymbol.get(symbol) ?? "Unknown";
}

export function getAuctionState(symbol) {
  if (!symbol) return null;
  return state.auctionBySymbol.get(symbol) ?? null;
}

export function isAuctionPhase(phase) {
  return AUCTION_PHASES.has(phase);
}
