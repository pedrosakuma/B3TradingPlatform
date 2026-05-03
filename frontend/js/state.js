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

export function setWatchlist(symbols) {
  state.watchlist = symbols.slice();
  notify("watchlist");
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
