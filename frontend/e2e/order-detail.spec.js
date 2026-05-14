// E2E for the Order Detail modal (#245). The full flow is:
//   1. login → blotter visible
//   2. inject a synthetic Working order + a couple of executions for it
//      via the same state entry points used by the worker (mirrors the
//      pattern in replaced-terminal.spec.js — the smoke stack doesn't
//      actually fill orders end-to-end)
//   3. click any non-button cell of the row → modal opens with header +
//      executions table
//   4. dismiss with backdrop, then re-open and dismiss with Esc
//   5. ensure clicking the Modify button does NOT open the detail modal

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

async function injectOrderAndExecutions(page, clOrdId) {
  await page.evaluate(async (id) => {
    const state = await import("/js/state.js");
    state.applyOrdersDelta({
      clOrdId: id,
      symbol: "PETR4",
      securityId: 4,
      side: "Buy",
      type: "Limit",
      quantity: 200,
      leavesQuantity: 100,
      cumulativeQuantity: 100,
      price: 32.50,
      status: "PartiallyFilled",
    });
    state.applyExecutionsDelta({
      clOrdId: id,
      symbol: "PETR4",
      side: "Buy",
      status: "Working",
      kind: "New",
      leavesQuantity: 200,
      cumulativeQuantity: 0,
      lastQuantity: 0,
      lastPrice: 0,
      rejectReason: null,
      timestampUtc: "2026-05-07T20:00:00.000Z",
      isNativeStp: false,
    });
    state.applyExecutionsDelta({
      clOrdId: id,
      symbol: "PETR4",
      side: "Buy",
      status: "PartiallyFilled",
      kind: "PartialFill",
      leavesQuantity: 100,
      cumulativeQuantity: 100,
      lastQuantity: 100,
      lastPrice: 32.50,
      rejectReason: null,
      timestampUtc: "2026-05-07T20:00:01.000Z",
      isNativeStp: false,
    });
  }, clOrdId);
}

test.describe("Order detail modal (#245)", () => {
  test("clicking a working-order row opens the detail modal with header + executions, then closes via backdrop, Esc, and × button", async ({ page }) => {
    await login(page);

    const clOrdId = "E2E-ORDER-DETAIL-1";
    await injectOrderAndExecutions(page, clOrdId);

    const row = page.locator(`#blotter-body tr[data-clordid="${clOrdId}"]`);
    await expect(row).toBeVisible();

    const modal = page.locator("#order-detail-modal");
    await expect(modal).toBeHidden();

    // Click on the Symbol cell (a non-button area of the row).
    await row.locator("td").nth(1).click();
    await expect(modal).toBeVisible();
    await expect(page.locator("#order-detail-title")).toContainText(clOrdId);
    // Status badge reuses .status-cell-* class.
    await expect(modal.locator(".status-cell-PartiallyFilled").first()).toBeVisible();
    // Executions table should have at least one fill row + one transition row.
    const execRows = modal.locator("#order-detail-exec-body tr");
    await expect(execRows).toHaveCount(2);

    // Backdrop click closes.
    await modal.click({ position: { x: 5, y: 5 } });
    await expect(modal).toBeHidden();

    // Re-open, then close via Esc.
    await row.locator("td").nth(1).click();
    await expect(modal).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(modal).toBeHidden();

    // Re-open, then close via × button.
    await row.locator("td").nth(1).click();
    await expect(modal).toBeVisible();
    await page.locator("#order-detail-close").click();
    await expect(modal).toBeHidden();
  });

  test("clicking the Modify button on a working-order row does NOT open the order-detail modal", async ({ page }) => {
    await login(page);

    const clOrdId = "E2E-ORDER-DETAIL-2";
    await injectOrderAndExecutions(page, clOrdId);

    const row = page.locator(`#blotter-body tr[data-clordid="${clOrdId}"]`);
    await expect(row).toBeVisible();

    const detailModal = page.locator("#order-detail-modal");
    const modifyModal = page.locator("#modify-modal");
    await expect(detailModal).toBeHidden();
    await expect(modifyModal).toBeHidden();

    await row.locator(".modify-btn").click();
    // Detail modal must remain hidden; Modify modal opens (its own flow).
    await expect(detailModal).toBeHidden();
    await expect(modifyModal).toBeVisible();
    // Cleanup: dismiss modify modal so it doesn't bleed into other tests.
    await page.keyboard.press("Escape");
    await expect(modifyModal).toBeHidden();
  });

  test("a new ER for the open ClOrdID is appended live and refreshes the header", async ({ page }) => {
    await login(page);

    const clOrdId = "E2E-ORDER-DETAIL-3";
    await injectOrderAndExecutions(page, clOrdId);

    const row = page.locator(`#blotter-body tr[data-clordid="${clOrdId}"]`);
    await expect(row).toBeVisible();
    await row.locator("td").nth(1).click();
    const modal = page.locator("#order-detail-modal");
    await expect(modal).toBeVisible();
    await expect(modal.locator("#order-detail-exec-body tr")).toHaveCount(2);

    // Push a second fill that completes the order.
    await page.evaluate(async (id) => {
      const state = await import("/js/state.js");
      state.applyOrdersDelta({
        clOrdId: id,
        symbol: "PETR4",
        securityId: 4,
        side: "Buy",
        type: "Limit",
        quantity: 200,
        leavesQuantity: 0,
        cumulativeQuantity: 200,
        price: 32.50,
        status: "Filled",
      });
      state.applyExecutionsDelta({
        clOrdId: id,
        symbol: "PETR4",
        side: "Buy",
        status: "Filled",
        kind: "Fill",
        leavesQuantity: 0,
        cumulativeQuantity: 200,
        lastQuantity: 100,
        lastPrice: 32.60,
        rejectReason: null,
        timestampUtc: "2026-05-07T20:00:02.000Z",
        isNativeStp: false,
      });
    }, clOrdId);

    await expect(modal.locator("#order-detail-exec-body tr")).toHaveCount(3);
    await expect(modal.locator(".status-cell-Filled").first()).toBeVisible();

    // P2 — after a live `orders.delta` the originating row reference
    // we cached on open is detached (renderBlotter() replaces
    // #blotter-body.innerHTML). Closing must still return focus to the
    // row matching the same ClOrdID, re-resolved from the new DOM.
    await page.locator("#order-detail-close").click();
    await expect(modal).toBeHidden();
    await expect(row).toBeFocused();
  });
});
