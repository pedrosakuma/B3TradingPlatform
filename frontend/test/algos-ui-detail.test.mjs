import { test } from "node:test";
import assert from "node:assert/strict";

globalThis.localStorage = {
  getItem: () => null,
  setItem: () => {},
};

const { getAlgoDetailParamEntries } = await import("../js/algosUi.js");

test("getAlgoDetailParamEntries formats price and quantity fields per algo type", () => {
  const entries = getAlgoDetailParamEntries({
    type: "Iceberg",
    iceberg: {
      displayQuantity: 2500,
      limitPrice: 31.25,
    },
  });

  assert.deepEqual(entries, [
    ["Display quantity", "2,500"],
    ["Limit price", "31.25"],
  ]);
});

test("getAlgoDetailParamEntries skips empty values and preserves typed labels", () => {
  const entries = getAlgoDetailParamEntries({
    type: "Vwap",
    vwap: {
      startUtc: "2026-01-01T13:00:00Z",
      endUtc: "2026-01-01T14:00:00Z",
      childOrderType: "Market",
      childPrice: null,
      tickIntervalSeconds: 30,
      sliceMaxPct: "",
      participationCap: 0.2,
      priceLimit: 30.5,
    },
  });

  assert.deepEqual(entries, [
    ["Start UTC", "2026-01-01T13:00:00Z"],
    ["End UTC", "2026-01-01T14:00:00Z"],
    ["Child order type", "Market"],
    ["Tick interval (s)", "30"],
    ["Participation cap", "0.2"],
    ["Price limit", "30.50"],
  ]);
});

test("getAlgoDetailParamEntries includes Pegged child order type", () => {
  const entries = getAlgoDetailParamEntries({
    type: "Pegged",
    pegged: {
      ref: "Mid",
      offsetTicks: -2,
      repegIntervalMs: 500,
      tickSize: 0.01,
      childOrderType: "Limit",
      priceLimit: 31.4,
    },
  });

  assert.deepEqual(entries, [
    ["Reference", "Mid"],
    ["Offset ticks", "-2"],
    ["Repeg interval (ms)", "500"],
    ["Tick size", "0.01"],
    ["Child order type", "Limit"],
    ["Price limit", "31.40"],
  ]);
});

test("getAlgoDetailParamEntries returns an empty list for unknown or missing blocks", () => {
  assert.deepEqual(getAlgoDetailParamEntries(null), []);
  assert.deepEqual(getAlgoDetailParamEntries({ type: "Pegged" }), []);
  assert.deepEqual(getAlgoDetailParamEntries({ type: "Unknown", unknown: {} }), []);
});
