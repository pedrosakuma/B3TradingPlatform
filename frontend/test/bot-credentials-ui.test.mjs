// Bot credentials UI smoke tests (sub-issue #169 of user-bot-fixp-listener-v0).
//
// The frontend is plain ES modules with DOM access — there is no jsdom
// in CI today, so we drive the module against a tiny hand-rolled DOM
// stub. The goal is the same as cancel-all.test.mjs: lock the contract
// the real renderer relies on without dragging in a heavier harness.
//
// What we verify here:
//   * `setBotCredentialsRows([])` renders the empty-state copy.
//   * Active rows render an Active badge AND a Revoke button; revoked
//     rows render a Revoked tag and NO action button.
//   * `openBotCredentialsSecretModal({ label, plainSecret })` reveals
//     the modal and pushes the plaintext PAT into the input value —
//     this is the "shown once" surface; if it ever silently fails the
//     user can never use the secret.
//   * `closeBotCredentialsSecretModal()` blanks the input value so
//     the secret leaves the DOM (matches the security invariant in
//     botCredentialsUi.js: never persisted, dropped on dismiss).

import { test } from "node:test";
import assert from "node:assert/strict";

import { installDomStub } from "./dom-stub.mjs";

installDomStub({
  ids: {
    "bot-credentials-open":            { tag: "button" },
    "bot-credentials-back":            { tag: "button" },
    "bot-credentials-refresh":         { tag: "button" },
    "bot-credentials-create-form":     { tag: "form" },
    "bot-credentials-label":           { tag: "input" },
    "bot-credentials-cert-thumbprint": { tag: "input" },
    "bot-credentials-create-submit":   { tag: "button" },
    "bot-credentials-body":            { tag: "tbody" },
    "bot-credentials-feedback":        { tag: "p" },
    "bot-credentials-secret-modal":    { tag: "div", hidden: true },
    "bot-credentials-secret-form":     { tag: "form" },
    "bot-credentials-secret-label":    { tag: "strong" },
    "bot-credentials-secret-value":    { tag: "input" },
    "bot-credentials-secret-copy-status": { tag: "p" },
    "bot-credentials-secret-copy":     { tag: "button" },
    "bot-credentials-secret-done":     { tag: "button" },
  },
});

const mod = await import("../js/botCredentialsUi.js");
mod.bindBotCredentialsUi();

test("empty state copy renders when there are no credentials", () => {
  mod.setBotCredentialsRows([]);
  const body = document.getElementById("bot-credentials-body");
  assert.match(body.innerHTML, /You have no bot credentials/);
});

test("active rows render badge + revoke button; revoked rows do not", () => {
  mod.setBotCredentialsRows([
    {
      id: "11111111-1111-1111-1111-111111111111",
      label: "active-bot",
      credShortId: "AAAA1111",
      createdAtUtc: "2025-01-02T03:04:05Z",
      revokedAt: null,
    },
    {
      id: "22222222-2222-2222-2222-222222222222",
      label: "old-bot",
      credShortId: "BBBB2222",
      createdAtUtc: "2025-01-01T00:00:00Z",
      revokedAt: "2025-01-03T00:00:00Z",
    },
  ]);
  const body = document.getElementById("bot-credentials-body");
  const html = body.innerHTML;
  assert.match(html, /active-bot/);
  assert.match(html, /old-bot/);
  assert.match(html, /Active/);
  assert.match(html, /Revoked/);
  // Revoke button only appears for the active row.
  const revokeMatches = html.match(/bot-cred-revoke/g) || [];
  assert.equal(revokeMatches.length, 1, "exactly one revoke button");
  // Revoke button targets the active credential id.
  assert.match(html, /data-id="11111111-1111-1111-1111-111111111111"/);
  assert.doesNotMatch(html, /data-id="22222222-2222-2222-2222-222222222222"/);
});

test("cert binding renders pinned badge with full title and unpinned label", () => {
  const thumbprint = "AB12" + "C".repeat(56) + "7F90";
  mod.setBotCredentialsRows([
    {
      id: "11111111-1111-1111-1111-111111111111",
      label: "pinned-bot",
      credShortId: "AAAA1111",
      createdAtUtc: "2025-01-02T03:04:05Z",
      revokedAt: null,
      boundCertThumbprint: thumbprint,
    },
    {
      id: "22222222-2222-2222-2222-222222222222",
      label: "plain-bot",
      credShortId: "BBBB2222",
      createdAtUtc: "2025-01-01T00:00:00Z",
      revokedAt: null,
      boundCertThumbprint: null,
    },
  ]);
  const html = document.getElementById("bot-credentials-body").innerHTML;
  assert.match(html, /pinned: <code>AB12…7F90<\/code>/);
  assert.match(html, new RegExp(`title="${thumbprint}"`));
  assert.match(html, /unpinned/);
});

