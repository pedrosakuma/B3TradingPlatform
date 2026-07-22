#!/usr/bin/env python3
"""Static smoke checks for docker-compose auth-mode wiring.

This intentionally uses only the Python standard library so it can run in CI
before any package restore. It catches the #608 class of regression where the
frontend AUTH_MODE changes but trading-host remains Local and therefore never
maps /api/auth/exchange.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
COMPOSE = ROOT / "docker" / "docker-compose.yml"
ENV_EXAMPLE = ROOT / "docker" / ".env.example"

compose = COMPOSE.read_text(encoding="utf-8")
env_example = ENV_EXAMPLE.read_text(encoding="utf-8")

required_pairs = {
    "backend mode": "Trading__Auth__Mode: ${AUTH_MODE:-Local}",
    "frontend mode": "AUTH_MODE: ${AUTH_MODE:-Local}",
    "backend local-login flag": "Trading__Auth__LocalLoginEnabled: ${AUTH_LOCAL_LOGIN_ENABLED:-}",
    "frontend local-login flag": "AUTH_LOCAL_LOGIN_ENABLED: ${AUTH_LOCAL_LOGIN_ENABLED:-}",
    "backend signup flag": "Trading__Auth__SignupEnabled: ${AUTH_SIGNUP_ENABLED:-}",
    "frontend signup flag": "AUTH_SIGNUP_ENABLED: ${AUTH_SIGNUP_ENABLED:-}",
    "backend totp flag": "Trading__Auth__TotpEnabled: ${AUTH_TOTP_ENABLED:-}",
    "frontend totp flag": "AUTH_TOTP_ENABLED: ${AUTH_TOTP_ENABLED:-}",
    "backend authority": "Trading__Auth__ExternalIdentity__Authority: ${AUTH_AUTHORITY:-}",
    "frontend authority": "AUTH_AUTHORITY: ${AUTH_AUTHORITY:-}",
    "backend issuer": "Trading__Auth__ExternalIdentity__Issuer: ${AUTH_ISSUER:-}",
    "backend tenant": "Trading__Auth__ExternalIdentity__TenantId: ${AUTH_TENANT_ID:-}",
    "backend audience": "Trading__Auth__ExternalIdentity__Audience: ${AUTH_API_AUDIENCE:-}",
    "frontend scope": "AUTH_API_SCOPE: ${AUTH_API_SCOPE:-}",
    "backend scope": "Trading__Auth__ExternalIdentity__RequiredScope: ${AUTH_REQUIRED_SCOPE:-}",
    "backend allowed SPA": "Trading__Auth__ExternalIdentity__AllowedClientApplicationIds__0: ${AUTH_CLIENT_ID:-}",
    "frontend client ID": "AUTH_CLIENT_ID: ${AUTH_CLIENT_ID:-}",
}

missing = [label for label, needle in required_pairs.items() if needle not in compose]
if missing:
    raise SystemExit("docker-compose auth wiring missing: " + ", ".join(missing))

for name in [
    "AUTH_MODE=Local",
    "# AUTH_AUTHORITY=",
    "# AUTH_ISSUER=",
    "# AUTH_TENANT_ID=",
    "# AUTH_CLIENT_ID=",
    "# AUTH_API_SCOPE=",
    "# AUTH_API_AUDIENCE=",
    "# AUTH_REQUIRED_SCOPE=",
    "# AUTH_KNOWN_AUTHORITIES=",
]:
    if name not in env_example:
        raise SystemExit(f"docker/.env.example missing {name}")

if "Hybrid/Entra" not in compose or "fails closed" not in compose:
    raise SystemExit("compose must document Hybrid/Entra fail-closed ExternalIdentity requirements")

print("compose auth config smoke passed")
