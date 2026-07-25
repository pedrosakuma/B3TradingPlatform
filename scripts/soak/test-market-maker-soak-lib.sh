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

[[ "$(soak_duration_to_seconds 00:00:12)" == "12" ]]
[[ "$(soak_duration_to_seconds 01:02:03)" == "3723" ]]
if soak_duration_to_seconds 12s >/dev/null 2>&1; then
    echo "ERROR: invalid MaxReferenceAge duration was accepted" >&2
    exit 1
fi
soak_validate_strict_refresh_timing 12 3 6 6
if soak_validate_strict_refresh_timing 12 6 6 6; then
    echo "ERROR: refresh cadence without freshness margin was accepted" >&2
    exit 1
fi

accelerated_freshness="$(jq -n '
  ["PETR4","VALE3","ITUB4"] as $symbols |
  def refresh($timestamp; $segment; $symbol; $direction):
    {
      timestampUtc:$timestamp,
      segment:$segment,
      phase:"accelerated-refresh",
      symbol:$symbol,
      direction:$direction
    };
  def sample($timestamp; $phase; $symbol; $metric; $value):
    {
      timestamp_utc:$timestamp,
      phase:$phase,
      metric:$metric,
      labels:{symbol:$symbol},
      value:$value
    };
  {
    configuredSymbols: $symbols,
    maxReferenceAgeSeconds: 12,
    refreshIntervalSeconds: 3,
    refreshMarginSeconds: 6,
    refreshes: [
      $symbols[] as $symbol |
      refresh("2026-07-24T00:00:00Z";"pre-outage";$symbol;"trading-buys"),
      refresh("2026-07-24T00:00:03Z";"pre-outage";$symbol;"counterparty-buys"),
      refresh("2026-07-24T00:00:06Z";"pre-outage";$symbol;"trading-buys"),
      refresh("2026-07-24T00:00:20Z";"post-recovery";$symbol;"counterparty-buys"),
      refresh("2026-07-24T00:00:23Z";"post-recovery";$symbol;"trading-buys")
    ],
    metricSamples: [
      $symbols[] as $symbol |
      sample("2026-07-24T00:00:01Z";"initial";$symbol;"bot_market_data_reference_eligible_current";1),
      sample("2026-07-24T00:00:01Z";"initial";$symbol;"bot_market_data_reference_age_seconds";1),
      sample("2026-07-24T00:00:07Z";"pre-outage";$symbol;"bot_market_data_reference_eligible_current";1),
      sample("2026-07-24T00:00:07Z";"pre-outage";$symbol;"bot_market_data_reference_age_seconds";1),
      sample("2026-07-24T00:00:21Z";"recovered";$symbol;"bot_market_data_reference_eligible_current";1),
      sample("2026-07-24T00:00:21Z";"recovered";$symbol;"bot_market_data_reference_age_seconds";1),
      sample("2026-07-24T00:00:25Z";"final";$symbol;"bot_market_data_reference_eligible_current";1),
      sample("2026-07-24T00:00:25Z";"final";$symbol;"bot_market_data_reference_age_seconds";2)
    ]
  }
')"
accelerated_freshness_report="$(soak_evaluate_strict_freshness <<<"$accelerated_freshness")"
[[ "$(jq -r '.passed' <<<"$accelerated_freshness_report")" == "true" ]]
[[ "$(jq -r '.refreshSummary | length' <<<"$accelerated_freshness_report")" == "3" ]]
unbounded_direction="$(jq '
  .refreshes |= map(
    if .segment == "pre-outage" then .direction = "trading-buys" else . end
  )
' <<<"$accelerated_freshness")"
[[ "$(soak_evaluate_strict_freshness <<<"$unbounded_direction" | jq -r '.passed')" == "false" ]]

stopped_refresh="$(jq '
  .refreshes |= map(select(
    .segment != "pre-outage" or .timestampUtc == "2026-07-24T00:00:00Z"
  )) |
  .metricSamples |= map(
    if .phase == "pre-outage" and
       .metric == "bot_market_data_reference_eligible_current"
    then .value = 0
    elif .phase == "pre-outage" and
         .metric == "bot_market_data_reference_age_seconds"
    then .value = 13
    else . end
  )
' <<<"$accelerated_freshness")"
stopped_refresh_report="$(soak_evaluate_strict_freshness <<<"$stopped_refresh")"
[[ "$(jq -r '.passed' <<<"$stopped_refresh_report")" == "false" ]]
[[ "$(jq '[.symbolFreshness[].failedSamples[]] | length > 0' \
    <<<"$stopped_refresh_report")" == "true" ]]

phase_step_calls=""
phase_step_ok() {
    phase_step_calls+="${1}:ok "
}
phase_step_fail() {
    phase_step_calls+="${1}:fail "
    return 23
}
for critical_phase in pre-outage-stabilization reconnected-no-reference; do
    if soak_run_required_phase_step "$critical_phase" capture-metrics phase_step_fail \
        2>/dev/null; then
        echo "ERROR: $critical_phase accepted a failed mandatory metric capture" >&2
        exit 1
    fi
    if soak_run_required_phase_step "$critical_phase" capture-outage-snapshot phase_step_fail \
        2>/dev/null; then
        echo "ERROR: $critical_phase accepted a failed mandatory outage query" >&2
        exit 1
    fi
    soak_run_required_phase_step "$critical_phase" capture-metrics phase_step_ok
done
[[ "$phase_step_calls" == \
    "pre-outage-stabilization:fail pre-outage-stabilization:fail pre-outage-stabilization:ok reconnected-no-reference:fail reconnected-no-reference:fail reconnected-no-reference:ok " ]]

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

monitor_stable=true
sleep 30 &
early_monitor_pid=$!
kill "$early_monitor_pid"
wait "$early_monitor_pid" 2>/dev/null || true
if ! soak_stop_event_monitor "$early_monitor_pid"; then
    monitor_stable=false
fi
if $monitor_stable; then
    echo "ERROR: an unexpectedly exited lifecycle monitor could still pass" >&2
    exit 1
fi

sleep 30 &
intentional_monitor_pid=$!
soak_stop_event_monitor "$intentional_monitor_pid"

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
