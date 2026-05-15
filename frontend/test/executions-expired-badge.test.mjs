// Q1.4 (#256) — Expired ER (kind=Expired from #255 backend) renders
// in the executions log with the gray .Expired class. Also verifies
// that "Expired" is now a terminal order status so the blotter
// terminalises GTD-expired rows the same way Cancelled does.

import { test } from "node:test";
import assert from "node:assert/strict";

import { isTerminalOrderStatus } from "../js/state.js";

test("Expired is a terminal order status (Q1.4 #256)", () => {
  assert.equal(isTerminalOrderStatus("Expired"), true);
  assert.equal(isTerminalOrderStatus("New"), false);
  assert.equal(isTerminalOrderStatus("Filled"), true);
});

// Inspect the CSS bundle once — guarantees the gray Expired badge
// rule actually shipped instead of silently falling back to default
// text color (which would make the badge visually identical to a
// fill).
test("styles.css ships an .executions-log .Expired rule", async () => {
  const fs = await import("node:fs/promises");
  const css = await fs.readFile(new URL("../css/styles.css", import.meta.url), "utf8");
  assert.match(css, /\.executions-log\s+\.Expired\s*\{[^}]*color:\s*#888/);
});
