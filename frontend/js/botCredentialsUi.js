// User-bot credentials view (sub-issue #169 of RFC user-bot-fixp-listener-v0).
//
// Render-only DOM layer for the credentials list, the create form, and
// the "shown once" plaintext-PAT modal. Network calls are owned by app.js;
// this module exposes handlers + render hooks the same way adminUi.js
// does so the auth/fetch path stays in one place.
//
// SECURITY INVARIANTS (do not break these without re-reviewing #169):
//   * The plaintext PAT (`b3t_xxx_yyy`) returned by POST /api/user-bot-credentials
//     lives ONLY in the secret-modal input element while the modal is
//     open. It is never written to localStorage / sessionStorage /
//     IndexedDB / cookies, never appended to a URL, and never logged.
//   * Dismissing the modal blanks the input value so the secret leaves
//     the DOM as soon as the user is done with it.
//   * The list endpoint never returns the secret, so the table can
//     safely be re-rendered from server state without leaking anything.

const $ = (id) => document.getElementById(id);

let onCreate = () => {};
let onRevoke = () => {};
let onSetCertBinding = () => {};
let onRefresh = () => {};
let onBack = () => {};
let onOpenView = () => {};

// In-memory only. Rows are mirrored from /api/user-bot-credentials each
// refresh; never serialised, never inspected for secret material.
let rowsCache = null;
let loading = false;

// Tracks whether a create request is currently in flight, so the ack
// checkbox handler (below) never re-enables the submit button mid-request.
let createSubmitting = false;

export function setBotCredentialsHandlers(handlers) {
  onCreate   = handlers.onCreate   ?? onCreate;
  onRevoke   = handlers.onRevoke   ?? onRevoke;
  onSetCertBinding = handlers.onSetCertBinding ?? onSetCertBinding;
  onRefresh  = handlers.onRefresh  ?? onRefresh;
  onBack     = handlers.onBack     ?? onBack;
  onOpenView = handlers.onOpenView ?? onOpenView;
}

export function bindBotCredentialsUi() {
  const openBtn = $("bot-credentials-open");
  if (openBtn) {
    openBtn.addEventListener("click", () => onOpenView());
  }

  const backBtn = $("bot-credentials-back");
  if (backBtn) {
    backBtn.addEventListener("click", () => onBack());
  }

  const refreshBtn = $("bot-credentials-refresh");
  if (refreshBtn) {
    refreshBtn.addEventListener("click", () => onRefresh());
  }

  // Sandbox/simulation acknowledgment gate (RFC user-bot-fixp-listener-v0,
  // docs/SANDBOX-AND-LEGAL.md §3: "Gate credential issuance ... until an
  // in-app ToS gate ships"). The checkbox is the client-side half of that
  // gate — the create button stays disabled until it's checked.
  const ackEl = $("bot-credentials-ack");
  const createBtn = $("bot-credentials-create-submit");
  if (ackEl && createBtn) {
    ackEl.addEventListener("change", () => {
      createBtn.disabled = createSubmitting || !ackEl.checked;
    });
  }

  const form = $("bot-credentials-create-form");
  if (form) {
    form.addEventListener("submit", (e) => {
      e.preventDefault();
      if (ackEl && !ackEl.checked) {
        setBotCredentialsFeedback("Please acknowledge the sandbox notice above first.", "error");
        return;
      }
      const labelEl = $("bot-credentials-label");
      const label = (labelEl?.value ?? "").trim();
      if (!label) {
        setBotCredentialsFeedback("Label is required.", "error");
        return;
      }
      const thumbprintEl = $("bot-credentials-cert-thumbprint");
      const normalizedThumbprint = normalizeCertThumbprint(thumbprintEl?.value ?? "");
      if (normalizedThumbprint === false) {
        setBotCredentialsFeedback(
          "Client cert thumbprint must be 64 hexadecimal characters (colons and whitespace are allowed).",
          "error");
        return;
      }
      onCreate({ label, boundCertThumbprint: normalizedThumbprint });
    });
  }

  const body = $("bot-credentials-body");
  if (body) {
    body.addEventListener("click", (e) => {
      const editBtn = e.target.closest(".bot-cred-edit-pin");
      if (editBtn) {
        const id = editBtn.dataset.id;
        const label = editBtn.dataset.label || id;
        const current = editBtn.dataset.thumbprint || "";
        if (!id) return;
        const next = window.prompt(
          `Client cert SHA-256 thumbprint for ${label} (leave empty to clear):`,
          current,
        );
        if (next === null) return;
        const normalizedThumbprint = normalizeCertThumbprint(next);
        if (normalizedThumbprint === false) {
          setBotCredentialsFeedback(
            "Client cert thumbprint must be 64 hexadecimal characters (colons and whitespace are allowed).",
            "error");
          return;
        }
        onSetCertBinding({ id, label, boundCertThumbprint: normalizedThumbprint });
        return;
      }

      const revokeBtn = e.target.closest(".bot-cred-revoke");
      if (!revokeBtn) return;
      const id = revokeBtn.dataset.id;
      const label = revokeBtn.dataset.label || id;
      if (!id) return;
      if (!window.confirm(
        `Revoke credential ${label}? This cannot be undone.`,
      )) return;
      onRevoke({ id, label });
    });
  }

  bindSecretModal();
}

