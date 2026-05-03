// Tiny JWT helper. We do NOT verify the signature here — the backend
// is the source of truth for authorization. The decoded payload is
// used only for cosmetic UX decisions (showing/hiding admin areas,
// labelling the user). Treat any value derived from this as untrusted.

export function decodeJwt(token) {
  if (typeof token !== "string") return null;
  const parts = token.split(".");
  if (parts.length !== 3) return null;
  try {
    const payload = parts[1].replace(/-/g, "+").replace(/_/g, "/");
    const padded = payload + "=".repeat((4 - (payload.length % 4)) % 4);
    const json = atob(padded);
    return JSON.parse(json);
  } catch {
    return null;
  }
}

export function claimsFromToken(token) {
  const payload = decodeJwt(token) ?? {};
  return {
    role: typeof payload.role === "string" ? payload.role : null,
    firm: typeof payload.firm === "string" ? payload.firm : null,
    sub:  typeof payload.sub  === "string" ? payload.sub  : null,
  };
}
