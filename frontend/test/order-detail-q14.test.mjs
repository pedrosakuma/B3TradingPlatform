// Q1.4 (#256) — Order Detail modal (#245) header surfaces the new
// Q1.1 fields: TIF, StopPrice (when non-null), GoodTillDate (when
// non-null). fmtGtd is unit-tested independently here.

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({ ids: {} });

const { fmtGtd } = await import("../js/ui.js");

test("fmtGtd returns '—' for null / empty", () => {
  assert.equal(fmtGtd(null), "—");
  assert.equal(fmtGtd(""), "—");
  assert.equal(fmtGtd(undefined), "—");
});

test("fmtGtd formats an ISO timestamp with pt-BR date order and explicit UTC", () => {
  assert.equal(fmtGtd("2026-05-07T14:30:00Z"), "07/05/2026, 14:30 UTC");
});

test("fmtGtd zero-pads single-digit components", () => {
  assert.equal(fmtGtd("2026-01-02T03:04:00Z"), "02/01/2026, 03:04 UTC");
});

test("fmtGtd echoes the (escaped) input for an unparseable string", () => {
  assert.equal(fmtGtd("not-a-date"), "not-a-date");
});
