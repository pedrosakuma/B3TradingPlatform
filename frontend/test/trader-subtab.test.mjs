// Fase 4 (#400). Trader sub-tab + lower-band + ticket-advanced state +
// hash router. Run: `node --test frontend/test/trader-subtab.test.mjs`.

import { test } from 'node:test';
import assert from 'node:assert/strict';

let n = 0;
async function freshState() {
  n += 1;
  return await import(`../js/state.js?bust-trader=${n}`);
}

// ── state slices ──────────────────────────────────────────────────

test('traderSubTab defaults to markets', async () => {
  const state = await freshState();
  assert.equal(state.getState().traderSubTab, 'markets');
});

test('setTraderSubTab accepts the three known sub-tabs', async () => {
  const state = await freshState();
  for (const sub of ['watchlist', 'auctions', 'markets']) {
    state.setTraderSubTab(sub);
    assert.equal(state.getState().traderSubTab, sub);
  }
});

test('setTraderSubTab ignores unknown names', async () => {
  const state = await freshState();
  state.setTraderSubTab('watchlist');
  state.setTraderSubTab('not-a-real-tab');
  state.setTraderSubTab('');
  state.setTraderSubTab(null);
  assert.equal(state.getState().traderSubTab, 'watchlist');
});

test('setTraderSubTab notifies subscribers exactly once per change', async () => {
  const state = await freshState();
  let hits = 0;
  state.subscribe((slice) => {
    if (slice === 'traderSubTab') hits += 1;
  });
  state.setTraderSubTab('watchlist');
  state.setTraderSubTab('watchlist');           // dedupe
  state.setTraderSubTab('auctions');
  state.setTraderSubTab('unknown');              // ignored
  assert.equal(hits, 2);
});

test('traderBottomTab defaults to blotter', async () => {
  const state = await freshState();
  assert.equal(state.getState().traderBottomTab, 'blotter');
});

test('setTraderBottomTab accepts blotter/executions only', async () => {
  const state = await freshState();
  state.setTraderBottomTab('executions');
  assert.equal(state.getState().traderBottomTab, 'executions');
  state.setTraderBottomTab('blotter');
  assert.equal(state.getState().traderBottomTab, 'blotter');
  state.setTraderBottomTab('positions'); // not a valid lower-band tab
  assert.equal(state.getState().traderBottomTab, 'blotter');
});

test('setTraderBottomTab dedupes and notifies once per change', async () => {
  const state = await freshState();
  let hits = 0;
  state.subscribe((slice) => {
    if (slice === 'traderBottomTab') hits += 1;
  });
  state.setTraderBottomTab('executions');
  state.setTraderBottomTab('executions');
  state.setTraderBottomTab('blotter');
  assert.equal(hits, 2);
});

test('ticketAdvancedOpen defaults to false and toggles cleanly', async () => {
  const state = await freshState();
  assert.equal(state.getState().ticketAdvancedOpen, false);
  let hits = 0;
  state.subscribe((slice) => {
    if (slice === 'ticketAdvancedOpen') hits += 1;
  });
  state.setTicketAdvancedOpen(true);
  state.setTicketAdvancedOpen(true);   // dedupe
  state.setTicketAdvancedOpen(false);
  assert.equal(state.getState().ticketAdvancedOpen, false);
  assert.equal(hits, 2);
  // Coerces truthy/falsy to bool without notifying when already at that value.
  state.setTicketAdvancedOpen(1);
  assert.equal(state.getState().ticketAdvancedOpen, true);
});

// ── hash router (#400 schema extension) ────────────────────────────

import { parseHashRoute, hashForView, TRADER_SUB_TABS } from '../js/hashRouter.js';

test('parseHashRoute resolves Trader + sub-tab', () => {
  assert.deepEqual(parseHashRoute('#trading'),
                   { view: 'trader', subTab: null });
  assert.deepEqual(parseHashRoute('#trading/markets'),
                   { view: 'trader', subTab: 'markets' });
  assert.deepEqual(parseHashRoute('#trading/watchlist'),
                   { view: 'trader', subTab: 'watchlist' });
  assert.deepEqual(parseHashRoute('#trading/auctions'),
                   { view: 'trader', subTab: 'auctions' });
});

test('parseHashRoute accepts the legacy #trader alias', () => {
  // #trader → trader (the canonical hash is #trading; this alias keeps
  // any bookmark / hand-typed URL from breaking).
  assert.deepEqual(parseHashRoute('#trader'),
                   { view: 'trader', subTab: null });
  assert.deepEqual(parseHashRoute('#trader/markets'),
                   { view: 'trader', subTab: 'markets' });
});

test('parseHashRoute rejects invalid trader sub-tabs', () => {
  assert.deepEqual(parseHashRoute('#trading/ghost'),  { view: null, subTab: null });
  assert.deepEqual(parseHashRoute('#trading/'),       { view: null, subTab: null });
});

test('hashForView serialises trader + sub-tab', () => {
  assert.equal(hashForView('trader'),                '#trading');
  assert.equal(hashForView('trader', 'markets'),     '#trading/markets');
  assert.equal(hashForView('trader', 'watchlist'),   '#trading/watchlist');
  assert.equal(hashForView('trader', 'auctions'),    '#trading/auctions');
  assert.equal(hashForView('trader', 'bogus'),       '#trading');
});

test('parseHashRoute ↔ hashForView round-trips every trader sub-tab', () => {
  for (const sub of TRADER_SUB_TABS) {
    const hash = hashForView('trader', sub);
    assert.deepEqual(parseHashRoute(hash), { view: 'trader', subTab: sub });
  }
});