// ── List render ────────────────────────────────────────────────────

export function setBotCredentialsLoading(isLoading) {
  loading = !!isLoading;
  renderList();
}

export function setBotCredentialsRows(rows) {
  rowsCache = Array.isArray(rows) ? rows : [];
  loading = false;
  renderList();
}

export function clearBotCredentials() {
  rowsCache = null;
  loading = false;
  setBotCredentialsFeedback(null);
  resetCreateForm();
  closeBotCredentialsSecretModal();
}

export function resetCreateForm() {
  const labelEl = $("bot-credentials-label");
  const thumbprintEl = $("bot-credentials-cert-thumbprint");
  if (labelEl) labelEl.value = "";
  if (thumbprintEl) thumbprintEl.value = "";
  setCreateSubmitting(false);
}

export function setCreateSubmitting(submitting) {
  const btn = $("bot-credentials-create-submit");
  if (!btn) return;
  createSubmitting = !!submitting;
  const ackEl = $("bot-credentials-ack");
  btn.disabled = createSubmitting || !!(ackEl && !ackEl.checked);
  btn.textContent = createSubmitting ? "Creating…" : "Create credential";
}

export function setBotCredentialsFeedback(message, kind) {
  const el = $("bot-credentials-feedback");
  if (!el) return;
  if (!message) { el.hidden = true; el.textContent = ""; el.className = "feedback"; return; }
  el.hidden = false;
  el.textContent = message;
  el.className = `feedback ${kind === "ok" ? "ok" : kind === "warn" ? "warn" : "error"}`;
}

function renderList() {
  const body = $("bot-credentials-body");
  if (!body) return;
  if (loading && rowsCache == null) {
    body.innerHTML = `<tr><td colspan="6" class="muted">loading…</td></tr>`;
    return;
  }
  const rows = rowsCache ?? [];
  if (rows.length === 0) {
    body.innerHTML =
      `<tr><td colspan="6" class="muted">You have no bot credentials. Create one to get started.</td></tr>`;
    return;
  }
  // Active rows first, then revoked. Within each group, newest first.
  const sorted = [...rows].sort((a, b) => {
    const ar = a.revokedAt ? 1 : 0;
    const br = b.revokedAt ? 1 : 0;
    if (ar !== br) return ar - br;
    return String(b.createdAtUtc).localeCompare(String(a.createdAtUtc));
  });
  body.innerHTML = sorted.map(renderRow).join("");
}

function renderRow(c) {
  const revoked = !!c.revokedAt;
  const created = formatDate(c.createdAtUtc);
  const statusBadge = revoked
    ? `<span class="killed-tag badge badge-danger badge-square badge-uppercase">Revoked</span>`
    : `<span class="status-pill badge badge-uppercase status-connected">Active</span>`;
  const certBinding = renderCertBinding(c.boundCertThumbprint);
  const actions = revoked
    ? ""
    : `<button type="button" class="bot-cred-edit-pin btn btn-link"
               data-id="${escapeHtml(c.id)}" data-label="${escapeHtml(c.label)}"
               data-thumbprint="${escapeHtml(c.boundCertThumbprint ?? "")}">
         Edit pin
       </button>
       <button type="button" class="bot-cred-revoke btn btn-danger btn-sm"
              data-id="${escapeHtml(c.id)}" data-label="${escapeHtml(c.label)}">
         Revoke
       </button>`;
  return `<tr>
    <td>${escapeHtml(c.label)}</td>
    <td><code>${escapeHtml(c.credShortId)}</code></td>
    <td>${escapeHtml(created)}</td>
    <td>${certBinding}</td>
    <td>${statusBadge}</td>
    <td>${actions}</td>
  </tr>`;
}

