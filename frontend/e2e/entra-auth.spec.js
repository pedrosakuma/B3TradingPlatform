import { expect, test } from "@playwright/test";

function b64url(obj) {
  return Buffer.from(JSON.stringify(obj)).toString("base64url");
}

function internalJwt(claims) {
  return `${b64url({ alg: "none", typ: "JWT" })}.${b64url({
    iss: "b3-trading",
    aud: "b3-trading-clients",
    exp: Math.floor(Date.now() / 1000) + 600,
    nbf: Math.floor(Date.now() / 1000) - 10,
    ...claims,
  })}.sig`;
}

async function serveConfig(page, auth) {
  await page.route("**/js/env.js", async (route) => {
    await route.fulfill({
      contentType: "application/javascript",
      body: `window.__B3_CONFIG__ = ${JSON.stringify({
        marketDataWsUrl: "",
        appTitle: "B3TradingPlatform",
        auth,
      })};`,
    });
  });
}

async function installFakeMsal(page, { redirectResult = null, silentResult = null } = {}) {
  await page.addInitScript(({ redirectResult, silentResult }) => {
    window.__B3_TEST_MSAL_LOG__ = [];
    window.__B3_TEST_MSAL__ = {
      createPublicClientApplication(config) {
        window.__B3_TEST_MSAL_CONFIG__ = config;
        let active = redirectResult?.account || silentResult?.account || { homeAccountId: "fake-account" };
        return {
          async initialize() { window.__B3_TEST_MSAL_LOG__.push("initialize"); },
          enableAccountStorageEvents() { window.__B3_TEST_MSAL_LOG__.push("storage-events"); },
          async handleRedirectPromise() { window.__B3_TEST_MSAL_LOG__.push("handleRedirectPromise"); return redirectResult; },
          setActiveAccount(account) { active = account; },
          getActiveAccount() { return active; },
          getAllAccounts() { return active ? [active] : []; },
          async loginRedirect(request) { window.__B3_TEST_MSAL_LOGIN__ = request; },
          async acquireTokenSilent() { window.__B3_TEST_MSAL_LOG__.push("acquireTokenSilent"); return silentResult || { accessToken: "silent-access", account: active }; },
          async logoutRedirect(request) { window.__B3_TEST_MSAL_LOGOUT__ = request; },
        };
      },
    };
  }, { redirectResult, silentResult });
}

const entraAuth = {
  mode: "Entra",
  authority: "https://tenant.ciamlogin.com/tenant/v2.0",
  clientId: "spa-client-id",
  apiScope: "api://trading/access_as_user",
  redirectUri: "http://localhost:8080/",
  logoutUri: "http://localhost:8080/",
  knownAuthorities: ["tenant.ciamlogin.com"],
};

test.describe("Entra External ID frontend harness", () => {
  test("MSAL redirect result exchanges for internal session and scrubs callback URL", async ({ page }) => {
    await serveConfig(page, entraAuth);
    await installFakeMsal(page, {
      redirectResult: { accessToken: "external-access", account: { homeAccountId: "entra-account", username: "person@example.com" } },
    });
    await page.route("**/auth/exchange", async (route) => {
      expect(route.request().headers().authorization).toBe("Bearer external-access");
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({
          token: internalJwt({ sub: "internal-alice", role: "admin", firm: "FIRM09" }),
          expiresAt: new Date(Date.now() + 600_000).toISOString(),
        }),
      });
    });

    await page.goto("/?code=abc&state=opaque&session_state=s#callback");
    await expect(page.locator("#trader-view")).toBeVisible();
    await expect(page.locator("#user-label")).toContainText("internal-alice");
    await expect(page.locator("#user-role")).toContainText("admin");
    expect(page.url()).not.toContain("code=abc");
    expect(page.url()).not.toContain("state=opaque");
    await expect(page.locator("#login-password")).toHaveCount(0);

    const storage = await page.evaluate(() => ({
      session: JSON.parse(sessionStorage.getItem("b3tp.session")),
      local: localStorage.getItem("b3tp.session"),
      msalConfig: window.__B3_TEST_MSAL_CONFIG__,
    }));
    expect(storage.session.username).toBe("internal-alice");
    expect(storage.session.authMode).toBe("Entra");
    expect(storage.session.remember).toBe(false);
    expect(storage.local).toBeNull();
    expect(storage.msalConfig.cache.cacheLocation).toBe("sessionStorage");
  });

  test("Hybrid mode shows explicit Entra and local choices without signup by default", async ({ page }) => {
    await serveConfig(page, { ...entraAuth, mode: "Hybrid" });
    await installFakeMsal(page);
    await page.goto("/");
    await expect(page.locator("#auth-choice")).toBeVisible();
    await expect(page.locator("#entra-login")).toBeVisible();
    await expect(page.locator("#auth-use-local")).toBeVisible();
    await page.click("#auth-use-local");
    await expect(page.locator("#login-username")).toBeVisible();
    await expect(page.locator("#login-signup-switch")).toBeHidden();
    await expect(page.locator(".remember")).toBeHidden();
  });

  test("account_not_provisioned is stable and accessible", async ({ page }) => {
    await serveConfig(page, entraAuth);
    await installFakeMsal(page, { redirectResult: { accessToken: "external-access", account: { homeAccountId: "a" } } });
    await page.route("**/auth/exchange", async (route) => {
      await route.fulfill({ status: 403, contentType: "application/json", body: JSON.stringify({ error: "account_not_provisioned" }) });
    });
    await page.goto("/?code=abc&state=opaque");
    const alert = page.locator("#auth-error[role='alert']");
    await expect(alert).toBeVisible();
    await expect(alert).toContainText("not provisioned");
    await expect(page.locator("#signup-form")).toHaveCount(0);
  });

  test("logout clears internal state and delegates Entra logout once", async ({ page }) => {
    await serveConfig(page, entraAuth);
    await installFakeMsal(page, { redirectResult: { accessToken: "external-access", account: { homeAccountId: "a" } } });
    await page.route("**/auth/exchange", async (route) => {
      await route.fulfill({
        contentType: "application/json",
        body: JSON.stringify({
          token: internalJwt({ sub: "internal-bob", role: "user", firm: "FIRM01" }),
          expiresAt: new Date(Date.now() + 600_000).toISOString(),
        }),
      });
    });
    await page.goto("/?code=abc&state=opaque");
    await expect(page.locator("#trader-view")).toBeVisible();
    await page.click("#logout");
    await expect.poll(() => page.evaluate(() => window.__B3_TEST_MSAL_LOGOUT__?.postLogoutRedirectUri)).toBe("http://localhost:8080/");
    expect(await page.evaluate(() => sessionStorage.getItem("b3tp.session"))).toBeNull();
  });
});
