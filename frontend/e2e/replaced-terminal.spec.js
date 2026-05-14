// Regression for #243: Replaced is a terminal OrderStatus, so the
// blotter row's Modify/Cancel buttons must render disabled and a
// click on Modify must not open the modal.
//
// The compose stub gateway never echoes a Replaced ER on its own
// (and reproducing the priority-lost CancelReplace flow needs the
// matching engine, not the smoke stack), so we inject a synthetic
// Replaced row directly into the state module — the helper under
// test (`isTerminalOrderStatus`) is the same code path the row
// renderer in ui.js uses to disable the action buttons.

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

test.describe("Replaced terminal status (#243)", () => {
  test("blotter row with Replaced status disables Modify/Cancel and modal stays hidden", async ({ page }) => {
    await login(page);

    // Inject a synthetic Replaced order through the same state entry
    // point the worker uses (`applyOrdersDelta`); the renderer keys
    // its disabled-button decision off `isTerminalOrderStatus`.
    const clOrdId = "E2E-REPLACED-1";
    await page.evaluate(async (id) => {
      const state = await import("/js/state.js");
      state.applyOrdersDelta({
        clOrdId: id,
        symbol: "PETR4",
        side: "Buy",
        type: "Limit",
        quantity: 100,
        leavesQuantity: 0,
        cumulativeQuantity: 0,
        price: 25.00,
        status: "Replaced",
      });
    }, clOrdId);

    const row = page.locator(`#blotter-body tr[data-clordid="${clOrdId}"]`);
    await expect(row).toBeVisible();
    await expect(row.locator(".status-cell-Replaced")).toHaveText("Replaced");

    const modifyBtn = row.locator(".modify-btn");
    const cancelBtn = row.locator(".cancel-btn");
    await expect(modifyBtn).toBeDisabled();
    await expect(cancelBtn).toBeDisabled();

    // Click Modify with `force` to bypass Playwright's actionability
    // check (disabled buttons are normally not clickable). The handler
    // must still be a no-op — the modal must remain hidden.
    const modal = page.locator("#modify-modal");
    await expect(modal).toBeHidden();
    await modifyBtn.click({ force: true }).catch(() => { /* disabled — expected */ });
    await expect(modal).toBeHidden();
  });
});
