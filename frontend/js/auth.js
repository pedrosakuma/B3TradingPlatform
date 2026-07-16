import { validateEntraConfig } from "./authConfig.js";

const AUTH_RESPONSE_PARAMS = new Set([
  "code", "state", "session_state", "client_info", "error", "error_description", "error_uri",
]);

let interactionRequiredCtor = null;
async function loadMsalBrowser() {
  const mod = await import("@azure/msal-browser");
  interactionRequiredCtor = mod.InteractionRequiredAuthError;
  return mod;
}

function accountFrom(instance) {
  return instance.getActiveAccount?.()
    ?? instance.getAllAccounts?.()?.[0]
    ?? null;
}

export function isInteractionRequiredError(error) {
  if (interactionRequiredCtor && error instanceof interactionRequiredCtor) return true;
  const code = String(error?.errorCode || error?.error || error?.subError || error?.name || "").toLowerCase();
  return ["interaction_required", "login_required", "consent_required", "no_account_in_silent_request", "interactionrequiredautherror"].includes(code);
}

export function scrubAuthResponseFromUrl(win = globalThis.window) {
  if (!win?.location || !win?.history?.replaceState) return;
  const url = new URL(win.location.href);
  let changed = false;
  for (const key of [...url.searchParams.keys()]) {
    if (AUTH_RESPONSE_PARAMS.has(key)) {
      url.searchParams.delete(key);
      changed = true;
    }
  }

  const hash = url.hash || "";
  if (/^#(?:code|state|session_state|client_info|error|id_token|access_token)=/i.test(hash)) {
    url.hash = "";
    changed = true;
  }

  if (changed) {
    win.history.replaceState(win.history.state ?? null, "", `${url.pathname}${url.search}${url.hash}`);
  }
}

export function createEntraAuth(authConfig, deps = {}) {
  validateEntraConfig(authConfig);
  const testFactory = globalThis.window?.__B3_TEST_MSAL__?.createPublicClientApplication;
  const msalFactory = deps.msalFactory
    ?? testFactory
    ?? (async (config) => {
      const { PublicClientApplication } = await loadMsalBrowser();
      return new PublicClientApplication(config);
    });
  const win = deps.window ?? globalThis.window;
  const scopes = [authConfig.apiScope];
  const msalConfig = {
    auth: {
      clientId: authConfig.clientId,
      authority: authConfig.authority,
      knownAuthorities: authConfig.knownAuthorities,
      redirectUri: authConfig.redirectUri,
      postLogoutRedirectUri: authConfig.logoutUri,
      navigateToLoginRequestUrl: false,
    },
    cache: {
      cacheLocation: "sessionStorage",
      temporaryCacheLocation: "sessionStorage",
      storeAuthStateInCookie: false,
    },
    system: {
      allowNativeBroker: false,
    },
  };

  let instance = null;
  let initialized = false;
  async function getInstance() {
    if (!instance) instance = await msalFactory(msalConfig);
    return instance;
  }

  async function initialize() {
    if (initialized) return;
    const app = await getInstance();
    if (typeof app.initialize === "function") await app.initialize();
    if (typeof app.enableAccountStorageEvents === "function") app.enableAccountStorageEvents();
    initialized = true;
  }

  async function handleRedirectPromise() {
    await initialize();
    const app = await getInstance();
    try {
      const result = await app.handleRedirectPromise();
      if (result?.account && typeof app.setActiveAccount === "function") {
        app.setActiveAccount(result.account);
      }
      scrubAuthResponseFromUrl(win);
      return result?.accessToken ? { accessToken: result.accessToken, account: result.account ?? accountFrom(app) } : null;
    } catch (error) {
      scrubAuthResponseFromUrl(win);
      throw error;
    }
  }

  async function loginRedirect(extra = {}) {
    await initialize();
    const app = await getInstance();
    return app.loginRedirect({
      scopes,
      redirectUri: authConfig.redirectUri,
      redirectStartPage: win?.location?.href,
      ...extra,
    });
  }

  async function acquireTokenSilent() {
    await initialize();
    const app = await getInstance();
    const account = accountFrom(app);
    if (!account) {
      await loginRedirect();
      return { redirected: true };
    }
    try {
      const result = await app.acquireTokenSilent({ account, scopes, redirectUri: authConfig.redirectUri });
      if (result?.account && typeof app.setActiveAccount === "function") app.setActiveAccount(result.account);
      if (!result?.accessToken) throw new Error("Entra did not return an access token.");
      return { accessToken: result.accessToken, account: result.account ?? account };
    } catch (error) {
      if (isInteractionRequiredError(error)) {
        await loginRedirect();
        return { redirected: true };
      }
      throw error;
    }
  }

  async function logoutRedirect() {
    await initialize();
    const app = await getInstance();
    const account = accountFrom(app);
    return app.logoutRedirect({
      account,
      postLogoutRedirectUri: authConfig.logoutUri,
    });
  }

  async function clearCache() {
    await initialize();
    const app = await getInstance();
    if (typeof app.clearCache !== "function") return;
    const account = accountFrom(app);
    await app.clearCache({ account });
  }

  return Object.freeze({
    initialize,
    handleRedirectPromise,
    loginRedirect,
    acquireTokenSilent,
    clearCache,
    logoutRedirect,
    _getInstance: getInstance,
  });
}