function renderCertBinding(boundCertThumbprint) {
  const value = String(boundCertThumbprint ?? "").trim();
  if (!value) return `<span class="muted">unpinned</span>`;
  const short = `${value.slice(0, 4)}…${value.slice(-4)}`;
  return `<span class="status-pill badge badge-neutral" title="${escapeHtml(value)}">pinned: <code>${escapeHtml(short)}</code></span>`;
}

// ── "Shown once" secret modal ──────────────────────────────────────

let secretModalSubmit = null;
let secretModalCopy = null;
let secretCopyTimer = null;

function bindSecretModal() {
  const form = $("bot-credentials-secret-form");
  const copyBtn = $("bot-credentials-secret-copy");
  if (form && !secretModalSubmit) {
    secretModalSubmit = (e) => {
      e.preventDefault();
      closeBotCredentialsSecretModal();
    };
    form.addEventListener("submit", secretModalSubmit);
  }
  if (copyBtn && !secretModalCopy) {
    secretModalCopy = async () => {
      const input = $("bot-credentials-secret-value");
      if (!input) return;
      const value = input.value;
      if (!value) return;
      const ok = await copyToClipboard(value, input);
      setSecretCopyStatus(ok ? "Copied!" : "Copy failed — please copy manually.", ok ? "ok" : "error");
    };
    copyBtn.addEventListener("click", secretModalCopy);
  }
}

export function openBotCredentialsSecretModal({ label, plainSecret }) {
  const modal = $("bot-credentials-secret-modal");
  const labelEl = $("bot-credentials-secret-label");
  const input = $("bot-credentials-secret-value");
  if (!modal || !input) return;
  if (labelEl) labelEl.textContent = label ?? "";
  // The secret never enters any storage — it's pushed directly into the
  // input's value property and dropped on close. Do not log this value.
  input.value = plainSecret ?? "";
  setSecretCopyStatus(null);
  modal.hidden = false;
  // Defer focus so the readonly input is selected and ready to copy.
  requestAnimationFrame(() => {
    try {
      input.focus();
      input.select();
    } catch { /* ignore — focus is a nice-to-have */ }
  });
}

export function closeBotCredentialsSecretModal() {
  const modal = $("bot-credentials-secret-modal");
  const input = $("bot-credentials-secret-value");
  const labelEl = $("bot-credentials-secret-label");
  if (input) input.value = "";
  if (labelEl) labelEl.textContent = "";
  setSecretCopyStatus(null);
  if (modal) modal.hidden = true;
}

function setSecretCopyStatus(message, kind) {
  const el = $("bot-credentials-secret-copy-status");
  if (!el) return;
  if (secretCopyTimer) { clearTimeout(secretCopyTimer); secretCopyTimer = null; }
  if (!message) { el.hidden = true; el.textContent = ""; el.className = "feedback"; return; }
  el.hidden = false;
  el.textContent = message;
  el.className = `feedback ${kind === "ok" ? "ok" : "error"}`;
  if (kind === "ok") {
    secretCopyTimer = setTimeout(() => {
      el.hidden = true;
      el.textContent = "";
      secretCopyTimer = null;
    }, 2_500);
  }
}

async function copyToClipboard(value, fallbackInput) {
  // Prefer the modern async Clipboard API (requires a secure context).
  // Fall back to selecting the input + execCommand("copy") so http://
  // dev hosts and older browsers still work without leaking the value.
  try {
    if (navigator?.clipboard?.writeText) {
      await navigator.clipboard.writeText(value);
      return true;
    }
  } catch { /* fall through to legacy path */ }
  try {
    if (fallbackInput) {
      fallbackInput.focus();
      fallbackInput.select();
      const ok = document.execCommand && document.execCommand("copy");
      return !!ok;
    }
  } catch { /* ignore */ }
  return false;
}

// ── Helpers ────────────────────────────────────────────────────────

function formatDate(s) {
  if (!s) return "—";
  const d = new Date(s);
  if (Number.isNaN(d.getTime())) return String(s);
  // ISO-ish, seconds-precision; matches the look of the executions log.
  return d.toISOString().replace("T", " ").slice(0, 19) + "Z";
}

function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => (
    { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]
  ));
}

function normalizeCertThumbprint(value) {
  const normalized = String(value ?? "").replace(/[\s:]/g, "").toUpperCase();
  if (!normalized) return null;
  return /^[0-9A-F]{64}$/.test(normalized) ? normalized : false;
}
