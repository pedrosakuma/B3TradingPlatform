// #71: Volume heatmap — pure reducer + normaliser. The renderer is
// purely a function of these two helpers, so covering them under
// node --test gates the colour-scale logic without a DOM.

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";
installDomStub({ ids: {} });

const { computeHeatmapVolumes, HEATMAP_WINDOW_MS } = await import("../js/state.js");
const { normaliseHeatmap } = await import("../js/ui.js");

test("computeHeatmapVolumes sums only entries within the window", () => {
  const now = 10_000;
  const map = new Map([
    ["PETR4", [
      { ts: now - 15_000, qty: 100 }, // outside 10s window
      { ts: now -  9_000, qty:  50 },
      { ts: now -  1_000, qty:  25 },
    ]],
    ["VALE3", [
      { ts: now -  5_000, qty: 200 },
    ]],
  ]);
  const out = computeHeatmapVolumes(map, ["PETR4", "VALE3", "ITUB4"], now);
  assert.equal(out.get("PETR4"), 75);
  assert.equal(out.get("VALE3"), 200);
  assert.equal(out.get("ITUB4"), 0);
});

test("computeHeatmapVolumes respects a caller-supplied windowMs", () => {
  const now = 10_000;
  const map = new Map([
    ["PETR4", [
      { ts: now - 4_000, qty: 10 },
      { ts: now - 2_000, qty: 20 },
    ]],
  ]);
  // 3s window only includes the more recent print.
  const out = computeHeatmapVolumes(map, ["PETR4"], now, 3_000);
  assert.equal(out.get("PETR4"), 20);
});

test("normaliseHeatmap scales by global max in [0,1]", () => {
  const out = normaliseHeatmap(new Map([["A", 100], ["B", 50], ["C", 0]]));
  assert.equal(out.get("A"), 1);
  assert.equal(out.get("B"), 0.5);
  assert.equal(out.get("C"), 0);
});

test("normaliseHeatmap returns all-zero when no symbol has any volume", () => {
  const out = normaliseHeatmap(new Map([["A", 0], ["B", 0]]));
  assert.equal(out.get("A"), 0);
  assert.equal(out.get("B"), 0);
});

test("HEATMAP_WINDOW_MS exposes the default 10s window", () => {
  assert.equal(HEATMAP_WINDOW_MS, 10_000);
});
