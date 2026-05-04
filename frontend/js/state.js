// Main-thread mirror of the worker's authoritative cache. The worker
// holds the source-of-truth Map and posts diff/replace messages; this
// module just stores what arrives and notifies subscribers.

const TERMINAL_ORDER_STATUSES = new Set(["Filled", "Cancelled", "Rejected"]);

const listeners = new Set();
const state = {
  orders: new Map(),       // ClOrdID -> OrderDto
  positions: new Map(),    // Symbol  -> PositionDto
  executions: [],          // bounded ring of ExecutionDto
  status: "disconnected",  // disconnected | connecting | connected
  user: null,              // { username, expiresAt, token, backend, firm, role }
  marketData: new Map(),   // Symbol -> { lastPrice, lastQty, lastTradeId, updatedAt, info }
  marketDataStatus: "disconnected", // disconnected | connecting | connected | not_ready
  watchlist: [],           // [string] symbols (UPPERCASE)
  // Depth-of-Book slice (T2). One book entry per symbol; sides are
  // Map<priceKey, { qty, count }>. `ready` flips false on book.snapshot
  // marker and true on the following level.snapshot — incremental
  // updates that arrive before a snapshot are dropped to avoid
  // displaying a partial book. Sorting (top-N for render) happens
  // render-side, not in state.
  book: new Map(),         // Symbol -> { bids: Map, asks: Map, ready: bool, updatedAt: number }
  dobSymbol: null,         // currently selected DOB symbol or null
  // UX-only slices added in the operability pass.
  submitInflight: null,    // { startedAt: ms } | null — true while POST /orders is awaiting response
  wsReconnect: null,       // { nextAt: ms } | null — when worker has scheduled the next attempt
  firmsHealth: null,       // { mode, firms, fetchedAt } | null — admin-only poll of /admin/firms
  killStatus: null,        // { endClients: [], firms: [], fetchedAt } | null — admin-only
  eodReport: null,         // { ranAt, report } | null — last EOD response in this session
  currentView: "trader",   // "trader" | "admin" — which view is mounted
  // Blotter UX (section 3 of #30).
  blotterFilter: { text: "", status: "" }, // { text: substring, status: "" | <OrderStatus> }
  ordersHighlight: new Map(),              // ClOrdID -> Date.now() of last delta
  selectedClOrdId: null,                   // currently selected blotter row (for keyboard cancel)
  pendingFatFinger: null,                  // { payload, key } — set when a submit needs override
};

const EXECUTIONS_CAPACITY = 500;

export function subscribe(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

function notify(slice) {
  for (const fn of listeners) fn(slice);
}

export function getState() { return state; }

export function setUser(user)     { state.user = user;      notify("user"); }
export function setStatus(status) { state.status = status;  notify("status"); }

export function applyOrdersSnapshot(rows) {
  state.orders = new Map(rows.map(r => [r.clOrdId, r]));
  notify("orders");
}
export function applyOrdersDelta(row) {
  state.orders.set(row.clOrdId, row);
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
  state.selectedClOrdId = null;
  state.pendingFatFinger = null;
  notify("all");
}

// ── Market data slice ──────────────────────────────────────────────

export function setMarketDataStatus(status) {
  state.marketDataStatus = status;
  notify("marketDataStatus");
}

export function applyMdTrade({ symbol, price, qty, tradeId }) {
  const prev = state.marketData.get(symbol) || {};
  state.marketData.set(symbol, {
    ...prev,
    lastPrice: price,
    lastQty: qty,
    lastTradeId: tradeId,
    updatedAt: Date.now(),
  });
  notify("marketData");
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
  if (state.marketData.delete(symbol)) notify("marketData");
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
  const next = symbol ?? null;
  if (state.dobSymbol === next) return;
  state.dobSymbol = next;
  notify("dobSymbol");
}

export function setWatchlist(symbols) {
  const next = symbols.slice();
  state.watchlist = next;
  // Drop book caches for symbols no longer watched.
  const wanted = new Set(next);
  for (const sym of [...state.book.keys()]) {
    if (!wanted.has(sym)) state.book.delete(sym);
  }
  // Reset DOB selection if the chosen symbol just left the watchlist.
  if (state.dobSymbol && !wanted.has(state.dobSymbol)) {
    state.dobSymbol = null;
    notify("dobSymbol");
  }
  notify("watchlist");
  notify("book");
}

export function isTerminalOrderStatus(status) {
  return TERMINAL_ORDER_STATUSES.has(status);
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

export function setKillStatus(value) {
  state.killStatus = value;
  notify("killStatus");
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
  notify("blotterFilter");
}

export function setSelectedOrder(clOrdId) {
  state.selectedClOrdId = clOrdId ?? null;
  notify("selectedOrder");
}

export function setPendingFatFinger(payload, key) {
  state.pendingFatFinger = payload ? { payload, key } : null;
  notify("pendingFatFinger");
}
