#!/usr/bin/env bash

soak_environment_snapshot_is_secret_free() {
    set +x
    local snapshot_variable="$1"
    shift
    local environment_snapshot="${!snapshot_variable-}"
    local wrapped_environment variable_name secret_value
    wrapped_environment=$'\n'"$environment_snapshot"$'\n'
    [[ "$wrapped_environment" != *$'\nSOAK_TRADING_PASSWORD='* ]] || return 1
    [[ "$wrapped_environment" != *$'\nSOAK_COUNTERPARTY_PASSWORD='* ]] || return 1
    for variable_name in "$@"; do
        secret_value="${!variable_name-}"
        [[ -z "$secret_value" || "$environment_snapshot" != *"$secret_value"* ]] || return 1
    done
}

soak_child_environment_is_secret_free() {
    set +x
    local environment_snapshot
    environment_snapshot="$(env)"
    soak_environment_snapshot_is_secret_free environment_snapshot "$@"
}

soak_curl_json_request() {
    local method="$1" url="$2" token_variable="$3" body_variable="$4"
    (
        set +x
        local token="${!token_variable-}" body="${!body_variable-}"
        export -n token body
        local curl_bin="${SOAK_CURL_BIN:-curl}"
        local -a arguments=(-fsS --max-time 10 --request "$method")
        if [[ -n "$token" ]]; then
            exec 3<<<"Authorization: Bearer $token"
            arguments+=(--header @/dev/fd/3)
        fi
        if [[ -n "$body" ]]; then
            exec 4<<<"$body"
            arguments+=(--header 'Content-Type: application/json' --data-binary @/dev/fd/4)
        fi
        if ! soak_child_environment_is_secret_free "$token_variable" "$body_variable"; then
            echo "ERROR: refusing to launch curl with credentials present in its child environment" >&2
            return 1
        fi
        "$curl_bin" "${arguments[@]}" "$url"
    )
}

