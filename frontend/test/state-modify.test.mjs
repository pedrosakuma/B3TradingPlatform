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

// #381 — ExecKind.ReplaceRejected from the backend lands as an executions
// delta scoped to the OriginalClOrdId. applyExecutionsDelta must release
// the optimistic inflight-modify flag so the Modify button gets unstuck;
// without this the trader sees a permanently-spinning button after every
// venue replace-reject (no orders delta arrives — the order itself is
// unchanged by definition for a replace-reject).
test('applyExecutionsDelta(ReplaceRejected) clears inflightModifies for the original ClOrdID', async () => {
  const s = await freshState();
  s.markModifyInflight('42', true);
  assert.ok(s.getState().inflightModifies.has('42'));
  s.applyExecutionsDelta({
    clOrdId: '42', symbol: 'PETR4', side: 'Sell', status: 'Working',
    kind: 'ReplaceRejected', leavesQuantity: 100, cumulativeQuantity: 0,
    lastQuantity: 0, lastPrice: 0, rejectReason: 'reject_code=5',
    timestampUtc: new Date().toISOString(),
  });
  assert.ok(!s.getState().inflightModifies.has('42'));
  // Event is still appended to the executions log for the trader to see.
  assert.equal(s.getState().executions.length, 1);
  assert.equal(s.getState().executions[0].kind, 'ReplaceRejected');
});

test('applyExecutionsDelta with other kinds does NOT clear inflightModifies', async () => {
  const s = await freshState();
  s.markModifyInflight('42', true);
  s.applyExecutionsDelta({
    clOrdId: '42', symbol: 'PETR4', side: 'Sell', status: 'PartiallyFilled',
    kind: 'PartialFill', leavesQuantity: 80, cumulativeQuantity: 20,
    lastQuantity: 20, lastPrice: 30, rejectReason: null,
    timestampUtc: new Date().toISOString(),
  });
  // Spinner stays until the actual Replaced ack / explicit clear arrives.
  assert.ok(s.getState().inflightModifies.has('42'));
});
