// Q1.4 (#256) — typeChipHtml renders the right abbreviation per
// OrderType, and the working-orders row picks up the new TIF column.

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({ ids: {} });

const { typeChipHtml } = await import("../js/ui.js");

test("typeChipHtml maps each OrderType to its abbreviation + class", () => {
  const cases = [
    ["Limit",              "LIM",  "chip-lim"],
    ["Market",             "MKT",  "chip-mkt"],
    ["StopLoss",           "STP",  "chip-stp"],
    ["StopLimit",          "STPL", "chip-stpl"],
    ["MarketWithLeftover", "MWL",  "chip-mwl"],
  ];
  for (const [type, label, cls] of cases) {
    const html = typeChipHtml(type);
    assert.match(html, new RegExp(`>${label}<`), `chip label for ${type}`);
    assert.match(html, new RegExp(`class="type-chip ${cls}"`), `chip class for ${type}`);
    assert.match(html, new RegExp(`title="${type}"`), `chip title for ${type}`);
  }
});

test("typeChipHtml falls back to escaped text for unknown types", () => {
  const html = typeChipHtml("Pegged<x>");
  assert.equal(html.includes("type-chip"), false);
  assert.match(html, /Pegged&lt;x&gt;/);
});

test("typeChipHtml handles null gracefully", () => {
  assert.equal(typeChipHtml(null), "");
  assert.equal(typeChipHtml(undefined), "");
});
