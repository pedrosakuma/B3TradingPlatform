// Q2.6 (#273). Statement download — fetch + Content-Disposition parsing
// + Blob URL trigger. Pure node:test with a stubbed fetch / Blob /
// URL.createObjectURL.

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({
  ids: {
    "statement-status":      { tag: "p", hidden: true },
    "statement-json-modal":  { tag: "div", hidden: true },
    "statement-json-body":   { tag: "pre" },
  },
});

// Minimal Blob shim — Node's native global may not be present in older
// versions; the module under test only inspects `.size` and forwards
// the instance to URL.createObjectURL.
if (typeof globalThis.Blob === "undefined") {
  globalThis.Blob = class Blob {
    constructor(parts) { this._parts = parts; this.size = (parts?.[0] ?? "").length; }
  };
}

const { parseContentDispositionFilename, downloadStatementCsv } = await import("../js/protocol.js");

test("parseContentDispositionFilename extracts the plain filename", () => {
  assert.equal(
    parseContentDispositionFilename(`attachment; filename="statement-2025-01-15.csv"`),
    "statement-2025-01-15.csv",
  );
});

test("parseContentDispositionFilename handles unquoted plain filename", () => {
  assert.equal(
    parseContentDispositionFilename(`attachment; filename=statement-2025-01-15.csv`),
    "statement-2025-01-15.csv",
  );
});

test("parseContentDispositionFilename prefers RFC-5987 filename* when present", () => {
  assert.equal(
    parseContentDispositionFilename(
      `attachment; filename="fallback.csv"; filename*=UTF-8''statement-%C3%A1.csv`,
    ),
    "statement-á.csv",
  );
});

test("parseContentDispositionFilename returns null on missing / malformed header", () => {
  assert.equal(parseContentDispositionFilename(null), null);
  assert.equal(parseContentDispositionFilename(""), null);
  assert.equal(parseContentDispositionFilename("attachment"), null);
});

test("downloadStatementCsv requires dayKey", async () => {
  await assert.rejects(
    () => downloadStatementCsv("http://host", "tok", null),
    /dayKey/,
  );
});

test("downloadStatementCsv fetches CSV with bearer auth and returns blob + filename from Content-Disposition", async () => {
  const calls = [];
  globalThis.fetch = async (url, opts) => {
    calls.push({ url: String(url), opts });
    return {
      ok: true,
      status: 200,
      headers: {
        get: (name) => (name.toLowerCase() === "content-disposition"
          ? `attachment; filename="statement-2025-01-15.csv"`
          : null),
      },
      blob: async () => new Blob(["dayKey,foo\n2025-01-15,1\n"]),
    };
  };
  const { blob, filename } = await downloadStatementCsv("http://host", "tok", "2025-01-15");
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, "http://host/statement/2025-01-15.csv");
  assert.equal(calls[0].opts.headers.Authorization, "Bearer tok");
  assert.equal(filename, "statement-2025-01-15.csv");
  assert.ok(blob, "blob is returned");
});

test("downloadStatementCsv falls back to a synthesised filename when header is absent", async () => {
  globalThis.fetch = async () => ({
    ok: true,
    status: 200,
    headers: { get: () => null },
    blob: async () => new Blob(["x"]),
  });
  const { filename } = await downloadStatementCsv("http://host", "tok", "2025-02-03");
  assert.equal(filename, "statement-2025-02-03.csv");
});

test("downloadStatementCsv surfaces non-2xx as an Error with .status", async () => {
  globalThis.fetch = async () => ({
    ok: false,
    status: 401,
    headers: { get: () => null },
    text: async () => "",
  });
  await assert.rejects(
    () => downloadStatementCsv("http://host", "tok", "2025-01-15"),
    (err) => err.status === 401 && /401/.test(err.message),
  );
});

test("triggerBlobDownload creates an object URL and clicks a synthetic anchor", async () => {
  // Stub URL.createObjectURL / revokeObjectURL — the module references
  // them via `URL` directly. dom-stub doesn't provide createElement, so
  // bolt on the minimum the function needs.
  const created = [];
  const revoked = [];
  globalThis.URL.createObjectURL = (blob) => {
    const u = `blob:fake/${created.length}`;
    created.push({ url: u, blob });
    return u;
  };
  globalThis.URL.revokeObjectURL = (u) => { revoked.push(u); };

  const clicks = [];
  const anchors = [];
  const fakeBody = {
    appendChild(node) { node.parentNode = fakeBody; return node; },
    removeChild(node) { node.parentNode = null; return node; },
  };
  document.body = fakeBody;
  document.createElement = (tag) => {
    const a = {
      tagName: String(tag).toUpperCase(),
      href: "", download: "",
      click() { clicks.push({ href: this.href, download: this.download }); },
      parentNode: null,
    };
    anchors.push(a);
    return a;
  };

  const { triggerBlobDownload } = await import("../js/historyUi.js");
  triggerBlobDownload(new Blob(["abc"]), "statement-2025-01-15.csv");
  assert.equal(created.length, 1, "URL.createObjectURL was called");
  assert.equal(clicks.length, 1, "anchor click was synthesised");
  assert.equal(clicks[0].download, "statement-2025-01-15.csv");
  assert.equal(clicks[0].href, created[0].url);
});

test("openStatementJsonModal serialises into the modal body and unhides it", async () => {
  const { openStatementJsonModal, closeStatementJsonModal } = await import("../js/historyUi.js");
  const modal = document.getElementById("statement-json-modal");
  const body  = document.getElementById("statement-json-body");
  assert.equal(modal.hidden, true);
  openStatementJsonModal({ dayKey: "2025-01-15", positions: [{ symbol: "PETR4", netQty: 100 }] });
  assert.equal(modal.hidden, false);
  assert.match(body.textContent, /2025-01-15/);
  assert.match(body.textContent, /PETR4/);
  closeStatementJsonModal();
  assert.equal(modal.hidden, true);
  assert.equal(body.textContent, "");
});
