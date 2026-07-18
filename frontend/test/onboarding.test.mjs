import test from "node:test";
import assert from "node:assert/strict";

import {
  deriveFirstOrderProgress,
  readFirstOrderOnboarding,
  writeFirstOrderOnboarding,
} from "../js/onboarding.js";

function memoryStorage() {
  const values = new Map();
  return {
    getItem: (key) => values.get(key) ?? null,
    setItem: (key, value) => values.set(key, value),
  };
}

test("first-order progress follows the real connection, acceptance, and order state", () => {
  const state = { status: "disconnected", orders: new Map() };
  assert.equal(deriveFirstOrderProgress(state).stage, 0);

  state.status = "connected";
  assert.equal(deriveFirstOrderProgress(state).stage, 1);

  assert.equal(deriveFirstOrderProgress(state, "42").stage, 2);
  state.orders.set("42", { clOrdId: "42", status: "Working" });

  const completed = deriveFirstOrderProgress(state, "42");
  assert.equal(completed.stage, 3);
  assert.match(completed.message, /Working Orders/);
  assert.equal(completed.target, "blotter");
});

test("terminal first orders complete truthfully through the executions surface", () => {
  const state = {
    status: "connected",
    orders: new Map([["42", { clOrdId: "42", status: "Filled" }]]),
  };

  const completed = deriveFirstOrderProgress(state, "42");
  assert.equal(completed.stage, 3);
  assert.match(completed.message, /Filled/);
  assert.doesNotMatch(completed.message, /Working Orders/);
  assert.equal(completed.target, "executions");
});

test("onboarding persistence is scoped to each signed-in user", () => {
  const storage = memoryStorage();
  assert.equal(readFirstOrderOnboarding(storage, "alice"), null);
  assert.equal(writeFirstOrderOnboarding(storage, "alice", "dismissed"), true);
  assert.equal(readFirstOrderOnboarding(storage, "alice"), "dismissed");
  assert.equal(readFirstOrderOnboarding(storage, "bob"), null);
});

test("onboarding persistence accepts only terminal states", () => {
  const storage = memoryStorage();
  assert.equal(writeFirstOrderOnboarding(storage, "alice", "active"), false);
  assert.equal(readFirstOrderOnboarding(storage, "alice"), null);
  assert.equal(writeFirstOrderOnboarding(storage, "alice", "completed"), true);
  assert.equal(readFirstOrderOnboarding(storage, "alice"), "completed");
});
