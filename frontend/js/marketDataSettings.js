import { configuredMarketDataUrl, defaultMarketDataUrl } from "./protocol.js";

export const MD_KEY = "b3tp.md";

function readStoredSymbols(storage, fallbackSymbols = []) {
  let symbols = fallbackSymbols.slice();
  try {
    const raw = storage.getItem(MD_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      if (Array.isArray(parsed?.symbols)) symbols = parsed.symbols;
    }
  } catch { /* fall back */ }
  return symbols;
}

export function readMdConnectionConfig(storage, fallbackSymbols = []) {
  return {
    url: defaultMarketDataUrl(),
    symbols: readStoredSymbols(storage, fallbackSymbols),
  };
}

export function readMdDisplayConfig(storage, fallbackSymbols = []) {
  return {
    url: configuredMarketDataUrl() || defaultMarketDataUrl(),
    symbols: readStoredSymbols(storage, fallbackSymbols),
  };
}

export function writeMdConfig(storage, symbols) {
  storage.setItem(MD_KEY, JSON.stringify({ symbols }));
}

export function clearMdConfig(storage) {
  storage.removeItem(MD_KEY);
}
