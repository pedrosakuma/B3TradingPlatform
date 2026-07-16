import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../../", import.meta.url);

test("design-system foundation is loaded before screen styles", async () => {
  const html = await readFile(new URL("frontend/index.html", root), "utf8");
  const designSystem = html.indexOf('href="css/design-system.css"');
  const screenStyles = html.indexOf('href="css/styles.css"');

  assert.ok(designSystem >= 0, "design-system.css must be linked");
  assert.ok(screenStyles > designSystem, "screen styles must load after design-system primitives");
});

test("design-system exposes shared tokens and primitives", async () => {
  const css = await readFile(new URL("frontend/css/design-system.css", root), "utf8");

  for (const contract of [
    "--color-accent:",
    "--space-4:",
    "--radius-md:",
    ".btn {",
    ".btn-primary {",
    ".btn-outline-primary {",
    ".btn-danger {",
    ".btn-outline-danger {",
    ".btn-success {",
    ".btn-link {",
    ".tabs {",
    ".tab {",
    ".badge-success {",
    ".badge-warning {",
    ".badge-danger {",
    ".form-field {",
    ".form-surface :where(",
    ".control,",
    ".card {",
    ".stack {",
    ".cluster {",
  ]) {
    assert.ok(css.includes(contract), `missing design-system contract: ${contract}`);
  }
});

test("shared button foundation is not redefined by screen styles", async () => {
  const css = await readFile(new URL("frontend/css/styles.css", root), "utf8");

  assert.doesNotMatch(css, /^\.btn\s*\{/m);
  assert.doesNotMatch(css, /^:root\s*\{/m);
});

test("design-system catalog demonstrates the public primitives", async () => {
  const html = await readFile(new URL("frontend/design-system.html", root), "utf8");

  assert.match(html, /css\/design-system\.css/);
  assert.match(html, /class="btn btn-primary"/);
  assert.match(html, /class="tabs tabs-bordered"/);
  assert.match(html, /class="tab active"/);
  assert.match(html, /class="form-surface catalog-form"/);
  assert.match(html, /class="card stack"/);
});

test("application surfaces compose shared tabs, badges, and table actions", async () => {
  const [html, ui] = await Promise.all([
    readFile(new URL("frontend/index.html", root), "utf8"),
    readFile(new URL("frontend/js/ui.js", root), "utf8"),
  ]);

  assert.match(html, /class="trader-subtabs tabs tabs-bordered"/);
  assert.match(html, /class="settings-subtab tab"/);
  assert.match(html, /class="status-pill badge badge-uppercase/);
  assert.match(ui, /btn btn-outline-danger btn-sm/);
  assert.match(ui, /badge badge-warning badge-outline badge-uppercase/);
});

test("first-party frontend sources do not use CSP-blocked inline styles", async () => {
  const sources = await Promise.all([
    "frontend/index.html",
    "frontend/design-system.html",
    "frontend/js/app.js",
    "frontend/js/ui.js",
  ].map(path => readFile(new URL(path, root), "utf8")));

  for (const source of sources) {
    assert.doesNotMatch(source, /\sstyle\s*=/i);
  }
});
