import test from "node:test";
import assert from "node:assert/strict";

import {
  deriveSelfDepositFeedback,
  parseSelfDepositAmount,
} from "../js/selfDeposit.js";

test("self-deposit amount parsing accepts decimal comma and rejects empty or non-positive values", () => {
  assert.deepEqual(parseSelfDepositAmount("12,34"), { valid: true, amount: 12.34 });
  assert.deepEqual(parseSelfDepositAmount(""), {
    valid: false,
    message: "Enter an amount greater than zero.",
  });
  assert.deepEqual(parseSelfDepositAmount("0"), {
    valid: false,
    message: "Enter an amount greater than zero.",
  });
});

test("self-deposit success feedback reports deposited amount and resulting balance", () => {
  const feedback = deriveSelfDepositFeedback({
    kind: "success",
    amount: 2500,
    available: 10250.5,
  });

  assert.equal(feedback.tone, "ok");
  assert.match(feedback.message, /R\$\s*2\.500,00/);
  assert.match(feedback.message, /R\$\s*10\.250,50/);
});

test("self-deposit limit and disabled errors stay trader-readable", () => {
  const perRequest = deriveSelfDepositFeedback({
    kind: "error",
    error: {
      body: { error: "amount_exceeds_limit", maxDepositAmount: 1000 },
    },
  });
  assert.equal(perRequest.tone, "error");
  assert.match(perRequest.message, /R\$\s*1\.000,00/);

  const balanceCap = deriveSelfDepositFeedback({
    kind: "error",
    error: {
      body: {
        error: "balance_exceeds_limit",
        maxBalanceAfterDeposit: 5000,
        current: 4900,
      },
    },
  });
  assert.match(balanceCap.message, /R\$\s*5\.000,00/);
  assert.match(balanceCap.message, /R\$\s*4\.900,00/);

  const disabled = deriveSelfDepositFeedback({
    kind: "error",
    error: { status: 404 },
  });
  assert.match(disabled.message, /unavailable/i);
});
