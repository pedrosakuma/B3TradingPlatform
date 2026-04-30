// Bootstrap placeholder. The real trader UI will live here, mirroring the
// vanilla-JS + Web Worker architecture of B3MarketDataPlatform/frontend.
//
// For now this just reports backend health so the page has a heartbeat.
const backend = (location.hostname === "localhost" || location.hostname === "127.0.0.1")
  ? "http://localhost:5000"
  : "";

async function pingHealth() {
  try {
    const res = await fetch(`${backend}/health`);
    console.log("[health]", res.status, await res.text());
  } catch (err) {
    console.warn("[health] backend unreachable", err);
  }
}

pingHealth();
