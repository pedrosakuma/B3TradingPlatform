// Regression coverage for #696: nginx.conf.template's `$is_api` map is an
// explicit whitelist of path prefixes proxied to trading-host. Any REST
// prefix called by the frontend (frontend/js/protocol.js) that is missing
// from this whitelist silently falls through to the SPA static-file
// handler, which only accepts GET/HEAD — producing a 405 for any other
// verb (this is exactly how the /balance/deposit bug in #696 happened).
//
// This test can't spin up the real nginx container in every environment
// (the Docker image build needs npm registry access, which is blocked in
// some sandboxes/CI runners), so instead it statically parses both the
// nginx template and protocol.js and cross-checks them. It intentionally
// does NOT require a docker build, so it always runs as part of the plain
// `node --test` suite.

import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const frontendRoot = path.resolve(here, "..");

function readIsApiWhitelist() {
  const template = readFileSync(
    path.join(frontendRoot, "nginx.conf.template"),
    "utf8",
  );
  const mapMatch = template.match(/map \$uri \$is_api \{([\s\S]*?)\}/);
  assert.ok(mapMatch, "expected to find `map $uri $is_api { ... }` block");

  const prefixes = new Set();
  for (const line of mapMatch[1].split("\n")) {
    // Lines look like: `~^/balance(/|$)                      1;`
    const m = line.match(/~\^\/([a-zA-Z0-9_-]+)/);
    if (m) prefixes.add(m[1]);
  }
  return prefixes;
}

function readBackendPrefixesCalledFromProtocol() {
  const protocol = readFileSync(
    path.join(frontendRoot, "js", "protocol.js"),
    "utf8",
  );
  // Matches both `${backend}/foo` (template literals) and `${backend}/foo/`.
  const matches = protocol.matchAll(/\$\{backend\}\/([a-zA-Z0-9_-]+)/g);
  const prefixes = new Set();
  for (const m of matches) prefixes.add(m[1]);
  return prefixes;
}

test("every REST prefix called from protocol.js is proxied by nginx's $is_api whitelist", () => {
  const whitelisted = readIsApiWhitelist();
  const called = readBackendPrefixesCalledFromProtocol();

  const missing = [...called].filter((prefix) => !whitelisted.has(prefix));

  assert.deepEqual(
    missing,
    [],
    `these REST prefixes are called from protocol.js but missing from ` +
      `nginx.conf.template's $is_api map, so they would 405 through the real ` +
      `deployed frontend (see #696): ${missing.join(", ")}`,
  );
});

test("$is_api whitelist includes /balance and /sub-accounts (regression for #696)", () => {
  const whitelisted = readIsApiWhitelist();
  assert.ok(whitelisted.has("balance"), "/balance must be proxied to trading-host");
  assert.ok(
    whitelisted.has("sub-accounts"),
    "/sub-accounts must be proxied to trading-host",
  );
});
