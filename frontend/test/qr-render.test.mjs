// QR rendering tests for the Security panel (#320). No deps —
// runs under plain `node --test`. Validates that buildQrSvg
// returns a well-formed scalable SVG for typical otpauth URIs and
// that renderQrInto/clearQr honour the [hidden] contract used by
// the Security panel close path.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { buildQrSvg, renderQrInto, clearQr } from '../js/qrRender.js';

const OTPAUTH = 'otpauth://totp/B3:alice@example.com?secret=JBSWY3DPEHPK3PXP&issuer=B3&algorithm=SHA1&digits=6&period=30';

test('buildQrSvg returns a scalable SVG with QR modules for a typical otpauth URI', () => {
  const svg = buildQrSvg(OTPAUTH);
  assert.equal(typeof svg, 'string');
  assert.match(svg, /^<svg\b/, 'must be an <svg> root');
  assert.match(svg, /viewBox=/, 'must declare a viewBox so scaling works');
  assert.match(svg, /<rect\b/, 'must contain QR module rects');
});

test('buildQrSvg accepts a custom cell size and error correction level', () => {
  const svg = buildQrSvg(OTPAUTH, { cellSize: 8, margin: 2, errorCorrection: 'H' });
  assert.match(svg, /^<svg\b/);
  assert.match(svg, /<rect\b/);
});

test('buildQrSvg rejects empty or non-string input', () => {
  assert.throws(() => buildQrSvg(''), /non-empty string/);
  assert.throws(() => buildQrSvg(null), /non-empty string/);
  assert.throws(() => buildQrSvg(42), /non-empty string/);
});

test('renderQrInto sets innerHTML and unhides the element on success', () => {
  const el = { innerHTML: '', hidden: true };
  renderQrInto(el, OTPAUTH);
  assert.equal(el.hidden, false);
  assert.match(el.innerHTML, /^<svg\b/);
});

test('renderQrInto swallows failures and keeps the element hidden', () => {
  // Trigger the catch branch deterministically with an empty string —
  // buildQrSvg throws, the helper must not let that bubble.
  const el = { innerHTML: '<svg>stale</svg>', hidden: false };
  renderQrInto(el, '');
  assert.equal(el.hidden, true);
  assert.equal(el.innerHTML, '');
});

test('renderQrInto is a no-op when the element is missing', () => {
  // Must not throw — the Security panel may be torn down before
  // an in-flight enrollment response completes.
  assert.doesNotThrow(() => renderQrInto(null, OTPAUTH));
  assert.doesNotThrow(() => renderQrInto(undefined, OTPAUTH));
});

test('clearQr wipes innerHTML and re-hides the element', () => {
  const el = { innerHTML: '<svg>secret</svg>', hidden: false };
  clearQr(el);
  assert.equal(el.innerHTML, '');
  assert.equal(el.hidden, true);
});
