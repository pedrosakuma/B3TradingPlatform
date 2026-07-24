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
  "reconnectHoldSeconds": 10,
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
      "timestampUtc": "2026-07-24T00:00:30Z",
      "present": true,
      "feedIneligibleTransitions": 3,
      "quoteSuppressions": 4,
      "feedUnavailableCancels": 6,
      "ordersSubmitted": 12,
      "openOrders": 0,
      "eligibleSymbols": 0
    }
  ],
  "reconnectedSamples": [
    {
      "timestampUtc": "2026-07-24T00:00:40Z",
      "present": true,
      "feedIneligibleTransitions": 6,
      "quoteSuppressions": 4,
      "feedUnavailableCancels": 6,
      "ordersSubmitted": 12,
      "openOrders": 0,
      "eligibleSymbols": 0
    },
    {
      "timestampUtc": "2026-07-24T00:00:50Z",
      "present": true,
      "feedIneligibleTransitions": 6,
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
stale_epoch_reuse="$(jq '.reconnectedSamples[1].eligibleSymbols = 3 | .reconnectedSamples[1].openOrders = 6' \
    <<<"$passing_outage")"
[[ "$(soak_evaluate_outage_telemetry <<<"$stale_epoch_reuse" | jq -r '.passed')" == "false" ]]
short_reconnect_hold="$(jq '.reconnectedSamples[1].timestampUtc = "2026-07-24T00:00:49Z"' \
    <<<"$passing_outage")"
[[ "$(soak_evaluate_outage_telemetry <<<"$short_reconnect_hold" | jq -r '.passed')" == "false" ]]

stable_counter='{
  "requiredStableCycles": 2,
  "intervalSeconds": 10,
  "samples": [
    {"timestampUtc":"2026-07-24T00:00:00Z","present":true,"ordersSubmitted":12},
    {"timestampUtc":"2026-07-24T00:00:10Z","present":true,"ordersSubmitted":12},
    {"timestampUtc":"2026-07-24T00:00:20Z","present":true,"ordersSubmitted":12}
  ]
}'
[[ "$(soak_evaluate_counter_stabilization <<<"$stable_counter" | jq -r '.passed')" == "true" ]]
[[ "$(soak_evaluate_counter_stabilization <<<"$stable_counter" | jq -r '.intervalSeconds')" == "10" ]]
unstable_counter="$(jq '.samples[2].ordersSubmitted = 13' <<<"$stable_counter")"
[[ "$(soak_evaluate_counter_stabilization <<<"$unstable_counter" | jq -r '.passed')" == "false" ]]
missing_counter="$(jq '.samples[1].present = false' <<<"$stable_counter")"
[[ "$(soak_evaluate_counter_stabilization <<<"$missing_counter" | jq -r '.passed')" == "false" ]]
eventually_stable_counter="$(jq '.samples = [
  {timestampUtc:"2026-07-24T00:00:00Z",present:true,ordersSubmitted:11},
  {timestampUtc:"2026-07-24T00:00:10Z",present:true,ordersSubmitted:12},
  {timestampUtc:"2026-07-24T00:00:20Z",present:true,ordersSubmitted:12},
  {timestampUtc:"2026-07-24T00:00:30Z",present:true,ordersSubmitted:12}
]' <<<"$stable_counter")"
[[ "$(soak_evaluate_counter_stabilization <<<"$eventually_stable_counter" | jq -r '.passed')" == "true" ]]
short_cycle="$(jq '.samples[2].timestampUtc = "2026-07-24T00:00:19Z"' <<<"$stable_counter")"
[[ "$(soak_evaluate_counter_stabilization <<<"$short_cycle" | jq -r '.passed')" == "false" ]]

