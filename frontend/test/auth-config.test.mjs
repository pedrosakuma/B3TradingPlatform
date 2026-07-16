import test from "node:test";
import assert from "node:assert/strict";

import { normalizeAuthConfig, readPublicConfig, validateEntraConfig } from "../js/authConfig.js";

const win = { location: { origin: "https://trader.example", pathname: "/console" } };

test("auth config defaults to Local compatibility mode", () => {
  const cfg = normalizeAuthConfig({}, win);
  assert.equal(cfg.mode, "Local");
  assert.equal(cfg.localLoginEnabled, true);
  assert.equal(cfg.signupEnabled, true);
  assert.equal(cfg.entraEnabled, false);
});

test("Hybrid config keeps explicit local and Entra choices", () => {
  const cfg = normalizeAuthConfig({
    mode: "Hybrid",
    authority: "https://tenant.ciamlogin.com/id/v2.0",
    clientId: "spa-client",
    apiScope: "api://trading/access_as_user",
    knownAuthorities: "tenant.ciamlogin.com, login.contoso.example",
  }, win);
  assert.equal(cfg.entraEnabled, true);
  assert.equal(cfg.localLoginEnabled, true);
  assert.equal(cfg.signupEnabled, false);
  assert.deepEqual(cfg.knownAuthorities, ["tenant.ciamlogin.com", "login.contoso.example"]);
  assert.equal(cfg.redirectUri, "https://trader.example/console");
});

test("Entra mode disables local signup and password controls by default", () => {
  const cfg = normalizeAuthConfig({ mode: "Entra", signupEnabled: true, localLoginEnabled: true }, win);
  assert.equal(cfg.localLoginEnabled, false);
  assert.equal(cfg.signupEnabled, false);
  assert.equal(cfg.totpEnabled, false);
});

test("validateEntraConfig requires public OAuth config but no secret", () => {
  assert.throws(() => validateEntraConfig(normalizeAuthConfig({ mode: "Entra" }, win)), /missing authority/);
  assert.doesNotThrow(() => validateEntraConfig(normalizeAuthConfig({
    mode: "Entra",
    authority: "https://tenant.ciamlogin.com/id/v2.0",
    clientId: "spa-client",
    apiScope: "api://trading/access_as_user",
  }, win)));
});

test("readPublicConfig supports nested auth config", () => {
  const cfg = readPublicConfig({
    location: win.location,
    __B3_CONFIG__: {
      marketDataWsUrl: "ws://md.example/ws",
      appTitle: "Desk",
      auth: { mode: "Entra", authority: "https://a", clientId: "c", apiScope: "s" },
    },
  });
  assert.equal(cfg.appTitle, "Desk");
  assert.equal(cfg.auth.mode, "Entra");
});
