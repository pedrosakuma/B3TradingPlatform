import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs/promises";

import {
  MD_KEY,
  readMdConnectionConfig,
  readMdDisplayConfig,
  writeMdConfig,
} from "../js/marketDataSettings.js";

function withWindowConfig(config, fn) {
  const prevWindow = globalThis.window;
  globalThis.window = { __B3_CONFIG__: config };
  try {
    return fn();
  } finally {
    globalThis.window = prevWindow;
  }
}

function makeStorage(seed = {}) {
  const data = new Map(Object.entries(seed));
  return {
    getItem(key) { return data.has(key) ? data.get(key) : null; },
    setItem(key, value) { data.set(key, String(value)); },
    removeItem(key) { data.delete(key); },
  };
}

test("readMdConnectionConfig ignores stored URL overrides and uses deploy-time config", () => {
  const storage = makeStorage({
    [MD_KEY]: JSON.stringify({ url: "ws://evil.example/ws", symbols: ["ABEV3"] }),
  });

  withWindowConfig({ marketDataWsUrl: "wss://marketdata.example/ws" }, () => {
    assert.deepEqual(readMdConnectionConfig(storage, ["PETR4"]), {
      url: "wss://marketdata.example/ws",
      symbols: ["ABEV3"],
    });
  });
});

test("readMdConnectionConfig keeps the localhost dev fallback when no deploy-time URL is set", () => {
  const prevLocation = globalThis.location;
  globalThis.location = { protocol: "http:", hostname: "localhost" };
  try {
    withWindowConfig({ marketDataWsUrl: "" }, () => {
      assert.deepEqual(readMdConnectionConfig(makeStorage(), ["PETR4"]), {
        url: "ws://localhost:8081/ws",
        symbols: ["PETR4"],
      });
    });
  } finally {
    globalThis.location = prevLocation;
  }
});

test("readMdDisplayConfig shows the effective URL in localhost dev mode", () => {
  const prevLocation = globalThis.location;
  globalThis.location = { protocol: "http:", hostname: "127.0.0.1" };
  try {
    withWindowConfig({ marketDataWsUrl: "" }, () => {
      assert.deepEqual(readMdDisplayConfig(makeStorage(), ["VALE3"]), {
        url: "ws://127.0.0.1:8081/ws",
        symbols: ["VALE3"],
      });
    });
  } finally {
    globalThis.location = prevLocation;
  }
});

test("writeMdConfig persists only the watchlist symbols", () => {
  const storage = makeStorage();

  writeMdConfig(storage, ["PETR4", "VALE3"]);

  assert.equal(storage.getItem(MD_KEY), JSON.stringify({ symbols: ["PETR4", "VALE3"] }));
});

test("settings markup renders the market-data URL as readonly with a hint", async () => {
  const html = await fs.readFile(new URL("../index.html", import.meta.url), "utf8");
  assert.match(html, /<input id="md-url" type="url" readonly aria-describedby="md-url-hint" \/>/);
  assert.match(html, /<p id="md-url-hint" class="field-hint">Derived from deploy-time config \(or the localhost dev fallback\) — not user-editable\.<\/p>/);
});
