#!/usr/bin/env bash
set -euo pipefail

readonly ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
readonly runner="${SOAK_SUITE_PROFILE_RUNNER:-$ROOT/scripts/soak/run-market-maker-soak.sh}"
readonly suite_manifest="${SOAK_SUITE_MANIFEST:?SOAK_SUITE_MANIFEST is required}"
readonly suite_root="$(dirname "$suite_manifest")"
readonly suite_id="${SOAK_SUITE_ID:-$(basename "$suite_root")}"
readonly profiles=(baseline inventory-skew volatility-spread pause-and-cancel)

run_profile() {
    local profile="$1"
    local -a arguments=(--profile "$profile")
    if [[ "$profile" != "baseline" ]]; then
        arguments+=(--no-build)
    fi

    SOAK_PROJECT_NAME="${suite_id}-${profile}" \
    SOAK_ARTIFACTS_DIR="${suite_root}/${profile}" \
        "$runner" "${arguments[@]}"
}

for profile in "${profiles[@]}"; do
    run_profile "$profile"
done
