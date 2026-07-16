export function classifyAuthResponse(response) {
  if (response
      && typeof response.token === "string"
      && response.token.length > 0
      && typeof response.expiresAt === "string"
      && response.expiresAt.length > 0) {
    return { kind: "session", response };
  }

  if (response?.requires2fa === true
      && typeof response.totpChallengeToken === "string"
      && response.totpChallengeToken.length > 0) {
    return { kind: "totp", totpChallengeToken: response.totpChallengeToken };
  }

  if (response?.requires2faEnrollment === true
      && typeof response.enrollmentToken === "string"
      && response.enrollmentToken.length > 0) {
    return { kind: "enrollment", enrollmentToken: response.enrollmentToken };
  }

  throw new Error("Authentication response did not include a valid session or challenge.");
}

export function requireEnrollmentResponse(response) {
  if (!response
      || typeof response.secret !== "string"
      || response.secret.length === 0
      || typeof response.otpauthUri !== "string"
      || response.otpauthUri.length === 0
      || !Array.isArray(response.recoveryCodes)
      || typeof response.totpChallengeToken !== "string"
      || response.totpChallengeToken.length === 0) {
    throw new Error("Enrollment response did not include a verification challenge.");
  }
  return response;
}
