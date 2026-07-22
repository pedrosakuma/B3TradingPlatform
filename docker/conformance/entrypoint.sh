#!/usr/bin/env bash
# Conformance runner entrypoint.
#
# Validates env vars + process liveness before invoking dotnet test, so
# misconfiguration fails loudly instead of producing a green run with
# zero tests executed (the bare ConformanceFactAttribute would skip).
#
# Exit codes follow the BSD sysexits convention so CI can tell apart:
#   64 EX_USAGE       — missing/invalid env
#   69 EX_UNAVAILABLE — trading-host not reachable in time
#   78 EX_CONFIG      — login preflight failed (creds wrong / store empty)
#   <test-runner exit code> — anything from dotnet test itself

set -euo pipefail

readonly EX_USAGE=64
readonly EX_UNAVAILABLE=69
readonly EX_CONFIG=78

require_env() {
    local name=$1
    local value=${!name:-}
    if [[ -z $value ]]; then
        echo "ERROR: $name is required (see docs/DOCKER.md 'Running conformance')" >&2
        exit $EX_USAGE
    fi
}

require_env B3T_BASE_URL
require_env B3T_AUTH_USER
require_env B3T_AUTH_PASS

# Force the test infrastructure to fail (not skip) if anything in
# PlatformEndpoint.TryResolve trips — we already validated env above, so
# any skip from this point is a config/contract drift we want to see.
export B3T_REQUIRE_CONFIGURED=true

# Strip a single trailing slash so $url/live doesn't collapse into
# // and trip the most paranoid reverse proxies.
base_url=${B3T_BASE_URL%/}
live_url=${B3T_LIVE_URL:-$base_url/live}
ready_url=${B3T_READY_URL:-$base_url/ready}
health_url=$base_url/health
login_url=$base_url/api/auth/login

echo "[conformance] target: $base_url"
echo "[conformance] waiting for $live_url ..."

# Process preflight must not require order-ingress readiness: Unavailable mode
# intentionally returns 503 from /ready while remaining a healthy conformance
# target. Individual specs assert /ready semantics. Total wait budget: 60 s.
deadline=$((SECONDS + 60))
until curl --silent --show-error --fail --max-time 3 -o /dev/null "$live_url"; do
    if (( SECONDS >= deadline )); then
        echo "ERROR: $live_url never became live within 60s" >&2
        exit $EX_UNAVAILABLE
    fi
    sleep 1
done

echo "[conformance] /live ok."

# Most conformance runs submit orders and therefore need order-ingress
# readiness, not merely a live process. Unavailable-mode runs are the
# exception: /ready is expected to stay 503 and the specs assert that.
# B3T_REQUIRE_READY=true|false overrides auto-detection.
require_ready=${B3T_REQUIRE_READY:-auto}
if [[ $require_ready == auto ]]; then
    health_json=$(curl --silent --show-error --fail --max-time 5 "$health_url" || true)
    if [[ $health_json == *'"mode":"Unavailable"'* ]]; then
        require_ready=false
    else
        require_ready=true
    fi
fi

if [[ $require_ready == true ]]; then
    echo "[conformance] waiting for order-ingress readiness at $ready_url ..."
    deadline=$((SECONDS + 60))
    until curl --silent --show-error --fail --max-time 3 -o /dev/null "$ready_url"; do
        if (( SECONDS >= deadline )); then
            echo "ERROR: $ready_url never became ready within 60s" >&2
            exit $EX_UNAVAILABLE
        fi
        sleep 1
    done
    echo "[conformance] /ready ok."
elif [[ $require_ready != false ]]; then
    echo "ERROR: B3T_REQUIRE_READY must be true, false, or auto (got '$require_ready')" >&2
    exit $EX_USAGE
else
    echo "[conformance] skipping /ready wait (Unavailable/process-only run)."
fi

echo "[conformance] preflight login as '$B3T_AUTH_USER' ..."

# Preflight the login so a wrong password / missing seed user fails with
# a clear EX_CONFIG (78) before we spin up the test runner. The actual
# test re-does this through HttpClient — that's intentional.
login_status=$(curl --silent --output /dev/null --write-out '%{http_code}' \
    --max-time 5 \
    -H 'Content-Type: application/json' \
    -d "{\"username\":\"$B3T_AUTH_USER\",\"password\":\"$B3T_AUTH_PASS\"}" \
    "$login_url" || true)

if [[ $login_status != 2* ]]; then
    echo "ERROR: preflight login at $login_url returned HTTP $login_status" >&2
    echo "       (check TRADING_SEED_PASSWORD matches the hash/salt the host was started with)" >&2
    exit $EX_CONFIG
fi

echo "[conformance] preflight ok; running suite ..."
exec dotnet test \
    --configuration Release \
    --no-build \
    --no-restore \
    --logger "console;verbosity=normal" \
    "$@"
