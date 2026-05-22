// Fase 3 (#399). Settings sub-tab state + hash router.
// Run: `node --test frontend/test/settings-subtab.test.mjs`.

import { test } from 'node:test';
import assert from 'node:assert/strict';

let n = 0;
async function freshState() {
  n += 1;
  return await import(`../js/state.js?bust-settings=${n}`);
}

test('settingsSubTab defaults to bot-credentials', async () => {
  const state = await freshState();
  assert.equal(state.getState().settingsSubTab, 'bot-credentials');
});

test('setSettingsSubTab accepts the four known sub-tabs', async () => {
  const state = await freshState();
  for (const sub of ['security', 'market-data', 'preferences', 'bot-credentials']) {
    state.setSettingsSubTab(sub);
    assert.equal(state.getState().settingsSubTab, sub);
  }
});

test('setSettingsSubTab ignores unknown names', async () => {
  const state = await freshState();
  state.setSettingsSubTab('security');
  state.setSettingsSubTab('not-a-real-tab');
  state.setSettingsSubTab('');
  state.setSettingsSubTab(null);
  assert.equal(state.getState().settingsSubTab, 'security');
});

test('setSettingsSubTab notifies subscribers exactly once per change', async () => {
  const state = await freshState();
  let hits = 0;
  state.subscribe((slice) => {
    if (slice === 'settingsSubTab') hits += 1;
  });
  state.setSettingsSubTab('security');
  state.setSettingsSubTab('security');           // dedupe
  state.setSettingsSubTab('market-data');
  state.setSettingsSubTab('unknown');             // ignored
  assert.equal(hits, 2);
});

// ── parseHashRoute (#399 hash schema) ──────────────────────────────

import { parseHashRoute, hashForView, SETTINGS_SUB_TABS } from '../js/hashRouter.js';

test('parseHashRoute maps top-level hashes to their views', () => {
  assert.deepEqual(parseHashRoute('#trading'),    { view: 'trader',     subTab: null });
  assert.deepEqual(parseHashRoute('#algos'),      { view: 'algos',      subTab: null });
  assert.deepEqual(parseHashRoute('#history'),    { view: 'history',    subTab: null });
  assert.deepEqual(parseHashRoute('#admin'),      { view: 'admin',      subTab: null });
  assert.deepEqual(parseHashRoute('#compliance'), { view: 'compliance', subTab: null });
});

test('parseHashRoute resolves Settings + sub-tab', () => {
  assert.deepEqual(parseHashRoute('#settings'),
                   { view: 'settings', subTab: null });
  assert.deepEqual(parseHashRoute('#settings/security'),
                   { view: 'settings', subTab: 'security' });
  assert.deepEqual(parseHashRoute('#settings/market-data'),
                   { view: 'settings', subTab: 'market-data' });
  assert.deepEqual(parseHashRoute('#settings/preferences'),
                   { view: 'settings', subTab: 'preferences' });
  assert.deepEqual(parseHashRoute('#settings/bot-credentials'),
                   { view: 'settings', subTab: 'bot-credentials' });
});

test('parseHashRoute redirects legacy #bot-credentials into Settings', () => {
  assert.deepEqual(parseHashRoute('#bot-credentials'),
                   { view: 'settings', subTab: 'bot-credentials' });
});

test('parseHashRoute rejects unknown hashes and invalid sub-tabs', () => {
  assert.deepEqual(parseHashRoute(''),                    { view: null, subTab: null });
  assert.deepEqual(parseHashRoute('#nope'),               { view: null, subTab: null });
  assert.deepEqual(parseHashRoute('#settings/ghost'),     { view: null, subTab: null });
  assert.deepEqual(parseHashRoute('#settings/'),          { view: null, subTab: null });
  assert.deepEqual(parseHashRoute(null),                  { view: null, subTab: null });
});

test('hashForView serialises view (+ sub-tab) back to the same hash', () => {
  assert.equal(hashForView('trader'),                    '#trading');
  assert.equal(hashForView('settings'),                  '#settings');
  assert.equal(hashForView('settings', 'security'),      '#settings/security');
  assert.equal(hashForView('settings', 'market-data'),   '#settings/market-data');
  assert.equal(hashForView('settings', 'bogus'),         '#settings');
  assert.equal(hashForView('unknown-view'),              null);
});

test('parseHashRoute ↔ hashForView round-trips every known sub-tab', () => {
  for (const sub of SETTINGS_SUB_TABS) {
    const hash = hashForView('settings', sub);
    assert.deepEqual(parseHashRoute(hash), { view: 'settings', subTab: sub });
  }
});
