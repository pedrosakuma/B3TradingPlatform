#!/usr/bin/env bash

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

soak_evaluate_outage_telemetry() {
    jq -c '
      . as $evidence |
      ($evidence.holdSamples // []) as $hold |
      {
        passed: (
          $evidence.pre.present and
          $evidence.settled.present and
          $evidence.post.present and
          ($hold | length) > 0 and
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
          postOpenOrders: ($evidence.expectedSymbolCount * 2),
          postEligibleSymbols: $evidence.expectedSymbolCount
        },
        observed: $evidence
      }
    '
}

soak_run_cleanup_steps() {
    local errors_file="$1"
    shift
    local errors='[]' step function_name status
    while (($#)); do
        step="$1"
        function_name="$2"
        shift 2
        if "$function_name"; then
            continue
        else
            status=$?
            errors="$(jq -cn \
                --argjson errors "$errors" \
                --arg step "$step" \
                --argjson exitCode "$status" \
                '$errors + [{step:$step,exitCode:$exitCode}]')"
        fi
    done
    printf '%s\n' "$errors" >"$errors_file"
    [[ "$(jq 'length' <<<"$errors")" == "0" ]]
}

soak_append_cleanup_error() {
    local errors_file="$1" step="$2" exit_code="$3"
    local errors
    errors="$(cat "$errors_file")"
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
