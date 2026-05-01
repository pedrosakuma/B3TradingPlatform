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
  user: null,              // { username, expiresAt, token, backend, firm }
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
  notify("all");
}

export function isTerminalOrderStatus(status) {
  return TERMINAL_ORDER_STATUSES.has(status);
}
