#!/usr/bin/env bash
set -euo pipefail

readonly ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
readonly runner="${SOAK_SUITE_PROFILE_RUNNER:-$ROOT/scripts/soak/run-market-maker-soak.sh}"
readonly suite_manifest="${SOAK_SUITE_MANIFEST:?SOAK_SUITE_MANIFEST is required}"
readonly suite_root="$(dirname "$suite_manifest")"
readonly suite_id="${SOAK_SUITE_ID:-$(basename "$suite_root")}"
readonly profiles=(baseline inventory-skew volatility-spread pause-and-cancel)
readonly suite_log="${SOAK_SUITE_LOG:-${suite_root}/suite-run.log}"

mkdir -p "$suite_root"
: >"$suite_log"

suite_log_line() {
    local line="$1"
    printf '%s\n' "$line"
    printf '%s\n' "$line" >>"$suite_log" || true
}

run_profile() {
    local profile="$1"
    local -a arguments=(--profile "$profile")
    if [[ "$profile" != "baseline" ]]; then
        arguments+=(--no-build)
    fi

    local profile_status=0
    suite_log_line \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ) [mm-soak-suite] starting profile=${profile}"
    SOAK_PROJECT_NAME="${suite_id}-${profile}" \
    SOAK_ARTIFACTS_DIR="${suite_root}/${profile}" \
        "$runner" "${arguments[@]}" \
        > >(tee -a "$suite_log") \
        2> >(tee -a "$suite_log" >&2) ||
        profile_status=$?
    suite_log_line \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ) [mm-soak-suite] finished profile=${profile} exit=${profile_status}"
    return "$profile_status"
}

for profile in "${profiles[@]}"; do
    run_profile "$profile"
done
