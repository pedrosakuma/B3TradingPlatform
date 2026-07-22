// Regression coverage for #696/#698: nginx.conf.template's `$is_api` map is
// the frontend's proxy allowlist. After the /api consolidation every REST
// call should funnel through the single `api` prefix (with `ws` separate for
// WebSockets and `/health`/`/ready`/`/live` staying at the root).
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
    // Only count *active* entries mapped to `1` (ignore comments, the
    // `default 0;` line, and any future `0`-valued entries), e.g.:
    //   ~^/api(/|$)                          1;
    const m = line.match(/^\s*~\^\/([a-zA-Z0-9_-]+)\([^)]*\)\s+1;\s*$/);
    if (m) prefixes.add(m[1]);
  }
  return prefixes;
}

// Extracts the first path segment out of a URL-path-shaped string literal,
// e.g. `/api/statement/${dayKey}` -> "api", `/ws/dropcopy` -> "ws".
function firstSegment(literal) {
  const m = literal.match(/^\/([a-zA-Z0-9_-]+)/);
  return m ? m[1] : null;
}

function readBackendPrefixesCalledFromProtocol() {
  const protocol = readFileSync(
    path.join(frontendRoot, "js", "protocol.js"),
    "utf8",
  );
  const prefixes = new Set();

  // Case A: a literal path directly following the `${backend}` interpolation,
  // e.g. fetch(`${backend}/api/orders/history`, ...).
  for (const m of protocol.matchAll(/\$\{backend\}\/([a-zA-Z0-9_-]+)/g)) {
    prefixes.add(m[1]);
  }

  // Case B: new URL("/some/path", backend) — the two-arg base-URL form,
  // e.g. new URL("/ws/dropcopy", backend).
  for (const m of protocol.matchAll(
    /new URL\(\s*[`"']([^`"']+)[`"']\s*,\s*backend\s*\)/g,
  )) {
    const seg = firstSegment(m[1]);
    if (seg) prefixes.add(seg);
  }

  // Case C: `${backend}${someVar}` — the path is built in a separate
  // variable (often a ternary picking between two equivalent-prefix
  // literals), e.g.:
  //   const path = dayKey ? `/api/statement/${...}` : `/api/statement`;
  //   fetch(`${backend}${path}`, ...)
  // Resolve each referenced variable back to its nearest preceding
  // declaration and pull every path-shaped literal out of it.
  for (const m of protocol.matchAll(/\$\{backend\}\$\{([a-zA-Z_$][\w$]*)\}/g)) {
    const varName = m[1];
    const declRe = new RegExp(`\\b(?:const|let)\\s+${varName}\\s*=`);
    const declMatch = declRe.exec(protocol);
    assert.ok(
      declMatch,
      `expected to find a "const/let ${varName} = ..." declaration feeding ` +
        `\${backend}\${${varName}}, so its literal(s) can be checked`,
    );
    const declEnd = protocol.indexOf(";", declMatch.index);
    const declSnippet = protocol.slice(
      declMatch.index,
      declEnd === -1 ? protocol.length : declEnd + 1,
    );
    const literals = declSnippet.matchAll(/[`"']\/[a-zA-Z][a-zA-Z0-9_-]*/g);
    let found = false;
    for (const lit of literals) {
      const seg = firstSegment(lit[0].slice(1));
      if (seg) {
        prefixes.add(seg);
        found = true;
      }
    }
    assert.ok(
      found,
      `expected at least one "/path" literal inside the "${varName}" ` +
        `declaration referenced via \${backend}\${${varName}}`,
    );
  }

  return prefixes;
}

test("every backend prefix called from protocol.js is proxied by nginx's $is_api whitelist", () => {
  const whitelisted = readIsApiWhitelist();
  const called = readBackendPrefixesCalledFromProtocol();

  const missing = [...called].filter((prefix) => !whitelisted.has(prefix));

  assert.deepEqual(
    missing,
    [],
    `these backend prefixes are called from protocol.js but missing from ` +
      `nginx.conf.template's $is_api map, so they would 405 through the real ` +
      `deployed frontend (see #696): ${missing.join(", ")}`,
  );
});

test("$is_api whitelist is collapsed to the stable api/ws + root probe prefixes", () => {
  const whitelisted = readIsApiWhitelist();
  assert.deepEqual(
    [...whitelisted].sort(),
    ["api", "health", "live", "ready", "ws"],
  );
});

test("protocol.js only targets the consolidated /api REST prefix plus /ws", () => {
  const called = readBackendPrefixesCalledFromProtocol();
  assert.deepEqual([...called].sort(), ["api", "ws"]);
});
