export const DEFAULT_APP_TITLE = "B3TradingPlatform";

export function configuredAppTitle(config = globalThis.window?.__B3_CONFIG__) {
  const configured = config?.appTitle;
  return typeof configured === "string" && configured.trim() !== ""
    ? configured
    : DEFAULT_APP_TITLE;
}

export function traderDocumentTitle(appTitle = configuredAppTitle()) {
  return `${appTitle} — Trader`;
}

export function applyAppTitle(doc = globalThis.document, config = globalThis.window?.__B3_CONFIG__) {
  const appTitle = configuredAppTitle(config);
  if (!doc) return appTitle;

  doc.title = traderDocumentTitle(appTitle);
  const loginHeading = doc.querySelector?.("#login-form h1");
  if (loginHeading) loginHeading.textContent = appTitle;

  const brand = doc.querySelector?.(".brand");
  if (brand) setBrandLeadingText(brand, `${appTitle} `, doc);
  return appTitle;
}

function setBrandLeadingText(brand, text, doc) {
  const leadingText = findLeadingTextNode(brand);
  if (leadingText) {
    leadingText.textContent = text;
    return;
  }

  if (typeof doc?.createTextNode === "function" && typeof brand.insertBefore === "function") {
    brand.insertBefore(doc.createTextNode(text), brand.firstChild ?? null);
    return;
  }

  brand.textContent = text.trimEnd();
}

function findLeadingTextNode(brand) {
  if (brand.firstChild?.nodeType === 3) return brand.firstChild;
  for (const node of brand.childNodes ?? []) {
    if (node?.nodeType === 3) return node;
  }
  return null;
}
