// PR #418 split the legacy `Replaced` ExecKind into two events: the
// original ClOrdID terminalises as `Replaced` and the replacement
// entering Working surfaces as `ReplacedNew`. The raw enum spelling
// would render literally as "ReplacedNew" in the executions log —
// `execKindLabel()` in ui.js rewrites it to the user-friendly
// "Replacement" while leaving every other kind untouched (so a
// future ExecKind addition stays visible while the label table
// catches up).

import { test } from "node:test";
import assert from "node:assert/strict";

import { execKindLabel } from "../js/ui.js";

test("execKindLabel maps ReplacedNew to 'Replacement'", () => {
  assert.equal(execKindLabel("ReplacedNew"), "Replacement");
});

test("execKindLabel passes through known kinds unchanged", () => {
  for (const k of ["New", "PartialFill", "Fill", "Cancelled", "Rejected",
                   "Replaced", "Expired", "ReplaceRejected"]) {
    assert.equal(execKindLabel(k), k);
  }
});

test("execKindLabel passes through unknown kinds (forward-compat)", () => {
  assert.equal(execKindLabel("SomeFutureKind"), "SomeFutureKind");
});

test("execKindLabel coerces null/undefined to empty string", () => {
  assert.equal(execKindLabel(null), "");
  assert.equal(execKindLabel(undefined), "");
});

test("styles.css ships an .executions-log .ReplacedNew rule", async () => {
  const fs = await import("node:fs/promises");
  const css = await fs.readFile(new URL("../css/styles.css", import.meta.url), "utf8");
  assert.match(css, /\.executions-log\s+\.ReplacedNew\s*\{[^}]*color:/);
});
