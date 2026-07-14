#!/bin/sh
# Renders /usr/share/nginx/html/js/env.js from the checked-in template,
# substituting JSON-escaped ${MARKETDATA_WS_URL} and ${APP_TITLE}. Runs as
# part of the stock nginx image's
# /docker-entrypoint.d/ hook chain, so it executes on every `docker run` /
# Kubernetes pod start before nginx boots.
#
# Numbered 20- (before 25-render-nginx-conf.sh) purely for readability —
# the two scripts render unrelated files (a JS config vs nginx.conf) and
# have no ordering dependency on each other.
#
# MARKETDATA_WS_URL lets non-Docker orchestrators (e.g. Kubernetes/AKS,
# where the marketdata WS is exposed on a shared LB IP + distinct port, not
# the dev docker-compose convention's <host>:8081) ship a correct
# out-of-the-box default for the "Market Data" panel instead of every
# operator pasting the URL in by hand. See #572. Defaults to "", which
# preserves today's behavior (js/protocol.js's defaultMarketDataUrl() falls
# back to its localhost/127.0.0.1 dev guess, then "").
#
# APP_TITLE lets deployers override the browser/login/app-shell brand text
# without rebuilding the static frontend. Defaults to "B3TradingPlatform".
set -e

js_string_literal() {
    printf '%s' "$1" | awk '
        BEGIN { printf "\"" }
        {
            gsub(/\\/, "\\\\");
            gsub(/"/, "\\\"");
            gsub(/\r/, "\\r");
            gsub(/\t/, "\\t");
            if (NR > 1) printf "\\n";
            printf "%s", $0;
        }
        END { printf "\"" }
    '
}

: "${MARKETDATA_WS_URL:=}"
: "${APP_TITLE:=B3TradingPlatform}"

marketdata_ws_url_json=$(js_string_literal "$MARKETDATA_WS_URL")
app_title_json=$(js_string_literal "$APP_TITLE")

marketdata_ws_url_json=$(printf '%s' "$marketdata_ws_url_json" | sed 's/[&|\\]/\\&/g')
app_title_json=$(printf '%s' "$app_title_json" | sed 's/[&|\\]/\\&/g')

sed \
    -e "s|__MARKETDATA_WS_URL_JSON__|$marketdata_ws_url_json|g" \
    -e "s|__APP_TITLE_JSON__|$app_title_json|g" \
    /etc/nginx/env.js.template \
    > /usr/share/nginx/html/js/env.js
