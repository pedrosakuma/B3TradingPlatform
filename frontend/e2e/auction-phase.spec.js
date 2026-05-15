// Q1.6 (#258) — auction phase / panel / ticket coupling E2E.
//
// We don't drive a real venue here; we inject phase + auction frames
// straight into state via the same setters the WS worker uses (mirrors
// the pattern in order-detail.spec.js — the smoke stack doesn't run a
// real opening auction). The DOM should react:
//   1. Watchlist row badge updates as the phase transitions.
//   2. Auction panel auto-expands when the selected symbol enters
//      OpeningCall and renders TOP / match-qty / imbalance.
//   3. Ticket TIF default flips to GoodForAuction; Submit disables in
//      Reserved.

import { expect, test } from "@playwright/test";

const USERNAME = process.env.E2E_USERNAME ?? "alice";
const PASSWORD = process.env.E2E_PASSWORD ?? "wonderland";

async function login(page) {
  await page.goto("/");
  await expect(page.locator("#login-view")).toBeVisible();
  await page.fill("#login-username", USERNAME);
  await page.fill("#login-password", PASSWORD);
  await page.click("#login-submit");
  await expect(page.locator("#trader-view")).toBeVisible();
  await expect(page.locator("#ws-status")).toHaveText("connected", { timeout: 30_000 });
}

async function injectPhase(page, symbol, phase) {
  await page.evaluate(async ({ symbol, phase }) => {
    const state = await import("/js/state.js");
    state.applyPhaseFrame({ symbol, phase, at: new Date().toISOString() });
  }, { symbol, phase });
}

async function injectAuctionTop(page, symbol, top, imbalance, side) {
  await page.evaluate(async ({ symbol, top, imbalance, side }) => {
    const state = await import("/js/state.js");
    state.applyAuctionFrame({
      symbol, top, indicativeMatchQty: 5000,
      imbalance, imbalanceSide: side,
      at: new Date().toISOString(),
    });
  }, { symbol, top, imbalance, side });
}

async function selectSymbol(page, symbol) {
  await page.evaluate(async (sym) => {
    const state = await import("/js/state.js");
    // Make sure the symbol is in the watchlist so the renderer shows it.
    const cur = state.getState().watchlist;
    if (!cur.includes(sym)) state.setWatchlist([...cur, sym]);
    state.setSelectedSymbol(sym);
  }, symbol);
}

test.describe("Auction phase + panel + ticket coupling (#258)", () => {
  test("phase transitions update badge, auto-open auction panel and adapt ticket", async ({ page }) => {
    await login(page);

    const SYM = "PETR4";
    await selectSymbol(page, SYM);

    // 1. Inject Open → badge shows OPEN, panel stays hidden.
    await injectPhase(page, SYM, "Open");
    await expect(page.locator(`.phase-badge[data-symbol="${SYM}"]`)).toHaveText("OPEN");
    await expect(page.locator("#auction-panel")).toBeHidden();

    // 2. Type the symbol into the ticket and inject OpeningCall — TIF
    //    flips to GoodForAuction, hint visible, panel auto-opens.
    await page.fill("#ticket-symbol", SYM);
    await injectPhase(page, SYM, "OpeningCall");
    await expect(page.locator(`.phase-badge[data-symbol="${SYM}"]`)).toHaveText("PRE-OPEN");
    await expect(page.locator("#auction-panel")).toBeVisible();
    await expect(page.locator("#ticket-tif")).toHaveValue("GoodForAuction");
    await expect(page.locator("#ticket-tif-hint")).toBeVisible();

    // 3. Inject auction top + imbalance — panel renders the values.
    await injectAuctionTop(page, SYM, 32.50, 1500, "Buy");
    await expect(page.locator("#auction-top-price")).toHaveText("32,50");
    await expect(page.locator("#auction-imbalance")).toContainText("Buy");

    // 4. Inject Reserved (halt) — Submit disabled with tooltip.
    await injectPhase(page, SYM, "Reserved");
    await expect(page.locator(`.phase-badge[data-symbol="${SYM}"]`)).toHaveText("RESERVED");
    await expect(page.locator("#ticket-submit")).toBeDisabled();
    await expect(page.locator("#ticket-submit")).toHaveAttribute("title", "Instrumento halted");

    // 5. Back to Open — Submit re-enabled, panel collapses.
    await injectPhase(page, SYM, "Open");
    await expect(page.locator("#ticket-submit")).toBeEnabled();
    await expect(page.locator("#auction-panel")).toBeHidden();
  });
});
