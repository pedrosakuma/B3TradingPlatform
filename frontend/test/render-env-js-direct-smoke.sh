#!/bin/sh
set -eu

# Direct regression for 20-render-env-js.sh. Uses project-local scratch files
# (not /tmp) so it can run without Docker and proves inserted JSON values are
# not rescanned as template placeholders.
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
WORK="$ROOT/test/.render-env-js-smoke"
OUT="$WORK/env.js"

cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT INT TERM
cleanup
mkdir -p "$WORK"

APP_TITLE='Desk title'
MARKETDATA_WS_URL='__APP_TITLE_JSON__'
AUTH_AUTHORITY='https://tenant.ciamlogin.com/__AUTH_CLIENT_ID_JSON__/v2.0'
AUTH_CLIENT_ID='__AUTH_API_SCOPE_JSON__'
AUTH_API_SCOPE='api://trading/__AUTH_MODE_JSON__'
AUTH_KNOWN_AUTHORITIES='tenant.ciamlogin.com,__AUTH_AUTHORITY_JSON__'

ENV_JS_TEMPLATE="$ROOT/env.js.template" \
ENV_JS_OUTPUT="$OUT" \
APP_TITLE="$APP_TITLE" \
MARKETDATA_WS_URL="$MARKETDATA_WS_URL" \
AUTH_MODE=Entra \
AUTH_AUTHORITY="$AUTH_AUTHORITY" \
AUTH_CLIENT_ID="$AUTH_CLIENT_ID" \
AUTH_API_SCOPE="$AUTH_API_SCOPE" \
AUTH_KNOWN_AUTHORITIES="$AUTH_KNOWN_AUTHORITIES" \
"$ROOT/20-render-env-js.sh"

EXPECTED_APP_TITLE_B64=$(printf '%s' "$APP_TITLE" | base64 | tr -d '\n') \
EXPECTED_MARKETDATA_WS_URL_B64=$(printf '%s' "$MARKETDATA_WS_URL" | base64 | tr -d '\n') \
EXPECTED_AUTH_AUTHORITY_B64=$(printf '%s' "$AUTH_AUTHORITY" | base64 | tr -d '\n') \
EXPECTED_AUTH_CLIENT_ID_B64=$(printf '%s' "$AUTH_CLIENT_ID" | base64 | tr -d '\n') \
EXPECTED_AUTH_API_SCOPE_B64=$(printf '%s' "$AUTH_API_SCOPE" | base64 | tr -d '\n') \
EXPECTED_AUTH_KNOWN_AUTHORITY_B64=$(printf '%s' '__AUTH_AUTHORITY_JSON__' | base64 | tr -d '\n') \
node "$ROOT/test/verify-rendered-env.mjs" "$OUT"

printf 'direct env.js render smoke passed\n' >&2
