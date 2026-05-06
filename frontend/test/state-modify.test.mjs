// Slice 5 of #122 — markModifyInflight reducer tests for state.js.
// Runs with `node --test frontend/test/state-modify.test.mjs`.
//
// Coverage: idempotency, set/clear flips notify("orders"), clearAll
// resets the set, and inflightModifies starts empty.

import { test } from 'node:test';
import assert from 'node:assert/strict';

let n = 0;
async function freshState() {
  n += 1;
  return await import(`../js/state.js?bust=${n}`);
}

test('inflightModifies starts empty', async () => {
  const s = await freshState();
  const st = s.getState();
  assert.ok(st.inflightModifies instanceof Set);
  assert.equal(st.inflightModifies.size, 0);
});

test('markModifyInflight(true) adds to the set, false removes', async () => {
  const s = await freshState();
  s.markModifyInflight('123', true);
  assert.ok(s.getState().inflightModifies.has('123'));
  s.markModifyInflight('123', false);
  assert.ok(!s.getState().inflightModifies.has('123'));
});

test('markModifyInflight is idempotent and no-ops on falsy ClOrdID', async () => {
  const s = await freshState();
  s.markModifyInflight('A', true);
  s.markModifyInflight('A', true);
  assert.equal(s.getState().inflightModifies.size, 1);
  s.markModifyInflight('', true);
  s.markModifyInflight(null, true);
  s.markModifyInflight(undefined, true);
  assert.equal(s.getState().inflightModifies.size, 1);
});

test('markModifyInflight notifies the "orders" slice on flip only', async () => {
  const s = await freshState();
  let calls = 0;
  s.subscribe((slice) => { if (slice === 'orders') calls += 1; });
  s.markModifyInflight('A', true);   // flip
  s.markModifyInflight('A', true);   // no-op
  s.markModifyInflight('A', false);  // flip
  s.markModifyInflight('A', false);  // no-op
  assert.equal(calls, 2);
});

test('clearAll() empties inflightModifies', async () => {
  const s = await freshState();
  s.markModifyInflight('A', true);
  s.markModifyInflight('B', true);
  s.clearAll();
  assert.equal(s.getState().inflightModifies.size, 0);
});
