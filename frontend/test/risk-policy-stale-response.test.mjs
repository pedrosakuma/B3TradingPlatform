// Q1.4 (#256) — risk-policy stale-response invalidation tests.
//
// applyRiskPolicyFetch() snapshots a per-call generation token before
// awaiting the network. clearAll() (session boundary) bumps that token
// via bumpRiskPolicyGeneration(); on resolution, any call whose
// captured generation no longer matches the active one drops its
// result silently. This guards against a delayed response from a
// previous session overwriting the next session's loaded policy.

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({ ids: {} });

const state = await import("../js/state.js");
const {
  applyRiskPolicyFetch,
  bumpRiskPolicyGeneration,
  _resetRiskPolicyWarnedForTests,
} = await import("../js/riskPolicy.js");

function deferred() {
  let resolve, reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

test("stale success does not overwrite a newer session's policy", async () => {
  state.setRiskPolicy(null);
  _resetRiskPolicyWarnedForTests();

  const dA = deferred();
  // Fetch A (prior session), delayed.
  const pA = applyRiskPolicyFetch({
    fetchPolicy: () => dA.promise,
    setRiskPolicy: state.setRiskPolicy,
    warn: () => {},
  });

  // Session boundary — invalidates A.
  bumpRiskPolicyGeneration();

  // Fetch B (new session), resolves immediately.
  const pB = applyRiskPolicyFetch({
    fetchPolicy: async () => ({ maxGtdHorizonDays: 30 }),
    setRiskPolicy: state.setRiskPolicy,
    warn: () => {},
  });
  await pB;

  // Now release A with a different (would-be-clobbering) value.
  dA.resolve({ maxGtdHorizonDays: 90 });
  await pA;

  assert.deepEqual(state.getState().riskPolicy, { maxGtdHorizonDays: 30 },
    "stale success from prior session must not overwrite the newer policy");
});

test("stale rejection does not null out a newer session's policy", async () => {
  state.setRiskPolicy(null);
  _resetRiskPolicyWarnedForTests();

  const dA = deferred();
  const pA = applyRiskPolicyFetch({
    fetchPolicy: () => dA.promise,
    setRiskPolicy: state.setRiskPolicy,
    warn: () => {},
  });

  bumpRiskPolicyGeneration();

  const pB = applyRiskPolicyFetch({
    fetchPolicy: async () => ({ maxGtdHorizonDays: 45 }),
    setRiskPolicy: state.setRiskPolicy,
    warn: () => {},
  });
  await pB;

  dA.reject(new Error("network gone with old session"));
  await pA;

  assert.deepEqual(state.getState().riskPolicy, { maxGtdHorizonDays: 45 },
    "stale rejection must not clobber the newer session's policy back to null");
});

test("stale malformed payload does not null out a newer session's policy", async () => {
  state.setRiskPolicy(null);
  _resetRiskPolicyWarnedForTests();

  const dA = deferred();
  const pA = applyRiskPolicyFetch({
    fetchPolicy: () => dA.promise,
    setRiskPolicy: state.setRiskPolicy,
    warn: () => {},
  });

  bumpRiskPolicyGeneration();

  const pB = applyRiskPolicyFetch({
    fetchPolicy: async () => ({ maxGtdHorizonDays: 60 }),
    setRiskPolicy: state.setRiskPolicy,
    warn: () => {},
  });
  await pB;

  dA.resolve({}); // malformed
  await pA;

  assert.deepEqual(state.getState().riskPolicy, { maxGtdHorizonDays: 60 },
    "stale malformed payload must not clobber the newer session's policy");
});

test("clearAll() during an in-flight fetch invalidates the response", async () => {
  state.setRiskPolicy(null);
  _resetRiskPolicyWarnedForTests();

  const dA = deferred();
  const pA = applyRiskPolicyFetch({
    fetchPolicy: () => dA.promise,
    setRiskPolicy: state.setRiskPolicy,
    warn: () => {},
  });

  // Session boundary mid-flight — clearAll bumps the generation.
  state.clearAll();

  // The in-flight fetch finally resolves with the prior session's data.
  dA.resolve({ maxGtdHorizonDays: 90 });
  await pA;

  assert.equal(state.getState().riskPolicy, null,
    "clearAll() must invalidate the in-flight load so its result can't repopulate");
});
