#!/bin/sh
# Renders /usr/share/nginx/html/js/env.js from the checked-in template,
# substituting ${MARKETDATA_WS_URL}. Runs as part of the stock nginx image's
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
set -e

: "${MARKETDATA_WS_URL:=}"

envsubst '${MARKETDATA_WS_URL}' \
    < /etc/nginx/env.js.template \
    > /usr/share/nginx/html/js/env.js
