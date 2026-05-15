// Q1.4 (#256). Risk-policy loader, factored out of app.js so the
// rejected-fetch / malformed-payload branches can be exercised by
// node:test without standing up the full app entry point (which runs
// init() on import and needs the real DOM).
//
// Production passes a closure over getRiskPolicy + the active session;
// tests pass a stub fetcher that resolves/rejects to the shape they
// want to cover. setRiskPolicy is also injected so the module stays
// dependency-free at import time.

let warned = false;

// Test seam: lets a test reset the once-only warn latch between cases
// without monkey-patching console.
export function _resetRiskPolicyWarnedForTests() {
  warned = false;
}

export async function applyRiskPolicyFetch({ fetchPolicy, setRiskPolicy, warn = console.warn }) {
  // Drop any prior policy snapshot up-front so an in-flight fetch on a
  // fresh session never validates the new trader's ticket against the
  // previous backend's cap. Readers fall back to the documented 30d
  // client-side default until the fetch resolves. setRiskPolicy(null)
  // notifies the "riskPolicy" slice so the ticket re-validates.
  setRiskPolicy(null);
  try {
    const policy = await fetchPolicy();
    const days = Number(policy?.maxGtdHorizonDays);
    if (Number.isFinite(days) && days > 0) {
      setRiskPolicy({ maxGtdHorizonDays: days });
      return;
    }
    // Malformed payload — make sure we don't leave a stale value
    // around (paranoia: setRiskPolicy(null) above already cleared it,
    // but a future refactor that drops the up-front clear must still
    // land on the documented fallback).
    setRiskPolicy(null);
    if (!warned) {
      warn("risk-policy fetch returned malformed payload; using FE default", policy);
      warned = true;
    }
  } catch (err) {
    setRiskPolicy(null);
    if (!warned) {
      warn("risk-policy fetch failed; using FE default", err);
      warned = true;
    }
  }
}
