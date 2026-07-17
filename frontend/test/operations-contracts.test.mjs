import { readFile } from "node:fs/promises";
import { test } from "node:test";
import assert from "node:assert/strict";

import {
  listSubAccounts,
  getSessionPhase,
  getAdminRiskLimits,
  getReferencePrices,
  mutateCash,
  createSubAccount,
  setSessionPhase,
  reloadAdminRisk,
  setOrderStale,
} from "../js/protocol.js";

const fixture = JSON.parse(await readFile(
  new URL("./fixtures/operations-contracts.json", import.meta.url),
  "utf8",
));

test("operations protocol consumes serialized backend contracts", async () => {
  const payloads = [
    fixture.subAccounts,
    fixture.sessionPhase,
    fixture.risk,
    fixture.referencePrices,
    fixture.cash,
  ];
  const requests = [];
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (url, init = {}) => {
    requests.push({ url: String(url), init });
    return new Response(JSON.stringify(payloads.shift()), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });

    test("administrative mutations use the authorized backend routes", async () => {
      const requests = [];
      const originalFetch = globalThis.fetch;
      globalThis.fetch = async (url, init = {}) => {
        requests.push({ url: String(url), init });
        if (String(url).endsWith("/sub-accounts/")) {
          return new Response(JSON.stringify(fixture.subAccounts[0]), {
            status: 201,
            headers: { "Content-Type": "application/json" },
          });
        }
        return new Response(null, { status: 204 });
      };
      try {
        await createSubAccount("http://host", "tok", { id: "BOOK-A", displayName: "Agency book" });
        await setSessionPhase("http://host", "tok", { symbol: "PETR4", phase: "OpeningAuction" });
        await reloadAdminRisk("http://host", "tok");
        await setOrderStale("http://host", "tok", {
          firmId: "default", clOrdId: "123", stale: true, reason: "venue gap",
        });
      } finally {
        globalThis.fetch = originalFetch;
      }

      assert.match(requests[0].url, /\/sub-accounts\/$/);
      assert.match(requests[1].url, /\/admin\/session-phase\/PETR4$/);
      assert.match(requests[2].url, /\/admin\/risk\/reload$/);
      assert.match(requests[3].url, /\/admin\/firms\/default\/orders\/123\/mark-stale$/);
      for (const request of requests) {
        assert.equal(request.init.headers.Authorization, "Bearer tok");
      }
    });
  };
  try {
    assert.deepEqual(await listSubAccounts("http://host", "tok", { includeDeactivated: true }), fixture.subAccounts);
    assert.deepEqual(await getSessionPhase("http://host", "tok"), fixture.sessionPhase);
    assert.deepEqual(
      await getAdminRiskLimits("http://host", "tok", fixture.risk.query),
      fixture.risk,
    );
    assert.deepEqual(await getReferencePrices("http://host", "tok", "PETR4"), fixture.referencePrices);
    assert.deepEqual(
      await mutateCash("http://host", "tok", {
        endclient: "alice", kind: "Deposit", amount: 1000, currency: "BRL",
      }),
      fixture.cash,
    );
  } finally {
    globalThis.fetch = originalFetch;
  }

  assert.match(requests[0].url, /includeDeactivated=true/);
  assert.match(requests[2].url, /firmId=default/);
  assert.match(requests[3].url, /symbols=PETR4/);
  assert.equal(requests[4].init.method, "POST");
});
