#!/bin/sh
set -eu

# Builds the actual nginx:alpine frontend image, runs the real
# /docker-entrypoint.d/20-render-env-js.sh inside a container, then parses the
# emitted env.js under Node to ensure adversarial values stay inert strings.

IMAGE_TAG="${1:-b3trading-frontend-env-smoke:local}"

docker build -t "$IMAGE_TAG" frontend >/dev/null

run_case() {
    name=$1
    app_title=$2
    marketdata_ws_url=$3
    auth_authority=${4:-}
    auth_known_authorities=${5:-}

    expected_app_title_b64=$(printf '%s' "$app_title" | base64 | tr -d '\n')
    expected_marketdata_ws_url_b64=$(printf '%s' "$marketdata_ws_url" | base64 | tr -d '\n')
    expected_auth_authority_b64=$(printf '%s' "$auth_authority" | base64 | tr -d '\n')

    printf 'smoke: %s\n' "$name" >&2

    docker run --rm \
        --entrypoint /bin/sh \
        -e "APP_TITLE=$app_title" \
        -e "MARKETDATA_WS_URL=$marketdata_ws_url" \
        -e "AUTH_MODE=Entra" \
        -e "AUTH_AUTHORITY=$auth_authority" \
        -e "AUTH_CLIENT_ID=spa-client" \
        -e "AUTH_API_SCOPE=api://trading/access_as_user" \
        -e "AUTH_KNOWN_AUTHORITIES=$auth_known_authorities" \
        "$IMAGE_TAG" \
        -c '/docker-entrypoint.d/20-render-env-js.sh && cat /usr/share/nginx/html/js/env.js' \
    | EXPECTED_APP_TITLE_B64="$expected_app_title_b64" \
      EXPECTED_MARKETDATA_WS_URL_B64="$expected_marketdata_ws_url_b64" \
      EXPECTED_AUTH_AUTHORITY_B64="$expected_auth_authority_b64" \
      CASE_NAME="$name" \
      node -e '
const fs = require("fs");
const vm = require("vm");

const source = fs.readFileSync(0, "utf8");
const context = {
  window: {},
  document: { cookie: "session=secret" },
  alert() {
    throw new Error(`rendered env.js executed alert() for ${process.env.CASE_NAME}`);
  },
};

vm.runInNewContext(source, context, { filename: "env.js" });

const actualAppTitle = context.window?.__B3_CONFIG__?.appTitle;
const actualMarketDataWsUrl = context.window?.__B3_CONFIG__?.marketDataWsUrl;
const actualAuth = context.window?.__B3_CONFIG__?.auth;

if (Buffer.from(actualAppTitle ?? "", "utf8").toString("base64") !== process.env.EXPECTED_APP_TITLE_B64) {
  throw new Error(`${process.env.CASE_NAME}: appTitle mismatch`);
}

if (Buffer.from(actualMarketDataWsUrl ?? "", "utf8").toString("base64") !== process.env.EXPECTED_MARKETDATA_WS_URL_B64) {
  throw new Error(`${process.env.CASE_NAME}: marketDataWsUrl mismatch`);
}
if (actualAuth?.mode !== "Entra") {
  throw new Error(`${process.env.CASE_NAME}: auth.mode mismatch`);
}
if (Buffer.from(actualAuth?.authority ?? "", "utf8").toString("base64") !== process.env.EXPECTED_AUTH_AUTHORITY_B64) {
  throw new Error(`${process.env.CASE_NAME}: auth.authority mismatch`);
}
if (!Array.isArray(actualAuth?.knownAuthorities)) {
  throw new Error(`${process.env.CASE_NAME}: auth.knownAuthorities must be an array`);
}
'
}

run_case \
    'default-safe-text' \
    'B3TradingPlatform' \
    'ws://localhost:8081/ws' \
    'https://tenant.ciamlogin.com/tenant/v2.0' \
    'tenant.ciamlogin.com'

run_case \
    'quote-breakout-attempt' \
    'Acme\"; alert(document.cookie); //' \
    'wss://md.example/ws?x=1&note=\"quoted\"'

run_case \
    'slashes-backticks-shell-metacharacters' \
    'Desk \\ Backtick ` $HOME & pipes | semis ; stays inert' \
    'wss://md.example/ws?path=\\desk\\feed&cmd=`echo nope`&raw=$HOME&join=a&b|c'

run_case \
    'control-characters' \
    "$(printf 'Line 1\nLine\t2\rLine 3\b\fDone')" \
    "$(printf 'wss://md.example/ws?line=1\nline=2\tend\r')"

run_case \
    'placeholder-token-cross-talk' \
    '__MARKETDATA_WS_URL_JSON__' \
    '__APP_TITLE_JSON__'

printf 'render smoke passed for %s\n' "$IMAGE_TAG" >&2
