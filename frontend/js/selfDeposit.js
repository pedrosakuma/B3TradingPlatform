import { formatCurrency } from "./formatters.js";

export function parseSelfDepositAmount(rawValue) {
  const normalized = String(rawValue ?? "").trim().replace(",", ".");
  if (!normalized) {
    return { valid: false, message: "Enter an amount greater than zero." };
  }
  const amount = Number(normalized);
  if (!Number.isFinite(amount) || amount <= 0) {
    return { valid: false, message: "Enter an amount greater than zero." };
  }
  return { valid: true, amount };
}

export function deriveSelfDepositFeedback({ kind, amount, available, error, message } = {}) {
  switch (kind) {
    case "submitted":
      return { tone: "info", message: "Submitting deposit…" };
    case "success":
      return {
        tone: "ok",
        message: `Deposited ${formatCurrency(amount)}. Available balance ${formatCurrency(available)}.`,
      };
    case "validation":
      return { tone: "error", message: message || "Enter an amount greater than zero." };
    case "error":
      if (error?.status === 404) {
        return { tone: "error", message: "Self-service deposit is unavailable in this environment." };
      }
      if (error?.body?.error === "amount_exceeds_limit") {
        return {
          tone: "error",
          message: `Deposit limit is ${formatCurrency(error.body.maxDepositAmount)} per request.`,
        };
      }
      if (error?.body?.error === "balance_exceeds_limit") {
        return {
          tone: "error",
          message: `Deposit would exceed the sandbox balance cap of ${formatCurrency(error.body.maxBalanceAfterDeposit)} (current ${formatCurrency(error.body.current)}).`,
        };
      }
      if (error?.body?.error === "amount must be > 0") {
        return { tone: "error", message: "Enter an amount greater than zero." };
      }
      return { tone: "error", message: error?.message || "Deposit failed." };
    default:
      throw new Error(`Unknown self-deposit feedback kind: ${kind}`);
  }
}
