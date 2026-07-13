// Default (non-Docker) deploy-time configuration for the static frontend.
//
// When served via the frontend Docker image, this file is overwritten at
// container start by rendering env.js.template with envsubst (see
// 20-render-env-js.sh), substituting MARKETDATA_WS_URL. Outside Docker
// (e.g. opening index.html directly, or a plain static file server for
// local dev), this checked-in copy is served as-is with an empty default,
// which preserves today's behavior: js/protocol.js's
// defaultMarketDataUrl() falls back to its localhost/127.0.0.1 dev guess,
// then "". See #572.
window.__B3_CONFIG__ = {
  marketDataWsUrl: "",
};
