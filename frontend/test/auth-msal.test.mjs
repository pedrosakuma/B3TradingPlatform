import test from "node:test";
import assert from "node:assert/strict";

import { createEntraAuth, scrubAuthResponseFromUrl } from "../js/auth.js";

function authConfig() {
  return {
    authority: "https://tenant.ciamlogin.com/tenant/v2.0",
    clientId: "spa-client",
    apiScope: "api://trading/access_as_user",
    redirectUri: "https://app.example/",
    logoutUri: "https://app.example/logout",
    knownAuthorities: ["tenant.ciamlogin.com"],
  };
}

function fakeWindow(url = "https://app.example/?code=abc&state=opaque#ignored") {
  const calls = [];
  return {
    location: new URL(url),
    history: {
      state: { ok: true },
      replaceState: (...args) => calls.push(args),
    },
    calls,
  };
}

test("MSAL wrapper uses sessionStorage cache and delegates redirect state handling", async () => {
  let capturedConfig;
  const instance = {
    initializeCalled: false,
    active: null,
    async initialize() { this.initializeCalled = true; },
    enableAccountStorageEvents() { this.storageEvents = true; },
    async handleRedirectPromise() { return { accessToken: "external", account: { homeAccountId: "a" } }; },
    setActiveAccount(account) { this.active = account; },
  };
  const win = fakeWindow();
  const auth = createEntraAuth(authConfig(), {
    window: win,
    msalFactory: (config) => { capturedConfig = config; return instance; },
  });
  const result = await auth.handleRedirectPromise();
  assert.equal(result.accessToken, "external");
  assert.equal(instance.initializeCalled, true);
  assert.equal(instance.storageEvents, true);
  assert.equal(instance.active.homeAccountId, "a");
  assert.equal(capturedConfig.cache.cacheLocation, "sessionStorage");
  assert.equal(capturedConfig.cache.temporaryCacheLocation, "sessionStorage");
  assert.equal(capturedConfig.auth.navigateToLoginRequestUrl, false);
  assert.equal(win.calls.length, 1);
  assert.equal(win.calls[0][2], "/#ignored");
});

test("silent token interaction-required falls back to loginRedirect", async () => {
  let loginRedirect = 0;
  const instance = {
    async initialize() {},
    getActiveAccount() { return { homeAccountId: "a" }; },
    async acquireTokenSilent() { const e = new Error("interaction"); e.errorCode = "interaction_required"; throw e; },
    async loginRedirect(req) { loginRedirect += 1; assert.deepEqual(req.scopes, ["api://trading/access_as_user"]); },
  };
  const auth = createEntraAuth(authConfig(), { window: fakeWindow("https://app.example/"), msalFactory: () => instance });
  const result = await auth.acquireTokenSilent();
  assert.equal(result.redirected, true);
  assert.equal(loginRedirect, 1);
});

test("logoutRedirect passes configured post-logout URI", async () => {
  let logoutRequest;
  const instance = {
    async initialize() {},
    getActiveAccount() { return { homeAccountId: "a" }; },
    async logoutRedirect(req) { logoutRequest = req; },
  };
  const auth = createEntraAuth(authConfig(), { window: fakeWindow("https://app.example/"), msalFactory: () => instance });
  await auth.logoutRedirect();
  assert.equal(logoutRequest.postLogoutRedirectUri, "https://app.example/logout");
  assert.equal(logoutRequest.account.homeAccountId, "a");
});

test("clearCache clears local MSAL cache without logout redirect", async () => {
  let clearRequest;
  let logoutCalled = false;
  const instance = {
    async initialize() {},
    getActiveAccount() { return { homeAccountId: "a" }; },
    async clearCache(req) { clearRequest = req; },
    async logoutRedirect() { logoutCalled = true; },
  };
  const auth = createEntraAuth(authConfig(), { window: fakeWindow("https://app.example/"), msalFactory: () => instance });
  await auth.clearCache();
  assert.equal(clearRequest.account.homeAccountId, "a");
  assert.equal(logoutCalled, false);
});

test("scrubAuthResponseFromUrl removes callback error hash", () => {
  const win = fakeWindow("https://app.example/#error=access_denied&state=abc");
  scrubAuthResponseFromUrl(win);
  assert.equal(win.calls[0][2], "/");
});