first_no_build='{
  "manifestExists": false,
  "acceptedRunCount": 0,
  "buildImages": false,
  "gitClean": true,
  "gitSha": "abc123",
  "manifestBuiltFromGitSha": null,
  "pinnedRuntimeImages": null,
  "actualRuntimeImages": [{"service":"trading-host","imageId":"sha256:one","repoDigests":[]}]
}'
[[ "$(soak_evaluate_suite_source_binding <<<"$first_no_build" | jq -r '.passed')" == "false" ]]
first_clean_build="$(jq '.buildImages = true' <<<"$first_no_build")"
[[ "$(soak_evaluate_suite_source_binding <<<"$first_clean_build" | jq -r '.passed')" == "true" ]]
first_dirty_build="$(jq '.buildImages = true | .gitClean = false' <<<"$first_no_build")"
[[ "$(soak_evaluate_suite_source_binding <<<"$first_dirty_build" | jq -r '.passed')" == "false" ]]
empty_manifest_no_build="$(jq '
  .manifestExists = true |
  .manifestBuiltFromGitSha = .gitSha |
  .pinnedRuntimeImages = .actualRuntimeImages
' <<<"$first_no_build")"
[[ "$(soak_evaluate_suite_source_binding <<<"$empty_manifest_no_build" | jq -r '.passed')" == "false" ]]
later_pinned_no_build="$(jq '
  .manifestExists = true |
  .acceptedRunCount = 1 |
  .manifestBuiltFromGitSha = .gitSha |
  .pinnedRuntimeImages = .actualRuntimeImages
' <<<"$first_no_build")"
[[ "$(soak_evaluate_suite_source_binding <<<"$later_pinned_no_build" | jq -r '.passed')" == "true" ]]
later_mismatched_no_build="$(jq '.actualRuntimeImages[0].imageId = "sha256:two"' \
    <<<"$later_pinned_no_build")"
[[ "$(soak_evaluate_suite_source_binding <<<"$later_mismatched_no_build" | jq -r '.passed')" == "false" ]]

scratch="$ROOT/soak-artifacts/self-test"
rm -rf "$scratch"
mkdir -p "$scratch"
curl_argv="$scratch/curl-argv"
curl_header="$scratch/curl-header"
curl_body="$scratch/curl-body"
curl_environment="$scratch/curl-environment"
mock_curl() {
    printf '%s\n' "$@" >"$curl_argv"
    env >"$curl_environment"
    cat <&3 >"$curl_header"
    cat <&4 >"$curl_body"
    printf '{"ok":true}\n'
}
secret_token='argv-test-token-must-not-appear'
secret_body='{"password":"argv-test-password-must-not-appear"}'
SOAK_CURL_BIN=mock_curl \
    soak_curl_json_request POST https://example.invalid secret_token secret_body >/dev/null
! grep -Fq "$secret_token" "$curl_argv"
! grep -Fq 'argv-test-password-must-not-appear' "$curl_argv"
grep -Fq "$secret_token" "$curl_header"
grep -Fq 'argv-test-password-must-not-appear' "$curl_body"
grep -Fxq '@/dev/fd/3' "$curl_argv"
grep -Fxq '@/dev/fd/4' "$curl_argv"
! grep -Fq "$secret_token" "$curl_environment"
! grep -Fq 'argv-test-password-must-not-appear' "$curl_environment"
! grep -Eq '^SOAK_(TRADING|COUNTERPARTY)_PASSWORD=' "$curl_environment"
curl_trace="$scratch/curl-trace"
(
    set -x
    SOAK_CURL_BIN=mock_curl \
        soak_curl_json_request POST https://example.invalid secret_token secret_body >/dev/null
) 2>"$curl_trace"
! grep -Fq "$secret_token" "$curl_trace"
! grep -Fq 'argv-test-password-must-not-appear' "$curl_trace"

export SOAK_TRADING_PASSWORD='environment-test-password'
private_password="$SOAK_TRADING_PASSWORD"
if soak_child_environment_is_secret_free private_password; then
    echo "ERROR: exported password variable was accepted in a child environment" >&2
    exit 1
fi
unset SOAK_TRADING_PASSWORD
soak_child_environment_is_secret_free private_password

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
