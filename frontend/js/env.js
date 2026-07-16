// Default (non-Docker) deploy-time configuration for the static frontend.
// Docker renders frontend/env.js.template at container start. Local remains
// the compatibility default: password/TOTP/signup behavior is unchanged until
// AUTH_MODE is set to Hybrid or Entra in the container environment.
window.__B3_CONFIG__ = {
  marketDataWsUrl: "",
  appTitle: "B3TradingPlatform",
  auth: {
    mode: "Local",
    localLoginEnabled: true,
    signupEnabled: true,
    totpEnabled: true,
    authority: "",
    clientId: "",
    apiScope: "",
    redirectUri: "",
    logoutUri: "",
    knownAuthorities: [],
  },
};
