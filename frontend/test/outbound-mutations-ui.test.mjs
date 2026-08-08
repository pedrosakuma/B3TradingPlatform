// #785. Outbound mutation reconciliation admin UI.
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import assert from "node:assert/strict";
import { installDomStub } from "./dom-stub.mjs";

import {
  listOutboundMutations,
  getOutboundMutation,
  registerOutboundMutationEvidence,
  resolveOutboundMutation,
  approveOutboundMutationResolution,
} from "../js/protocol.js";

const fixture = JSON.parse(await readFile(
  new URL("./fixtures/outbound-mutations.json", import.meta.url),
  "utf8",
));

test("outbound mutation protocol calls hit the expected admin routes", async () => {
  const payloads = [
    fixture.list,
    fixture.detailSelf,
    fixture.evidence,
    fixture.resolvePending,
    fixture.approveOk,
  ];
  const requests = [];
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (url, init = {}) => {
    requests.push({ url: String(url), init });
    return new Response(JSON.stringify(payloads.shift()), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  };
  try {
    assert.deepEqual(
      await listOutboundMutations("http://host", "tok", {
        firmId: "FIRM01", state: "ambiguous", requiresReconciliation: true,
      }),
      fixture.list,
    );
    assert.deepEqual(
      await getOutboundMutation("http://host", "tok", "22222222-2222-2222-2222-222222222222"),
      fixture.detailSelf,
    );
    assert.deepEqual(
      await registerOutboundMutationEvidence("http://host", "tok", "22222222-2222-2222-2222-222222222222", {
        sourceType: "official_extract",
        evidenceReference: "extract-001",
        coverageStartUtc: "2026-07-25T00:00:00Z",
        coverageEndUtc: "2026-07-26T00:00:00Z",
        attestationReference: "att-1",
      }),
      fixture.evidence,
    );
    assert.deepEqual(
      await resolveOutboundMutation("http://host", "tok", "22222222-2222-2222-2222-222222222222", {
        decision: "venue_absent",
        evidenceType: "manual_annotation",
        evidenceReference: "ticket-42",
        reason: "confirmed absent per venue mass-action extract",
      }),
      fixture.resolvePending,
    );
    assert.deepEqual(
      await approveOutboundMutationResolution(
        "http://host", "tok", "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333",
      ),
      fixture.approveOk,
    );
  } finally {
    globalThis.fetch = originalFetch;
  }

  assert.match(requests[0].url, /\/api\/admin\/outbound-mutations\/\?firmId=FIRM01&state=ambiguous&requiresReconciliation=true$/);
  assert.match(requests[1].url, /\/api\/admin\/outbound-mutations\/22222222-2222-2222-2222-222222222222$/);
  assert.equal(requests[2].url, "http://host/api/admin/outbound-mutations/22222222-2222-2222-2222-222222222222/evidence");
  assert.equal(requests[2].init.method, "POST");
  assert.equal(requests[3].url, "http://host/api/admin/outbound-mutations/22222222-2222-2222-2222-222222222222/resolve");
  assert.equal(requests[3].init.method, "POST");
  assert.equal(
    requests[4].url,
    "http://host/api/admin/outbound-mutations/22222222-2222-2222-2222-222222222222/resolve/33333333-3333-3333-3333-333333333333/approve",
  );
  assert.equal(requests[4].init.method, "POST");
  for (const request of requests) {
    assert.equal(request.init.headers.Authorization, `${"Bearer"} tok`);
  }
});

installDomStub({
  ids: {
    "admin-refresh": { tag: "button" },
    "admin-feedback": { tag: "p", hidden: true },
    "admin-mode": { tag: "span" },
    "admin-firms-body": { tag: "tbody" },
    "admin-endclient-body": { tag: "tbody" },
    "admin-add-ec-form": { tag: "form" },
    "admin-add-ec-id": { tag: "input" },
    "admin-halts-body": { tag: "tbody" },
    "admin-add-halt-form": { tag: "form" },
    "admin-add-halt-symbol": { tag: "input" },
    "admin-eod-btn": { tag: "button" },
    "admin-eod-output": { tag: "pre", hidden: true },
    "outbound-mutations-filter-form": { tag: "form" },
    "outbound-mutations-state": { tag: "select" },
    "outbound-mutations-requires-reconciliation": { tag: "input" },
    "outbound-mutations-body": { tag: "tbody" },
    "outbound-mutations-feedback": { tag: "p", hidden: true },
    "outbound-mutation-detail": { tag: "div", hidden: true },
    "outbound-detail-id": { tag: "code" },
    "outbound-detail-summary": { tag: "pre" },
    "outbound-evidence-form": { tag: "form" },
    "outbound-evidence-source-type": { tag: "select" },
    "outbound-evidence-reference": { tag: "input" },
    "outbound-evidence-coverage-start": { tag: "input" },
    "outbound-evidence-coverage-end": { tag: "input" },
    "outbound-evidence-attestation": { tag: "input" },
    "outbound-resolve-form": { tag: "form" },
    "outbound-resolve-decision": { tag: "select" },
    "outbound-resolve-evidence-type": { tag: "select" },
    "outbound-resolve-evidence-reference": { tag: "input" },
    "outbound-resolve-reason": { tag: "input" },
    "outbound-pending-proposals": { tag: "div" },
  },
});

const stateModule = await import("../js/state.js");
const adminUi = await import("../js/adminUi.js");

let loadedDetailFor = null;
adminUi.bindAdminUi();
adminUi.setAdminHandlers({
  onLoadOutboundMutationDetail: (mutationId) => { loadedDetailFor = mutationId; },
  currentUsername: "admin-alice",
});

test("renders the requires-reconciliation mutation list with a pending-approval badge", () => {
  stateModule.setOutboundMutations({ ...fixture.list, fetchedAt: Date.now() });
  const body = document.getElementById("outbound-mutations-body");
  assert.match(body.innerHTML, /11111111-1111-1111-1111-111111111111/);
  assert.match(body.innerHTML, /22222222-2222-2222-2222-222222222222/);
  assert.match(body.innerHTML, /pending approval/i);
});

test("expanding a row requests detail and reveals the detail panel", () => {
  const body = document.getElementById("outbound-mutations-body");
  // dom-stub's innerHTML assignment doesn't build real child nodes, so we
  // drive the click handler indirectly via dispatchEvent on the body with
  // a synthetic target instead of querying for the rendered <button>.
  const fakeButton = { dataset: { mutationId: "22222222-2222-2222-2222-222222222222" }, closest: (sel) => (sel === ".outbound-mutation-expand" ? fakeButton : null) };
  body.dispatchEvent({ type: "click", target: fakeButton });

  assert.equal(loadedDetailFor, "22222222-2222-2222-2222-222222222222");
  assert.equal(document.getElementById("outbound-mutation-detail").hidden, false);
  assert.equal(document.getElementById("outbound-detail-id").textContent, "22222222-2222-2222-2222-222222222222");
});

test("approve action is hidden for the current session's own proposal (maker == checker guard)", () => {
  stateModule.setOutboundMutationDetail({
    mutationId: "22222222-2222-2222-2222-222222222222",
    detail: fixture.detailSelf,
    fetchedAt: Date.now(),
  });
  const proposals = document.getElementById("outbound-pending-proposals");
  assert.doesNotMatch(proposals.innerHTML, /outbound-approve-proposal/);
  assert.match(proposals.innerHTML, /you proposed it/i);
});

test("approve action is offered when a different admin made the proposal", () => {
  stateModule.setOutboundMutationDetail({
    mutationId: "22222222-2222-2222-2222-222222222222",
    detail: fixture.detailOther,
    fetchedAt: Date.now(),
  });
  const proposals = document.getElementById("outbound-pending-proposals");
  assert.match(proposals.innerHTML, /outbound-approve-proposal/);
  assert.match(proposals.innerHTML, /admin-bob/);
});

test("approving dispatches the mutation/proposal id pair to the handler", () => {
  let approved = null;
  adminUi.setAdminHandlers({
    onApproveOutboundMutation: (mutationId, proposalId) => { approved = { mutationId, proposalId }; },
  });
  stateModule.setOutboundMutationDetail({
    mutationId: "22222222-2222-2222-2222-222222222222",
    detail: fixture.detailOther,
    fetchedAt: Date.now(),
  });
  const originalConfirm = globalThis.window.confirm;
  globalThis.window.confirm = () => true;
  try {
    const container = document.getElementById("outbound-pending-proposals");
    const fakeButton = {
      dataset: {
        mutationId: "22222222-2222-2222-2222-222222222222",
        proposalId: "33333333-3333-3333-3333-333333333333",
      },
      closest: (sel) => (sel === ".outbound-approve-proposal" ? fakeButton : null),
    };
    container.dispatchEvent({ type: "click", target: fakeButton });
  } finally {
    globalThis.window.confirm = originalConfirm;
  }

  assert.deepEqual(approved, {
    mutationId: "22222222-2222-2222-2222-222222222222",
    proposalId: "33333333-3333-3333-3333-333333333333",
  });
});
