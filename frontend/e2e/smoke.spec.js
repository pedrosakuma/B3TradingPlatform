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
//
// NOT covered (tracked separately):
//   - POST /orders ↔ ExecutionReport round-trip. The ticket form does
//     not yet pass SecurityId, so Stub-mode submits would 400. See the
//     follow-up issue linked in #30.

import { expect, test } from "@playwright/test";

const USERNAME = process.env.E2E_USERNAME ?? "alice";
const PASSWORD = process.env.E2E_PASSWORD ?? "wonderland";

test.describe("trader workspace smoke", () => {
  test("login → trader view → WS connected", async ({ page }) => {
    await page.goto("/");

    // 1. Login form visible.
    await expect(page.locator("#login-view")).toBeVisible();
    await page.fill("#login-username", USERNAME);
    await page.fill("#login-password", PASSWORD);
    // Backend defaults to same-origin via the nginx reverse proxy, so
    // we leave the backend field blank.
    await page.click("#login-submit");

    // 2. Trader view replaces login.
    await expect(page.locator("#trader-view")).toBeVisible();
    await expect(page.locator("#login-view")).toBeHidden();
    await expect(page.locator("#user-label")).toHaveText(USERNAME);

    // 3. WS reaches connected. Generous timeout for cold container.
    await expect(page.locator("#ws-status")).toHaveText("connected", { timeout: 30_000 });
    await expect(page.locator("#ws-status")).toHaveAttribute("aria-label", /connected/);

    // 4. ARIA hooks from section 5.
    await expect(page.locator("#ws-status")).toHaveAttribute("role", "status");
    await expect(page.locator("#logout")).toHaveAttribute("aria-label", "Logout");

    // 5. Del with no selection should be a no-op (no JS exception).
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
});
