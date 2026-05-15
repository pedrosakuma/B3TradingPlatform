// Q1.4 (#256). E2E for the ticket UI surfacing the new OrderType /
// TIF / StopPrice / GoodTillDate fields. We don't actually wait for
// a GTD to fire (the scheduler in #255 has its own backend tests);
// instead we inject a synthetic Working order with stopPrice + GTD
// fields populated and a synthetic kind=Expired ER, and assert the
// Order Detail modal + executions log render the new affordances.

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

test.describe("Q1.4 (#256) ticket UI for new OrderTypes/TIFs", () => {
  test("StopLimit + GTD ticket → form fields visible, modal + executions surface stop/gtd/expired", async ({ page }) => {
    await login(page);

    // 1. Pick StopLimit + GTD on the ticket and assert the conditional
    //    inputs become visible (not hidden), exposed to screen readers.
    await page.selectOption("#ticket-type", "StopLimit");
    await expect(page.locator("#ticket-stop-price-label")).toBeVisible();
    await expect(page.locator("#ticket-price-label")).toBeVisible();

    await page.selectOption("#ticket-tif", "GTD");
    await expect(page.locator("#ticket-good-till-date-label")).toBeVisible();

    // 2. Inject a synthetic working order with the new fields populated
    //    so we don't depend on the smoke stack actually running through
    //    a stop trigger end-to-end.
    const clOrdId = "E2E-Q14-STOPLIMIT-1";
    const gtd = "2099-12-31T20:00:00.000Z";
    await page.evaluate(async ({ id, gtd }) => {
      const state = await import("/js/state.js");
      state.applyOrdersDelta({
        clOrdId: id,
        symbol: "PETR4",
        securityId: 4,
        side: "Buy",
        type: "StopLimit",
        timeInForce: "GTD",
        quantity: 200,
        leavesQuantity: 200,
        cumulativeQuantity: 0,
        price: 33.10,
        stopPrice: 33.00,
        goodTillDate: gtd,
        status: "New",
      });
      // Synthetic Expired ER (kind=Expired comes from #255 backend
      // when the GTD scheduler reaps the order).
      state.applyExecutionsDelta({
        clOrdId: id,
        symbol: "PETR4",
        side: "Buy",
        status: "Expired",
        kind: "Expired",
        leavesQuantity: 0,
        cumulativeQuantity: 0,
        lastQuantity: 0,
        lastPrice: 0,
        rejectReason: null,
        timestampUtc: new Date().toISOString(),
        isNativeStp: false,
      });
    }, { id: clOrdId, gtd });

    // 3. Working orders table — TIF column + STPL chip on the row.
    const row = page.locator(`#blotter-body tr[data-clordid="${clOrdId}"]`);
    await expect(row).toBeVisible();
    await expect(row.locator(".type-chip.chip-stpl")).toHaveText("STPL");
    // TIF column is between Type and Qty (5th cell, index 4).
    await expect(row.locator("td").nth(4)).toHaveText("GTD");

    // 4. Order Detail modal — TIF, Stop price, Good-till-date all present.
    await row.locator("td").nth(1).click();
    const modal = page.locator("#order-detail-modal");
    await expect(modal).toBeVisible();
    await expect(modal).toContainText("TIF");
    await expect(modal).toContainText("GTD");
    await expect(modal).toContainText("Stop price");
    await expect(modal).toContainText("Good-till-date");
    await expect(modal).toContainText("2099-12-31");
    await page.keyboard.press("Escape");
    await expect(modal).toBeHidden();

    // 5. Executions log — Expired badge with the gray .Expired class.
    const expiredEntry = page.locator("#executions-log .kind.Expired").first();
    await expect(expiredEntry).toBeVisible();
    await expect(expiredEntry).toHaveText("Expired");
  });
});
