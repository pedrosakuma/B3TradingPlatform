// Default (non-Docker) deploy-time configuration for the static frontend.
//
// When served via the frontend Docker image, this file is overwritten at
// container start by rendering env.js.template (see 20-render-env-js.sh),
// substituting JSON-escaped MARKETDATA_WS_URL and APP_TITLE. Outside Docker
// (e.g. opening index.html directly, or a plain static file server for
// local dev), this checked-in copy is served as-is with the historical
// empty market-data URL plus the default "B3TradingPlatform" title.
// protocol.js's defaultMarketDataUrl() still falls back to its
// localhost/127.0.0.1 dev guess, then "". See #572 / #596.
window.__B3_CONFIG__ = {
  marketDataWsUrl: "",
  appTitle: "B3TradingPlatform",
};
