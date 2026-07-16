const AUTH_MODES = new Set(["Local", "Hybrid", "Entra"]);

function cleanString(value) {
  return typeof value === "string" ? value.trim() : "";
}

function boolOrDefault(value, fallback) {
  if (value === true || value === false) return value;
  if (typeof value === "string") {
    const normalized = value.trim().toLowerCase();
    if (["1", "true", "yes", "on"].includes(normalized)) return true;
    if (["0", "false", "no", "off"].includes(normalized)) return false;
  }
  return fallback;
}

function list(value) {
  if (Array.isArray(value)) {
    return value.map(cleanString).filter(Boolean);
  }
  if (typeof value === "string") {
    return value.split(",").map(cleanString).filter(Boolean);
  }
  return [];
}

function defaultUrl(win) {
  const loc = win?.location;
  if (!loc || !loc.origin) return "";
  return `${loc.origin}${loc.pathname || "/"}`;
}

export function normalizeAuthConfig(raw = {}, win = globalThis.window) {
  const modeRaw = cleanString(raw.mode ?? raw.authMode) || "Local";
  const mode = AUTH_MODES.has(modeRaw) ? modeRaw :
    [...AUTH_MODES].find((candidate) => candidate.toLowerCase() === modeRaw.toLowerCase()) ?? "Local";

  const entraEnabled = mode === "Hybrid" || mode === "Entra";
  const localLoginEnabled = boolOrDefault(
    raw.localLoginEnabled,
    mode === "Entra" ? false : true,
  );
  const signupEnabled = boolOrDefault(
    raw.signupEnabled,
    mode === "Local",
  );
  const totpEnabled = boolOrDefault(
    raw.totpEnabled,
    mode !== "Entra" && localLoginEnabled,
  );

  const redirectUri = cleanString(raw.redirectUri) || defaultUrl(win);
  const logoutUri = cleanString(raw.logoutUri) || redirectUri;
  const knownAuthorities = list(raw.knownAuthorities);
  const authority = cleanString(raw.authority);

  return Object.freeze({
    mode,
    entraEnabled,
    localLoginEnabled: mode === "Entra" ? false : localLoginEnabled,
    signupEnabled: mode === "Entra" ? false : signupEnabled,
    totpEnabled: mode === "Entra" ? false : totpEnabled,
    authority,
    clientId: cleanString(raw.clientId),
    apiScope: cleanString(raw.apiScope),
    redirectUri,
    logoutUri,
    knownAuthorities: Object.freeze(knownAuthorities),
  });
}

export function readPublicConfig(win = globalThis.window) {
  const raw = win?.__B3_CONFIG__ ?? {};
  const authRaw = raw.auth && typeof raw.auth === "object" ? raw.auth : raw;
  return Object.freeze({
    marketDataWsUrl: cleanString(raw.marketDataWsUrl),
    appTitle: cleanString(raw.appTitle) || "B3TradingPlatform",
    auth: normalizeAuthConfig(authRaw, win),
  });
}

export function validateEntraConfig(auth) {
  const missing = [];
  if (!cleanString(auth?.authority)) missing.push("authority");
  if (!cleanString(auth?.clientId)) missing.push("client ID");
  if (!cleanString(auth?.apiScope)) missing.push("API scope");
  if (!cleanString(auth?.redirectUri)) missing.push("redirect URI");
  if (missing.length > 0) {
    throw new Error(`Entra login is not configured: missing ${missing.join(", ")}.`);
  }
}

export function authModeLabel(auth) {
  if (auth?.mode === "Entra") return "Microsoft Entra";
  if (auth?.mode === "Hybrid") return "Hybrid";
  return "Local";
}
