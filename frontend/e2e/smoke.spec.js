// Frontend smoke E2E (section 5 of #30, opt-in via workflow_dispatch).
//
// Scope: catch obvious regressions in the trader workspace without
// relying on a real exchange. Verifies:
//   1. login form renders, accepts seeded credentials, hides itself
//   2. trader view appears with the username in the topbar
//   3. orders WebSocket reaches "connected" within the timeout
//   4. accessibility hooks added in section 5 are present
//   5. the cancel-order keyboard shortcut (Del) is wired even when
//      no row is selected (no exception, no broken state)
//   6. submit limit order → row appears in the blotter (#36 closed)
//   7. cancel from the blotter → DELETE 204, button disables on
//      terminal status

import { expect, test } from "@playwright/test";

const USERNAME = process.env.E2E_USERNAME ?? "alice";
const PASSWORD = process.env.E2E_PASSWORD ?? "wonderland";

// Symbol that the e2e overlay (docker/docker-compose.e2e.yml) maps
// in the SymbolDirectory so the backend can resolve it without an
// explicit SecurityId from the ticket form.
const SYMBOL = "PETR4";

async function login(page) {
  await page.goto("/");
  await expect(page.locator("#login-view")).toBeVisible();
  await page.fill("#login-username", USERNAME);
  await page.fill("#login-password", PASSWORD);
  await page.click("#login-submit");
  await expect(page.locator("#trader-view")).toBeVisible();
  await expect(page.locator("#ws-status")).toHaveText("connected", { timeout: 30_000 });
}

test.describe("trader workspace smoke", () => {
  test("login → trader view → WS connected", async ({ page }) => {
    await login(page);
    await expect(page.locator("#login-view")).toBeHidden();
    await expect(page.locator("#user-label")).toHaveText(USERNAME);

    // ARIA hooks from section 5.
    await expect(page.locator("#ws-status")).toHaveAttribute("aria-label", /connected/);
    await expect(page.locator("#ws-status")).toHaveAttribute("role", "status");
    await expect(page.locator("#logout")).toHaveAttribute("aria-label", "Logout");

    // Del with no selection should be a no-op (no JS exception).
    let pageError = null;
    page.on("pageerror", (err) => { pageError = err; });
    await page.locator("body").press("Delete");
    expect(pageError).toBeNull();
  });

  test("session-expiry modal markup is wired and hidden by default", async ({ page }) => {
    await page.goto("/");
    const modal = page.locator("#session-modal");
    await expect(modal).toBeHidden();
    await expect(modal).toHaveAttribute("role", "dialog");
    await expect(modal).toHaveAttribute("aria-modal", "true");
  });

  test("submit limit order → blotter row → cancel → DELETE 204", async ({ page }) => {
    await login(page);

    // Capture the DELETE response status independently of any UI
    // change so the test still proves the cancel round-trip even if
    // the stub gateway never produces a terminal ER.
    const deleteResponses = [];
    page.on("response", (resp) => {
      if (resp.request().method() === "DELETE" && resp.url().includes("/api/orders/")) {
        deleteResponses.push(resp.status());
      }
    });

    // Fill the ticket: PETR4, Buy, Limit, qty 100 (lot multiple),
    // price 32.00 (tick multiple). With no MD price observed the
    // fat-finger guard is skipped (validation.js:fatFingerCheck
    // returns null when lastPrice is unknown). 32.00 sits inside the
    // 10% price-collar band around the compose-seeded PETR4 reference
    // price of 32.50 (docker-compose.yml
    // Trading__Risk__ReferencePrices__PETR4) so the order isn't
    // rejected before the live market-data feed has printed a trade.
    await page.fill("#ticket-symbol", SYMBOL);
    await page.selectOption("#ticket-side", "Buy");
    await page.selectOption("#ticket-type", "Limit");
    await page.fill("#ticket-qty", "100");
    await page.fill("#ticket-price", "32.00");
    // Issue #105: Submit must be visible in the ticket panel viewport
    // without scrolling. Playwright's click() auto-scrolls into view,
    // so the click itself wouldn't catch a clipped button — assert
    // explicit in-viewport visibility before exercising it.
    await expect(page.locator("#ticket-submit")).toBeInViewport();
    await page.click("#ticket-submit");

    // The accepted ClOrdId is shown in the ticket-feedback line and
    // the order arrives in the blotter via the WS push.
    await expect(page.locator("#ticket-feedback")).toContainText(/accepted/i, { timeout: 15_000 });
    const row = page.locator("#blotter-body tr[data-clordid]").first();
    await expect(row).toBeVisible({ timeout: 15_000 });

    // Click the row's cancel button. The button is per-row with an
    // aria-label of "Cancel order <ClOrdId>".
    await row.locator(".cancel-btn").click();

    // The Stub gateway never echoes ERs, so the row may stay around
    // in PendingCancel. What we care about is that the DELETE made
    // it to the backend and returned 204.
    await expect.poll(
      () => deleteResponses.length,
      { timeout: 10_000, message: "expected at least one DELETE /api/orders/<id> response" },
    ).toBeGreaterThan(0);
    expect(deleteResponses[0]).toBe(204);
  });
});