soak_metric_sample() {
    jq -c '
      (.data.result // []) as $rows |
      {
        present: (($rows | length) > 0),
        seriesCount: ($rows | length),
        value: (
          if ($rows | length) == 0 then null
          else
            [$rows[].value[1] |
              if . == "NaN" or . == "+Inf" or . == "-Inf" then null else tonumber end
            ] as $values |
            if any($values[]; . == null) then null else ($values | add) end
          end
        )
      }
    '
}

soak_run_required_phase_step() {
    local phase="$1" step="$2" function_name="$3" status
    shift 3
    if "$function_name" "$phase" "$@"; then
        return 0
    else
        status=$?
        printf 'ERROR: required soak phase step failed: phase=%s step=%s exit=%s\n' \
            "$phase" "$step" "$status" >&2
        return "$status"
    fi
}

soak_evaluate_outage_telemetry() {
    jq -c '
      . as $evidence |
      ($evidence.holdSamples // []) as $hold |
      ($evidence.reconnectedSamples // []) as $reconnected |
      {
        passed: (
          $evidence.pre.present and
          $evidence.settled.present and
          $evidence.post.present and
          ($hold | length) > 0 and
          ($reconnected | length) >= 2 and
          $evidence.reconnectHoldSeconds > 0 and
          (
            (($reconnected | last).timestampUtc | fromdateiso8601) -
            (($reconnected | first).timestampUtc | fromdateiso8601)
          ) >= $evidence.reconnectHoldSeconds and
          $evidence.settled.feedIneligibleTransitions > $evidence.pre.feedIneligibleTransitions and
          (($hold | last).quoteSuppressions > $evidence.pre.quoteSuppressions) and
          $evidence.settled.feedUnavailableCancels > $evidence.pre.feedUnavailableCancels and
          $evidence.settled.openOrders == 0 and
          $evidence.settled.eligibleSymbols == 0 and
          $evidence.settled.ordersSubmitted == $evidence.pre.ordersSubmitted and
          all($hold[];
            .present and
            .openOrders == 0 and
            .eligibleSymbols == 0 and
            .ordersSubmitted == $evidence.settled.ordersSubmitted) and
          all($reconnected[];
            .present and
            .openOrders == 0 and
            .eligibleSymbols == 0 and
            .ordersSubmitted == $evidence.pre.ordersSubmitted) and
          $evidence.post.openOrders == $evidence.expectedSymbolCount * 2 and
          $evidence.post.eligibleSymbols == $evidence.expectedSymbolCount
        ),
        expected: {
          feedIneligibleTransitions: "settled > pre",
          quoteSuppressions: "last hold > pre",
          feedUnavailableCancels: "settled > pre",
          holdOpenOrders: 0,
          holdEligibleSymbols: 0,
          holdOrdersSubmitted: "unchanged from pre-outage through every hold sample",
          reconnectedWithoutFreshReference:
            "at least two samples with eligibility/openOrders=0 and submissions unchanged",
          reconnectHoldSeconds: $evidence.reconnectHoldSeconds,
          postOpenOrders: ($evidence.expectedSymbolCount * 2),
          postEligibleSymbols: $evidence.expectedSymbolCount
        },
        reconnectHoldSeconds: $evidence.reconnectHoldSeconds,
        observed: $evidence
      }
    '
}

soak_evaluate_counter_stabilization() {
    jq -c '
      (.samples // []) as $samples |
      (.requiredStableCycles // 2) as $cycles |
      (.intervalSeconds // 0) as $interval |
      (.timeoutSeconds // null) as $timeout |
      ($samples[(-($cycles + 1)):] // []) as $stableWindow |
      {
        passed: (
          $cycles >= 2 and
          $interval > 0 and
          ($stableWindow | length) == ($cycles + 1) and
          all($stableWindow[]; .present and .ordersSubmitted != null) and
          ([
            range(1; ($stableWindow | length)) as $index |
            (
              ($stableWindow[$index].timestampUtc | fromdateiso8601) -
              ($stableWindow[$index - 1].timestampUtc | fromdateiso8601)
            ) >= $interval
          ] | all) and
          ([$stableWindow[].ordersSubmitted] | unique | length) == 1
        ),
        requiredStableCycles: $cycles,
        intervalSeconds: $interval,
        timeoutSeconds: $timeout,
        stableWindow: $stableWindow,
        samples: $samples
      }
    '
}

soak_evaluate_suite_source_binding() {
    jq -c '
      (
        (.manifestExists | not) or
        (.acceptedRunCount == 0)
      ) as $requiresSourceBuild |
      (
        if $requiresSourceBuild then
          .buildImages and
          .gitClean and
          (
            .manifestBuiltFromGitSha == null or
            .manifestBuiltFromGitSha == .gitSha
          )
        else
          .manifestBuiltFromGitSha == .gitSha and
          .pinnedRuntimeImages != null and
          .actualRuntimeImages == .pinnedRuntimeImages
        end
      ) as $passed |
      {
        passed: $passed,
        requiresSourceBuild: $requiresSourceBuild,
        buildMode: (
          if .buildImages then "clean-checkout-compose-build"
          else "pinned-runtime-images"
          end
        ),
        expectedBuiltFromGitSha: .gitSha,
        observedBuiltFromGitSha: .manifestBuiltFromGitSha,
        runtimeImagesMatch: (
          if $requiresSourceBuild then null
          else .actualRuntimeImages == .pinnedRuntimeImages
          end
        )
      }
    '
}

soak_stop_event_monitor() {
    local monitor_pid="$1" wait_status
    [[ -n "$monitor_pid" ]] || return 0
    if ! kill -0 "$monitor_pid" 2>/dev/null; then
        wait "$monitor_pid" 2>/dev/null || true
        return 1
    fi
    kill "$monitor_pid" 2>/dev/null || return 1
    if wait "$monitor_pid" 2>/dev/null; then
        return 0
    else
        wait_status=$?
        [[ "$wait_status" == "130" || "$wait_status" == "143" ]]
    fi
}

soak_run_cleanup_steps() {
    local errors_file="$1"
    shift
    local errors='[]' step function_name status next_errors recording_failed=false
    while (($#)); do
        step="$1"
        function_name="$2"
        shift 2
        if "$function_name"; then
            continue
        else
            status=$?
            if next_errors="$(jq -cn \
                --argjson errors "$errors" \
                --arg step "$step" \
                --argjson exitCode "$status" \
                '$errors + [{step:$step,exitCode:$exitCode}]')"; then
                errors="$next_errors"
            else
                recording_failed=true
            fi
        fi
    done
    printf '%s\n' "$errors" >"$errors_file" || return 1
    if $recording_failed; then
        return 1
    fi
    local error_count
    error_count="$(jq 'length' <<<"$errors")" || return 1
    [[ "$error_count" == "0" ]]
}

soak_append_cleanup_error() {
    local errors_file="$1" step="$2" exit_code="$3"
    local errors
    errors="$(cat "$errors_file")" || return 1
    jq -cn \
        --argjson errors "$errors" \
        --arg step "$step" \
        --argjson exitCode "$exit_code" \
        '$errors + [{step:$step,exitCode:$exitCode}]' >"${errors_file}.next" &&
        mv "${errors_file}.next" "$errors_file"
}

soak_apply_cleanup_to_summary() {
    local summary_file="$1" cleanup_json="$2"
    jq --argjson cleanup "$cleanup_json" '
      .cleanup = $cleanup |
      if $cleanup.passed then . else
        .passed = false |
        .acceptanceEligible = false |
        .evidenceClass = "failed-cleanup"
      end
    ' "$summary_file" >"${summary_file}.next" &&
        mv "${summary_file}.next" "$summary_file"
}

soak_cleanup_exit_status() {
    local original_status="$1" cleanup_status="$2"
    if ((original_status != 0)); then
        printf '%s\n' "$original_status"
    elif ((cleanup_status != 0)); then
        printf '%s\n' "$cleanup_status"
    else
        printf '0\n'
    fi
}
