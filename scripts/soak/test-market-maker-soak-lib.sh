#!/usr/bin/env bash
set -euo pipefail

readonly ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
source "$ROOT/scripts/soak/market-maker-soak-lib.sh"

zero_sample="$(printf '%s\n' \
    '{"data":{"result":[{"value":[0,"0"]}]}}' | soak_metric_sample)"
[[ "$(jq -r '.present' <<<"$zero_sample")" == "true" ]]
[[ "$(jq -r '.value' <<<"$zero_sample")" == "0" ]]

absent_sample="$(printf '%s\n' '{"data":{"result":[]}}' | soak_metric_sample)"
[[ "$(jq -r '.present' <<<"$absent_sample")" == "false" ]]
[[ "$(jq -r '.value' <<<"$absent_sample")" == "null" ]]

passing_outage='{
  "expectedSymbolCount": 3,
  "pre": {
    "present": true,
    "feedIneligibleTransitions": 0,
    "quoteSuppressions": 0,
    "feedUnavailableCancels": 0,
    "ordersSubmitted": 12,
    "openOrders": 6,
    "eligibleSymbols": 3
  },
  "settled": {
    "present": true,
    "feedIneligibleTransitions": 3,
    "quoteSuppressions": 1,
    "feedUnavailableCancels": 6,
    "ordersSubmitted": 12,
    "openOrders": 0,
    "eligibleSymbols": 0
  },
  "holdSamples": [
    {
      "present": true,
      "feedIneligibleTransitions": 3,
      "quoteSuppressions": 4,
      "feedUnavailableCancels": 6,
      "ordersSubmitted": 12,
      "openOrders": 0,
      "eligibleSymbols": 0
    }
  ],
  "post": {
    "present": true,
    "feedIneligibleTransitions": 6,
    "quoteSuppressions": 4,
    "feedUnavailableCancels": 6,
    "ordersSubmitted": 18,
    "openOrders": 6,
    "eligibleSymbols": 3
  }
}'
[[ "$(soak_evaluate_outage_telemetry <<<"$passing_outage" | jq -r '.passed')" == "true" ]]
failing_outage="$(jq '.holdSamples[0].ordersSubmitted = 13' <<<"$passing_outage")"
[[ "$(soak_evaluate_outage_telemetry <<<"$failing_outage" | jq -r '.passed')" == "false" ]]
settling_submission="$(jq '.settled.ordersSubmitted = 13 | .holdSamples[0].ordersSubmitted = 13' \
    <<<"$passing_outage")"
[[ "$(soak_evaluate_outage_telemetry <<<"$settling_submission" | jq -r '.passed')" == "false" ]]
missing_outage="$(jq '.settled.present = false' <<<"$passing_outage")"
[[ "$(soak_evaluate_outage_telemetry <<<"$missing_outage" | jq -r '.passed')" == "false" ]]
no_suppression="$(jq '.holdSamples[0].quoteSuppressions = 0' <<<"$passing_outage")"
[[ "$(soak_evaluate_outage_telemetry <<<"$no_suppression" | jq -r '.passed')" == "false" ]]

scratch="$ROOT/soak-artifacts/self-test"
rm -rf "$scratch"
mkdir -p "$scratch"
steps_seen="$scratch/steps"
cleanup_errors="$scratch/cleanup-errors.json"
step_ok() { printf 'ok\n' >>"$steps_seen"; }
step_fail() { printf 'fail\n' >>"$steps_seen"; return 9; }
step_after() { printf 'after\n' >>"$steps_seen"; }
if soak_run_cleanup_steps "$cleanup_errors" \
    ok step_ok \
    teardown step_fail \
    after step_after; then
    echo "ERROR: cleanup aggregation accepted a failed teardown" >&2
    exit 1
fi
[[ "$(cat "$steps_seen")" == $'ok\nfail\nafter' ]]
[[ "$(jq -r '.[0].step + ":" + (.[0].exitCode | tostring)' "$cleanup_errors")" == "teardown:9" ]]
[[ "$(soak_cleanup_exit_status 7 1)" == "7" ]]
[[ "$(soak_cleanup_exit_status 0 1)" == "1" ]]
[[ "$(soak_cleanup_exit_status 0 0)" == "0" ]]
summary="$scratch/summary.json"
printf '%s\n' '{"passed":true,"acceptanceEligible":true,"evidenceClass":"acceptance-profile"}' >"$summary"
soak_apply_cleanup_to_summary "$summary" \
    '{"passed":false,"originalExitCode":0,"cleanupExitCode":1,"errors":[{"step":"compose-down","exitCode":1}]}'
jq -e '
  (.passed | not) and
  (.acceptanceEligible | not) and
  .evidenceClass == "failed-cleanup" and
  .cleanup.errors[0].step == "compose-down"
' "$summary" >/dev/null

rm -rf "$scratch"
rmdir "$ROOT/soak-artifacts" 2>/dev/null || true
printf 'PASS: market-maker soak helper self-tests\n'
