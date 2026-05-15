// Q1.4 (#256) — Expired ER (kind=Expired from #255 backend) renders
// in the executions log with the gray .Expired class. Pass-1 review
// correction: `Expired` belongs to `ExecKind` (executions log) only —
// the backend `OrderStatus` enum has no Expired member, and a GTD
// order's terminal status is `Cancelled` (set by the cancel pipeline
// the GTD scheduler invokes). The dedicated assertion that this is
// NOT an OrderStatus lives in expired-status-surfaces.test.mjs.

import { test } from "node:test";
import assert from "node:assert/strict";

// Inspect the CSS bundle once — guarantees the gray Expired badge
// rule actually shipped instead of silently falling back to default
// text color (which would make the badge visually identical to a
// fill).
test("styles.css ships an .executions-log .Expired rule", async () => {
  const fs = await import("node:fs/promises");
  const css = await fs.readFile(new URL("../css/styles.css", import.meta.url), "utf8");
  assert.match(css, /\.executions-log\s+\.Expired\s*\{[^}]*color:\s*#888/);
});
