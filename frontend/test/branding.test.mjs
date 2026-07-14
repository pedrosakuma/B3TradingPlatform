import { test } from "node:test";
import assert from "node:assert/strict";

import { DEFAULT_APP_TITLE, applyAppTitle, configuredAppTitle, traderDocumentTitle } from "../js/branding.js";

class FakeTextNode {
  constructor(text = "") {
    this.nodeType = 3;
    this.textContent = text;
  }
}

class FakeElement {
  constructor({ textContent = "", childNodes = [] } = {}) {
    this.textContent = textContent;
    this.childNodes = childNodes;
    this.firstChild = childNodes[0] ?? null;
  }
  insertBefore(node, before) {
    const idx = before ? this.childNodes.indexOf(before) : -1;
    if (idx >= 0) this.childNodes.splice(idx, 0, node);
    else this.childNodes.unshift(node);
    this.firstChild = this.childNodes[0] ?? null;
    return node;
  }
}

function makeDocument({ loginHeading, brand }) {
  return {
    title: "",
    createTextNode: (text) => new FakeTextNode(text),
    querySelector: (selector) => {
      if (selector === "#login-form h1") return loginHeading ?? null;
      if (selector === ".brand") return brand ?? null;
      return null;
    },
  };
}

test("configuredAppTitle falls back when config is missing or blank", () => {
  assert.equal(configuredAppTitle(), DEFAULT_APP_TITLE);
  assert.equal(configuredAppTitle({ appTitle: "" }), DEFAULT_APP_TITLE);
  assert.equal(configuredAppTitle({ appTitle: "   " }), DEFAULT_APP_TITLE);
});

test("configuredAppTitle preserves configured titles", () => {
  assert.equal(configuredAppTitle({ appTitle: "Acme Trader" }), "Acme Trader");
  assert.equal(traderDocumentTitle("Acme Trader"), "Acme Trader — Trader");
});

test("applyAppTitle updates the document title, login heading, and only the brand text node", () => {
  const loginHeading = new FakeElement({ textContent: "B3TradingPlatform" });
  const pill = { nodeType: 1, textContent: "trader" };
  const brandText = new FakeTextNode("B3TradingPlatform ");
  const brand = new FakeElement({ childNodes: [brandText, pill] });
  const doc = makeDocument({ loginHeading, brand });

  applyAppTitle(doc, { appTitle: "Acme Trader" });

  assert.equal(doc.title, "Acme Trader — Trader");
  assert.equal(loginHeading.textContent, "Acme Trader");
  assert.equal(brand.childNodes[0].textContent, "Acme Trader ");
  assert.equal(brand.childNodes[1].textContent, "trader");
});

test("applyAppTitle inserts a leading text node when brand markup has only the pill child", () => {
  const brand = new FakeElement({ childNodes: [{ nodeType: 1, textContent: "trader" }] });
  const doc = makeDocument({ brand });

  applyAppTitle(doc, {});

  assert.equal(doc.title, "B3TradingPlatform — Trader");
  assert.equal(brand.childNodes[0].textContent, "B3TradingPlatform ");
  assert.equal(brand.childNodes[1].textContent, "trader");
});
