import test from "node:test";
import assert from "node:assert/strict";

import { classifyAuthResponse, requireEnrollmentResponse } from "../js/authJourney.js";
import { enrollTotp, login, verifyTotp } from "../js/protocol.js";
import { installDomStub } from "./dom-stub.mjs";

test("mandatory enrollment response chain reaches a session", () => {
  const login = classifyAuthResponse({
    requires2faEnrollment: true,
    enrollmentToken: "force-enroll-token",
  });
  assert.deepEqual(login, { kind: "enrollment", enrollmentToken: "force-enroll-token" });

  const enrollment = requireEnrollmentResponse({
    secret: "JBSWY3DPEHPK3PXP",
    otpauthUri: "otpauth://totp/B3:alice?secret=JBSWY3DPEHPK3PXP",
    recoveryCodes: ["recovery-one"],
    totpChallengeToken: "verify-enrollment-token",
  });
  assert.equal(enrollment.totpChallengeToken, "verify-enrollment-token");

  const verified = classifyAuthResponse({
    token: "jwt-after-enrollment",
    expiresAt: "2026-07-16T18:00:00Z",
  });
  assert.equal(verified.kind, "session");
  assert.equal(verified.response.token, "jwt-after-enrollment");
});

test("renewal distinguishes the TOTP step and rejects missing credentials", () => {
  const passwordStep = classifyAuthResponse({
    requires2fa: true,
    totpChallengeToken: "renewal-challenge",
  });

  assert.deepEqual(passwordStep, { kind: "totp", totpChallengeToken: "renewal-challenge" });

  const secondStep = classifyAuthResponse({
    token: "renewed-jwt",
    expiresAt: "2026-07-16T19:00:00Z",
  });
  assert.equal(secondStep.kind, "session");
  assert.throws(
    () => classifyAuthResponse({ requires2fa: false }),
    /did not include a valid session or challenge/,
  );
});

test("protocol carries the mandatory enrollment challenge through the real API contracts", async () => {
  const responses = [
    { requires2faEnrollment: true, enrollmentToken: "force-token" },
    {
      secret: "JBSWY3DPEHPK3PXP",
      otpauthUri: "otpauth://totp/B3:alice?secret=JBSWY3DPEHPK3PXP",
      recoveryCodes: ["recovery-one"],
      totpChallengeToken: "verify-token",
    },
    { token: "jwt", expiresAt: "2026-07-16T18:00:00Z" },
  ];
  const requests = [];
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (url, init = {}) => {
    requests.push({ url, init });
    return new Response(JSON.stringify(responses.shift()), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  };
  try {
    const first = await login("https://trading.example", "alice", "wonderland");
    const enrollment = await enrollTotp("https://trading.example", null, first.enrollmentToken);
    const session = await verifyTotp("https://trading.example", {
      code: "123456",
      totpChallengeToken: enrollment.totpChallengeToken,
    });

    assert.equal(session.token, "jwt");
    assert.deepEqual(JSON.parse(requests[1].init.body), { enrollmentToken: "force-token" });
    assert.deepEqual(JSON.parse(requests[2].init.body), {
      code: "123456",
      totpChallengeToken: "verify-token",
    });
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("session modal switches from password to TOTP without submitting an empty session", async () => {
  const { elements } = installDomStub({
    ids: {
      "session-modal": { hidden: true },
      "session-modal-form": { tag: "form" },
      "session-modal-password": { tag: "input" },
      "session-modal-totp-label": { tag: "label", hidden: true },
      "session-modal-totp": { tag: "input" },
      "session-modal-msg": {},
      "session-modal-error": { hidden: true },
      "session-modal-logout": { tag: "button" },
    },
  });
  const ui = await import("../js/ui.js");
  const submitted = [];
  ui.openSessionModal({ onRenew: (value) => submitted.push(value), onLogout: () => {} });

  elements.get("session-modal-password").value = "wonderland";
  elements.get("session-modal-form").dispatchEvent({ type: "submit" });
  assert.deepEqual(submitted.pop(), { password: "wonderland" });

  ui.setSessionModalTotpRequired(true);
  elements.get("session-modal-totp").value = "123456";
  elements.get("session-modal-form").dispatchEvent({ type: "submit" });
  assert.deepEqual(submitted.pop(), { code: "123456" });
});
