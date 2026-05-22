// Fase 2 (#398). Client-side validation mirror of AlgoEndpoints POST.
// Run: `node --test frontend/test/algos-validation.test.mjs`.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateCreateAlgo } from '../js/validation.js';

const base = { symbol: 'PETR4', securityId: 1, side: 'Buy', totalQuantity: 1000 };

test('happy path: Iceberg', () => {
  const r = validateCreateAlgo({ ...base, type: 'Iceberg', iceberg: { displayQuantity: 100, limitPrice: 30.5 } });
  assert.equal(r.ok, true);
});

test('Iceberg: displayQuantity > totalQuantity rejected', () => {
  const r = validateCreateAlgo({ ...base, type: 'Iceberg', iceberg: { displayQuantity: 2000 } });
  assert.equal(r.ok, false);
  assert.match(r.error, /exceder totalQuantity/);
});

test('Iceberg: displayQuantity must be positive', () => {
  const r = validateCreateAlgo({ ...base, type: 'Iceberg', iceberg: { displayQuantity: 0 } });
  assert.equal(r.ok, false);
});

test('happy path: TWAP Limit', () => {
  const r = validateCreateAlgo({
    ...base, type: 'Twap',
    twap: {
      startUtc: '2026-01-01T13:00:00Z', endUtc: '2026-01-01T14:00:00Z',
      sliceCount: 10, childOrderType: 'Limit', childPrice: 30.5,
    },
  });
  assert.equal(r.ok, true);
});

test('TWAP: childPrice required when childOrderType=Limit', () => {
  const r = validateCreateAlgo({
    ...base, type: 'Twap',
    twap: {
      startUtc: '2026-01-01T13:00:00Z', endUtc: '2026-01-01T14:00:00Z',
      sliceCount: 10, childOrderType: 'Limit',
    },
  });
  assert.equal(r.ok, false);
  assert.match(r.error, /childPrice/);
});

test('TWAP: endUtc must be > startUtc', () => {
  const r = validateCreateAlgo({
    ...base, type: 'Twap',
    twap: {
      startUtc: '2026-01-01T14:00:00Z', endUtc: '2026-01-01T13:00:00Z',
      sliceCount: 10, childOrderType: 'Market', childPrice: null,
    },
  });
  assert.equal(r.ok, false);
  assert.match(r.error, /endUtc deve ser maior/);
});

test('TWAP: floor slice qty must be >= 1', () => {
  const r = validateCreateAlgo({
    ...base, totalQuantity: 5, type: 'Twap',
    twap: {
      startUtc: '2026-01-01T13:00:00Z', endUtc: '2026-01-01T14:00:00Z',
      sliceCount: 10, childOrderType: 'Market', childPrice: null,
    },
  });
  assert.equal(r.ok, false);
  assert.equal(r.detail?.impliedSliceQuantity, 0);
});

test('happy path: VWAP with defaults', () => {
  const r = validateCreateAlgo({
    ...base, type: 'Vwap',
    vwap: {
      startUtc: '2026-01-01T13:00:00Z', endUtc: '2026-01-01T14:00:00Z',
      childOrderType: 'Limit', childPrice: 30.5,
    },
  });
  assert.equal(r.ok, true);
});

test('VWAP: sliceMaxPct out of (0,1]', () => {
  const r = validateCreateAlgo({
    ...base, type: 'Vwap',
    vwap: {
      startUtc: '2026-01-01T13:00:00Z', endUtc: '2026-01-01T14:00:00Z',
      childOrderType: 'Market', childPrice: null, sliceMaxPct: 1.5,
    },
  });
  assert.equal(r.ok, false);
  assert.match(r.error, /sliceMaxPct/);
});

test('happy path: POV', () => {
  const r = validateCreateAlgo({
    ...base, type: 'Pov',
    pov: {
      startUtc: '2026-01-01T13:00:00Z', endUtc: '2026-01-01T14:00:00Z',
      childOrderType: 'Limit', childPrice: 30.5,
      participationRate: 0.1,
    },
  });
  assert.equal(r.ok, true);
});

test('POV: participationRate required in (0,1]', () => {
  const r = validateCreateAlgo({
    ...base, type: 'Pov',
    pov: {
      startUtc: '2026-01-01T13:00:00Z', endUtc: '2026-01-01T14:00:00Z',
      childOrderType: 'Market', childPrice: null,
      participationRate: 0,
    },
  });
  assert.equal(r.ok, false);
  assert.match(r.error, /participationRate/);
});

test('happy path: Pegged Mid', () => {
  const r = validateCreateAlgo({
    ...base, type: 'Pegged',
    pegged: { ref: 'Mid', offsetTicks: -1 },
  });
  assert.equal(r.ok, true);
});

test('Pegged: invalid ref rejected', () => {
  const r = validateCreateAlgo({
    ...base, type: 'Pegged',
    pegged: { ref: 'Bid', offsetTicks: 0 },
  });
  assert.equal(r.ok, false);
  assert.match(r.error, /Mid \| Best \| Last/);
});

test('Pegged: Market childOrderType rejected', () => {
  const r = validateCreateAlgo({
    ...base, type: 'Pegged',
    pegged: { ref: 'Mid', offsetTicks: 0, childOrderType: 'Market' },
  });
  assert.equal(r.ok, false);
  assert.match(r.error, /Pegged só aceita childOrderType=Limit/);
});

test('common: totalQuantity must be positive', () => {
  const r = validateCreateAlgo({ ...base, totalQuantity: 0, type: 'Iceberg', iceberg: { displayQuantity: 1 } });
  assert.equal(r.ok, false);
});

test('common: unknown type rejected at outer guard', () => {
  const r = validateCreateAlgo({ ...base, type: 'Xyzzy' });
  assert.equal(r.ok, false);
});

test('common: side must be Buy or Sell', () => {
  const r = validateCreateAlgo({ ...base, side: 'Hodl', type: 'Iceberg', iceberg: { displayQuantity: 100 } });
  assert.equal(r.ok, false);
});
