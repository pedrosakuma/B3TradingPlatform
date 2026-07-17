import { test } from "node:test";
import assert from "node:assert/strict";

let n = 0;
async function freshState() {
  n += 1;
  return import(`../js/state.js?reconnect=${n}`);
}

test("trader websocket reconnect clears only realtime slices", async () => {
  const state = await freshState();
  state.applyOrdersSnapshot([{ clOrdId: "1", symbol: "PETR4" }]);
  state.applyAlgoSnapshot([{ algoId: "9", symbol: "PETR4" }]);
  state.applyPnlSnapshot({ totalRealized: 12, totalUnrealized: 3 });
  state.applyHistoryOrdersPage({
    items: [{ clOrdId: "H1" }],
    nextCursor: null,
    reset: true,
  });
  state.setRiskPolicy({ maxGtdHorizonDays: 14 });
  state.applyMdTrade({ symbol: "PETR4", price: 30, qty: 100, tradeId: 1n });
  const mdActivity = state.getState().lastMdActivity;

  state.clearRealtime();

  assert.equal(state.getState().orders.size, 0);
  assert.equal(state.getState().algos.size, 1);
  assert.equal(state.getState().pnl.totalRealized, 12);
  assert.equal(state.getState().historyOrders.items[0].clOrdId, "H1");
  assert.equal(state.getState().riskPolicy.maxGtdHorizonDays, 14);
  assert.equal(state.getState().marketData.get("PETR4").lastPrice, 30);
  assert.equal(state.getState().lastMdActivity, mdActivity);
});