test("create form passes normalized cert thumbprint", () => {
  const labelEl = document.getElementById("bot-credentials-label");
  const thumbprintEl = document.getElementById("bot-credentials-cert-thumbprint");
  const form = document.getElementById("bot-credentials-create-form");
  const calls = [];
  mod.setBotCredentialsHandlers({
    onCreate: (payload) => calls.push(payload),
  });

  labelEl.value = "new-bot";
  thumbprintEl.value = "ab12:" + "cd ".repeat(28) + "7f90";
  form.dispatchEvent({ type: "submit" });

  assert.deepEqual(calls, [{
    label: "new-bot",
    boundCertThumbprint: "AB12" + "CD".repeat(28) + "7F90",
  }]);
});

test("invalid create thumbprint shows feedback and does not submit", () => {
  const labelEl = document.getElementById("bot-credentials-label");
  const thumbprintEl = document.getElementById("bot-credentials-cert-thumbprint");
  const form = document.getElementById("bot-credentials-create-form");
  const feedback = document.getElementById("bot-credentials-feedback");
  let submitted = false;
  mod.setBotCredentialsHandlers({
    onCreate: () => { submitted = true; },
  });

  labelEl.value = "bad-bot";
  thumbprintEl.value = "not-a-thumbprint";
  form.dispatchEvent({ type: "submit" });

  assert.equal(submitted, false);
  assert.equal(feedback.hidden, false);
  assert.match(feedback.textContent, /64 hexadecimal/);
  assert.match(feedback.className, /error/);
});

test("edit pin calls handler with normalized value and clears on empty", () => {
  const body = document.getElementById("bot-credentials-body");
  const calls = [];
  const dataset = {
    id: "11111111-1111-1111-1111-111111111111",
    label: "edit-bot",
    thumbprint: "AB12" + "CD".repeat(28) + "7F90",
  };
  const editTarget = {
    closest: (selector) => selector === ".bot-cred-edit-pin" ? { dataset } : null,
  };
  mod.setBotCredentialsHandlers({
    onSetCertBinding: (payload) => calls.push(payload),
  });

  window.prompt = () => " 0011:" + "aa".repeat(28) + "eeff ";
  body.dispatchEvent({ type: "click", target: editTarget });
  window.prompt = () => " \n ";
  body.dispatchEvent({ type: "click", target: editTarget });

  assert.deepEqual(calls, [
    {
      id: dataset.id,
      label: dataset.label,
      boundCertThumbprint: "0011" + "AA".repeat(28) + "EEFF",
    },
    {
      id: dataset.id,
      label: dataset.label,
      boundCertThumbprint: null,
    },
  ]);
});

test("openBotCredentialsSecretModal exposes the PAT once and close drops it", () => {
  const modal = document.getElementById("bot-credentials-secret-modal");
  const input = document.getElementById("bot-credentials-secret-value");
  const labelEl = document.getElementById("bot-credentials-secret-label");
  assert.equal(modal.hidden, true, "modal starts hidden");

  mod.openBotCredentialsSecretModal({
    label: "my-bot",
    plainSecret: "b3t_abcd1234_xxxxxxxxxxxxxxxxxxxxxxxx",
  });
  assert.equal(modal.hidden, false, "modal is visible after open");
  assert.equal(labelEl.textContent, "my-bot");
  assert.equal(
    input.value,
    "b3t_abcd1234_xxxxxxxxxxxxxxxxxxxxxxxx",
    "PAT pushed into the modal input — this is the 'shown once' surface",
  );

  mod.closeBotCredentialsSecretModal();
  assert.equal(modal.hidden, true, "modal is hidden after close");
  assert.equal(input.value, "", "secret cleared from the DOM on close");
  assert.equal(labelEl.textContent, "", "label cleared on close");
});

test("clearBotCredentials closes the modal and wipes the secret", () => {
  mod.openBotCredentialsSecretModal({
    label: "another",
    plainSecret: "b3t_zzz_yyy",
  });
  const input = document.getElementById("bot-credentials-secret-value");
  assert.notEqual(input.value, "");
  mod.clearBotCredentials();
  assert.equal(input.value, "", "clearBotCredentials wipes the modal secret");
  const modal = document.getElementById("bot-credentials-secret-modal");
  assert.equal(modal.hidden, true);
});
