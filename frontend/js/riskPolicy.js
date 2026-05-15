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

// Monotonic per-session generation token. Every applyRiskPolicyFetch()
// call snapshots this counter BEFORE awaiting the network; on
// resolution it re-checks and silently drops its result if the counter
// has moved on. clearAll() (state.js) calls bumpRiskPolicyGeneration()
// on session boundaries (logout / WS reconnect), so an in-flight load
// from a prior session can never overwrite — or null out — the new
// session's policy.
let _policyGeneration = 0;

export function bumpRiskPolicyGeneration() {
  _policyGeneration += 1;
  return _policyGeneration;
}

// Test seam: lets a test reset the once-only warn latch between cases
// without monkey-patching console.
export function _resetRiskPolicyWarnedForTests() {
  warned = false;
}

export async function applyRiskPolicyFetch({ fetchPolicy, setRiskPolicy, warn = console.warn }) {
  // Capture the generation BEFORE the up-front clear and the await so
  // that any session boundary crossed while the fetch is in flight
  // invalidates this call's result on resolution.
  const myGen = _policyGeneration;
  // Drop any prior policy snapshot up-front so an in-flight fetch on a
  // fresh session never validates the new trader's ticket against the
  // previous backend's cap. Readers fall back to the documented 30d
  // client-side default until the fetch resolves. setRiskPolicy(null)
  // notifies the "riskPolicy" slice so the ticket re-validates.
  setRiskPolicy(null);
  try {
    const policy = await fetchPolicy();
    // Stale-response guard: if the generation moved while we were
    // awaiting, a newer session has already taken ownership of the
    // riskPolicy slice. Drop our result on the floor — applying it
    // (even as null) would clobber the newer session's loaded policy.
    if (myGen !== _policyGeneration) return;
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
    if (myGen !== _policyGeneration) return;
    setRiskPolicy(null);
    if (!warned) {
      warn("risk-policy fetch failed; using FE default", err);
      warned = true;
    }
  }
}
