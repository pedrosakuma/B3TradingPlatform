// QR rendering helper for the Security panel — issue #320.
//
// Wraps the vendored qrcode-generator library (Kazuhiko Arase, MIT)
// to produce an inline SVG for an `otpauth://` URI. SVG is preferred
// over <img src="data:image/png"> so the markup stays under
// `default-src 'self'` without needing `img-src data:` for QR codes,
// and so the badge scales cleanly on hi-DPI displays.
//
// Public surface (kept small on purpose):
//   buildQrSvg(text, opts?) → string  — pure, used by tests.
//   renderQrInto(el, text, opts?)      — DOM glue used by app.js.
//
// Auto type-number detection (typeNumber=0) lets the library pick the
// smallest QR version that fits, so otpauth URIs of any realistic
// length (issuer/account/secret/digits/period) all render without us
// guessing a fixed version.

import qrcode from "./vendor/qrcode.js";

const DEFAULTS = Object.freeze({
  cellSize: 4,             // px per module — ~120-160px for typical otpauth URI
  margin: 4,               // quiet-zone modules (QR spec minimum is 4)
  errorCorrection: "M",    // L|M|Q|H — M is the de-facto auth-app default
});

export function buildQrSvg(text, opts = {}) {
  if (typeof text !== "string" || text.length === 0) {
    throw new Error("buildQrSvg: text must be a non-empty string");
  }
  const { cellSize, margin, errorCorrection } = { ...DEFAULTS, ...opts };
  const qr = qrcode(0, errorCorrection);
  qr.addData(text);
  qr.make();
  return qr.createSvgTag({ cellSize, margin, scalable: true });
}

export function renderQrInto(el, text, opts = {}) {
  if (!el) return;
  try {
    el.innerHTML = buildQrSvg(text, opts);
    el.hidden = false;
  } catch (err) {
    // Never let a QR rendering failure block the enrollment flow —
    // the otpauth URI input below remains the source of truth.
    el.innerHTML = "";
    el.hidden = true;
    if (typeof console !== "undefined") {
      console.warn("qrRender: failed to render QR", err);
    }
  }
}

export function clearQr(el) {
  if (!el) return;
  el.innerHTML = "";
  el.hidden = true;
}
