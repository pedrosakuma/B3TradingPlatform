// Fase 5 (#401). Tests for the density state slice.

import test from "node:test";
import assert from "node:assert/strict";

import { getState, setDensity, subscribe, DENSITY_VALUES } from "../js/state.js";

test("density: default is 'comfortable'", () => {
  assert.equal(getState().density, "comfortable");
});

test("density: DENSITY_VALUES exposes the whitelist", () => {
  assert.ok(DENSITY_VALUES.has("comfortable"));
  assert.ok(DENSITY_VALUES.has("compact"));
  assert.equal(DENSITY_VALUES.has("dense"), false);
});

test("density: setDensity whitelist rejects unknown values", () => {
  setDensity("compact");
  assert.equal(getState().density, "compact");
  setDensity("bogus");
  assert.equal(getState().density, "compact");
  // Restore default for sibling tests.
  setDensity("comfortable");
});

test("density: setDensity dedupes — no notify when value unchanged", () => {
  setDensity("comfortable");
  let hits = 0;
  const unsub = subscribe((slice) => { if (slice === "density") hits += 1; });
  try {
    setDensity("comfortable"); // no-op
    assert.equal(hits, 0);
    setDensity("compact");
    assert.equal(hits, 1);
    setDensity("compact"); // no-op
    assert.equal(hits, 1);
    setDensity("comfortable");
    assert.equal(hits, 2);
  } finally {
    unsub();
    setDensity("comfortable");
  }
});
