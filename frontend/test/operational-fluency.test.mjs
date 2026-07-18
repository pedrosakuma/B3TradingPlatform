import test from "node:test";
import assert from "node:assert/strict";

import {
  deriveOrderSubmitFeedback,
  deriveTraderEmptyState,
  deriveTradingReadiness,
} from "../js/operationalFluency.js";

const establishedGateway = {
  readyForOrders: true,
  firms: [{ firmId: "FIRM01", state: "established", reconnecting: false }],
};

test("trading readiness reports healthy canonical signals without adding eligibility", () => {
  const readiness = deriveTradingReadiness({
    status: "connected",
    gatewayHealth: establishedGateway,
    marketDataStatus: "connected",
    symbol: "PETR4",
    phase: "Open",
  });

  assert.equal(readiness.tone, "ok");
  assert.equal(readiness.title, "Ready for live trading");
  assert.deepEqual(readiness.signals.map((signal) => signal.value), [
    "Live",
    "Established",
    "Live",
    "Open",
  ]);
  assert.equal("disabled" in readiness, false);
});

test("Reserved phase truthfully identifies the existing submit blocker", () => {
  const readiness = deriveTradingReadiness({
    status: "connected",
    gatewayHealth: establishedGateway,
    marketDataStatus: "connected",
    symbol: "PETR4",
    phase: "Reserved",
  });

  assert.equal(readiness.tone, "danger");
  assert.equal(readiness.title, "Submit blocked");
  assert.match(readiness.message, /existing phase rule/);
  assert.equal(readiness.signals.at(-1).value, "Halted");
});

test("gateway degradation warns without claiming a new client-side gate", () => {
  const readiness = deriveTradingReadiness({
    status: "connected",
    gatewayHealth: {
      readyForOrders: false,
      firms: [{ firmId: "FIRM01", state: "suspended", reconnecting: false }],
    },
    marketDataStatus: "connected",
    symbol: "VALE3",
    phase: "Open",
  });

  assert.equal(readiness.title, "Venue unavailable");
  assert.match(readiness.message, /Submit availability is unchanged/);
  assert.match(readiness.message, /server remains authoritative/);
});

test("gateway health fetch failures render as unreachable without a firms array", () => {
  const readiness = deriveTradingReadiness({
    status: "connected",
    gatewayHealth: { error: "fetch_failed", fetchedAt: Date.now() },
    marketDataStatus: "connected",
    symbol: "VALE3",
    phase: "Open",
  });

  const gateway = readiness.signals.find((signal) => signal.key === "gateway");
  assert.equal(gateway.value, "Unreachable");
  assert.equal(gateway.tone, "danger");
  assert.match(gateway.detail, /health check failed/);
  assert.equal(readiness.title, "Venue unavailable");
});

test("readiness keeps market data and order-update outages as actionable warnings", () => {
  const readiness = deriveTradingReadiness({
    status: "disconnected",
    gatewayHealth: establishedGateway,
    marketDataStatus: "not_ready",
    symbol: "PETR4",
    phase: "Open",
  });

  assert.equal(readiness.tone, "warning");
  assert.equal(readiness.signals[0].value, "Offline");
  assert.equal(readiness.signals[2].value, "Unavailable");
  assert.match(readiness.signals[2].detail, /review price and quantity/);
});

test("chart and book empty states distinguish selection, waiting, timeout, and empty snapshot", () => {
  assert.equal(deriveTraderEmptyState("chart").title, "Select a symbol");
  assert.equal(
    deriveTraderEmptyState("chart", { symbol: "PETR4" }).title,
    "Waiting for candle history",
  );
  assert.equal(
    deriveTraderEmptyState("chart", { symbol: "PETR4", timedOut: true }).title,
    "No candle data received",
  );
  assert.equal(
    deriveTraderEmptyState("chart", { symbol: "PETR4", snapshotReady: true }).title,
    "No candles yet",
  );
  assert.match(
    deriveTraderEmptyState("book", { symbol: "PETR4", timedOut: true }).detail,
    /MBP/,
  );
  assert.equal(
    deriveTraderEmptyState("book", { side: "bid" }).title,
    "No bid levels",
  );
});

test("tape, orders, and executions distinguish first-use from scoped no-results", () => {
  assert.equal(
    deriveTraderEmptyState("tape", { showAll: true }).title,
    "No trades received yet",
  );
  assert.match(
    deriveTraderEmptyState("tape", { symbol: "PETR4", showAll: false }).detail,
    /All symbols/,
  );
  assert.equal(deriveTraderEmptyState("orders").title, "No working orders yet");
  assert.equal(
    deriveTraderEmptyState("orders", { filtered: true }).title,
    "No orders match this view",
  );
  assert.equal(deriveTraderEmptyState("executions").title, "No executions yet");
  assert.equal(
    deriveTraderEmptyState("executions", { filtered: true }).title,
    "No executions match this filter",
  );
});

test("order feedback separates platform acceptance from the live order update", () => {
  const accepted = deriveOrderSubmitFeedback({ clOrdId: "42" });
  assert.equal(accepted.tone, "info");
  assert.match(accepted.message, /Platform accepted/);
  assert.match(accepted.message, /Waiting for its live order update/);

  const live = deriveOrderSubmitFeedback({ clOrdId: "42", status: "New", live: true });
  assert.equal(live.tone, "ok");
  assert.match(live.message, /Live order update received/);
  assert.doesNotMatch(live.message, /Platform accepted/);

  const rejected = deriveOrderSubmitFeedback({
    clOrdId: "43",
    status: "Rejected",
    live: true,
  });
  assert.equal(rejected.tone, "error");
});
