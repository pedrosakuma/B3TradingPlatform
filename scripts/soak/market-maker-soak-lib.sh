#!/usr/bin/env bash

readonly SOAK_ACCEPTANCE_DEPOSIT_AMOUNT_DEFAULT="1250000.00"
readonly SOAK_ACCEPTANCE_FUNDING_HEADROOM_PERCENT="25"

soak_conservative_profile_funding_bound() {
    local profile="$1"
    awk \
        -v profile="$profile" \
        -v warmup=900 \
        -v duration=7200 \
        -v interval=1 \
        -v quantity=100 \
        -v bootstrap_price=32.80 \
        -v tick=0.01 \
        -v inventory_bias_lots=12 \
        -v strict_refresh_interval=7 \
        -v recovery_cross_attempts=3 \
        -v headroom_percent="$SOAK_ACCEPTANCE_FUNDING_HEADROOM_PERCENT" '
          function round_cent(value) {
            return int(value * 100 + 0.500001) / 100
          }
          function fee(price, notional, brokerage) {
            notional = price * quantity
            brokerage = round_cent(notional * 5 / 10000)
            if (brokerage < 2) brokerage = 2
            return brokerage + round_cent(notional * 3.25 / 10000) + round_cent(notional * 2.75 / 10000)
          }
          function ceil(value) {
            return value == int(value) ? value : int(value) + 1
          }
          BEGIN {
            window_orders = int((warmup + duration) / interval)
            run_buys = int((window_orders + 1) / 2)
            run_sells = int(window_orders / 2)
            setup_buys = 1
            setup_sells = 1
            net_quantity = 0
            offset_ticks = 6

            if (profile == "inventory-skew") {
              setup_buys += inventory_bias_lots * 2
              setup_sells += inventory_bias_lots
              net_quantity = inventory_bias_lots * quantity
              offset_ticks = 11
            } else if (profile == "volatility-spread") {
              offset_ticks = 26
            } else if (profile != "baseline" && profile != "pause-and-cancel") {
              exit 2
            }

            buys = setup_buys + run_buys
            sells = setup_sells + run_sells
            executions = buys + sells
            offset = offset_ticks * tick
            price = bootstrap_price
            total_fees = 0
            maximum_side = buys > sells ? buys : sells

            # Isolated-venue upper path: every Buy advances the live price by
            # the full marketable offset and every Sell executes at that level.
            for (i = 0; i < maximum_side; i++) {
              if (i < buys) {
                if (i > 0) price += offset
                total_fees += fee(price)
              }
              if (i < sells) total_fees += fee(price)
            }

            pairs = buys < sells ? buys : sells
            pair_crossing_cost = pairs * 2 * offset * quantity
            inventory_capital = net_quantity * price
            subtotal = pair_crossing_cost + inventory_capital + total_fees

            if (profile == "pause-and-cancel") {
              strict_cycles = 1 + ceil((warmup + duration) / strict_refresh_interval) + recovery_cross_attempts
              # Each end-client receives one fill per configured symbol per
              # cycle; direction alternates but every execution pays fees.
              subtotal += strict_cycles * (fee(price) + fee(70.00) + fee(32.00))
            }

            with_headroom = subtotal * (100 + headroom_percent) / 100
            print ceil(with_headroom)
          }
        '
}

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

soak_curl_json_request_with_status() {
    local method="$1" url="$2" token_variable="$3" body_variable="$4"
    (
        set +x
        local token="${!token_variable-}" body="${!body_variable-}"
        export -n token body
        local curl_bin="${SOAK_CURL_BIN:-curl}"
        local curl_output curl_exit=0 http_status response_body
        local -a arguments=(-sS --max-time 10 --request "$method")
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
        curl_output="$("$curl_bin" "${arguments[@]}" \
            --write-out $'\n%{http_code}' "$url")" || curl_exit=$?
        http_status="${curl_output##*$'\n'}"
        response_body="${curl_output%$'\n'*}"
        [[ "$curl_output" == *$'\n'* ]] || {
            http_status="000"
            response_body="$curl_output"
        }
        jq -cn \
            --argjson curlExit "$curl_exit" \
            --arg httpStatus "$http_status" \
            --arg body "$response_body" \
            '{curlExit:$curlExit,httpStatus:$httpStatus,body:$body}'
    )
}

soak_curl_status_envelope_into() {
    local response_variable="$1" status_variable="$2" envelope="$3"
    local parsed_curl_exit parsed_http_status parsed_response_body
    parsed_curl_exit="$(jq -er '.curlExit' <<<"$envelope")" || return 1
    parsed_http_status="$(jq -er '.httpStatus' <<<"$envelope")" || return 1
    parsed_response_body="$(jq -r '.body' <<<"$envelope")" || return 1
    printf -v "$response_variable" '%s' "$parsed_response_body"
    printf -v "$status_variable" '%s' "$parsed_http_status"
    ((parsed_curl_exit == 0)) || return "$parsed_curl_exit"
    [[ "$parsed_http_status" =~ ^[0-9]{3}$ ]] || return 1
    ((10#$parsed_http_status < 400)) || return 22
}

soak_sanitize_response_body() {
    local body="$1"
    if jq -e . >/dev/null 2>&1 <<<"$body"; then
        jq -c '
          walk(
            if type == "object" then
              with_entries(
                if (.key | test("token|password|secret|authorization"; "i"))
                then .value = "[REDACTED]"
                else .
                end
              )
            else .
            end
          )
        ' <<<"$body"
    else
        jq -cn --arg raw "$body" '{raw:$raw}'
    fi
}

soak_append_submit_failure() {
    local output_file="$1" timestamp="$2" stage="$3" command_status="$4"
    local http_status="$5" response_body="$6" context_json="$7"
    local sanitized_response
    sanitized_response="$(soak_sanitize_response_body "$response_body")" || return 1
    jq -cn \
        --arg timestampUtc "$timestamp" \
        --arg stage "$stage" \
        --argjson commandStatus "$command_status" \
        --arg httpStatus "$http_status" \
        --argjson responseBody "$sanitized_response" \
        --argjson requestContext "$context_json" \
        '{
          timestampUtc:$timestampUtc,
          stage:$stage,
          commandStatus:$commandStatus,
          httpStatus:(
            if $httpStatus == "" or $httpStatus == "000"
            then null
            else ($httpStatus | tonumber)
            end
          ),
          responseBody:$responseBody,
          requestContext:$requestContext
        }' >>"$output_file"
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

soak_sum_metric_values() {
    local total=0 value name
    for name in "$@"; do
        value="$(metric_value "sum(${name})")" || return 1
        total="$(awk -v total="$total" -v value="$value" 'BEGIN { print total + value }')" ||
            return 1
    done
    printf '%s\n' "$total" || return 1
}

soak_operational_error_metric_names() {
    printf '%s\n' \
        bot_orders_submit_failed_total \
        bot_orders_rejected_total \
        bot_orders_ttl_refresh_cancel_rejected_total \
        bot_orders_ttl_refresh_cancel_submit_failed_total \
        bot_orders_book_driven_requote_submit_failed_total \
        bot_orders_book_driven_requote_cancel_rejected_total \
        bot_orders_feed_unavailable_cancel_rejected_total \
        bot_orders_feed_unavailable_cancel_submit_failed_total \
        bot_orders_cancel_ack_expired_total
}

soak_operational_error_total() {
    local -a metric_names
    mapfile -t metric_names < <(soak_operational_error_metric_names)
    soak_sum_metric_values "${metric_names[@]}"
}

soak_duration_to_seconds() {
    local duration="$1"
    [[ "$duration" =~ ^([0-9]+):([0-5][0-9]):([0-5][0-9])$ ]] || return 1
    printf '%s\n' "$((10#${BASH_REMATCH[1]} * 3600 +
        10#${BASH_REMATCH[2]} * 60 +
        10#${BASH_REMATCH[3]}))"
}

soak_validate_strict_refresh_timing() {
    local max_age_seconds="$1" interval_seconds="$2" margin_seconds="$3"
    local telemetry_cycle_seconds="$4"
    ((max_age_seconds > 0)) &&
        ((interval_seconds > 0)) &&
        ((margin_seconds >= telemetry_cycle_seconds)) &&
        ((interval_seconds + margin_seconds < max_age_seconds))
}

soak_resolve_marketable_limit() {
    local side="$1" reference="$2" tick="$3" spread_ticks="$4"
    local skew_ticks="$5" volatility_ticks="$6" extra_ticks="$7"
    local collar_percent="$8" collar_absolute="$9"

    awk \
        -v side="$side" \
        -v reference="$reference" \
        -v tick="$tick" \
        -v spread="$spread_ticks" \
        -v skew="$skew_ticks" \
        -v volatility="$volatility_ticks" \
        -v extra="$extra_ticks" \
        -v collar_percent="$collar_percent" \
        -v collar_absolute="$collar_absolute" '
          BEGIN {
            if (reference <= 0 || tick <= 0 ||
                (side != "Buy" && side != "Sell"))
              exit 2

            lower = -1.0e100
            upper = 1.0e100
            if (collar_percent != "") {
              percent_lower = reference * (1 - collar_percent / 100)
              percent_upper = reference * (1 + collar_percent / 100)
              if (percent_lower > lower) lower = percent_lower
              if (percent_upper < upper) upper = percent_upper
            }
            if (collar_absolute != "" && collar_absolute > 0) {
              absolute_lower = reference - collar_absolute
              absolute_upper = reference + collar_absolute
              if (absolute_lower > lower) lower = absolute_lower
              if (absolute_upper < upper) upper = absolute_upper
            }

            offset = tick * (spread + skew + volatility + extra)
            required = side == "Buy" ? reference + offset : reference - offset
            if ((side == "Buy" && required > upper) ||
                (side == "Sell" && required < lower))
              exit 3

            ticks = required / tick
            rounded_ticks = side == "Buy" ? int(ticks + 0.999999999) : int(ticks)
            resolved = rounded_ticks * tick
            if (resolved < lower || resolved > upper)
              exit 3

            printf "%.8f\n", resolved
          }
        '
}

soak_reference_timestamp_is_fresh() {
    local updated_epoch_ms="$1" now_epoch_ms="$2" max_age_seconds="$3"
    [[ "$updated_epoch_ms" =~ ^[0-9]+$ &&
       "$now_epoch_ms" =~ ^[0-9]+$ &&
       "$max_age_seconds" =~ ^[0-9]+$ ]] || return 1
    ((max_age_seconds > 0)) || return 1
    ((updated_epoch_ms <= now_epoch_ms)) || return 1
    ((now_epoch_ms - updated_epoch_ms < max_age_seconds * 1000))
}

soak_primary_reference_bootstrap_reset() {
    soak_primary_live_reference_observed=false
}

soak_primary_reference_mark_live_observed() {
    soak_primary_live_reference_observed=true
}

soak_primary_reference_fallback_allowed() {
    [[ "$1" == "missing" &&
       "${soak_primary_live_reference_observed:-false}" == "false" ]]
}

soak_evaluate_strict_freshness() {
    jq -c '
      . as $evidence |
      ($evidence.configuredSymbols // []) as $symbols |
      ($evidence.refreshes // []) as $refreshes |
      ($evidence.metricSamples // []) as $samples |
      ($evidence.maxReferenceAgeSeconds // 0) as $maxAge |
      ($evidence.refreshIntervalSeconds // 0) as $interval |
      ($evidence.refreshMarginSeconds // 0) as $margin |
      ["outage-settled","outage-hold","reconnected-no-reference"] as $excludedPhases |
      [
        $samples[] |
        select(.metric == "bot_market_data_reference_eligible_current") |
        select(.phase as $phase | $excludedPhases | index($phase) | not) |
        . as $eligible |
        [
          $samples[] |
          select(.timestamp_utc == $eligible.timestamp_utc) |
          select(.phase == $eligible.phase) |
          select(.metric == "bot_market_data_reference_age_seconds") |
          select(.labels.symbol == $eligible.labels.symbol)
        ] as $ageRows |
        {
          timestampUtc: $eligible.timestamp_utc,
          phase: $eligible.phase,
          symbol: $eligible.labels.symbol,
          eligible: $eligible.value,
          referenceAgeSeconds: (
            if ($ageRows | length) == 0 then null
            else ($ageRows | map(.value) | min)
            end
          ),
          passed: (
            $eligible.value == 1 and
            ($ageRows | length) > 0 and
            ($ageRows | map(.value) | min) < $maxAge
          )
        }
      ] as $observations |
      def segment_phases($segment):
        if $segment == "pre-outage" then
          ["initial","warmup","warmup-complete","pre-outage-stabilization","pre-outage"]
        else ["recovered","duration","final"]
        end;
      [
        $symbols[] as $symbol |
        {
          symbol: $symbol,
          count: ([$refreshes[] | select(.symbol == $symbol)] | length),
          timestampsUtc: [
            $refreshes[] | select(.symbol == $symbol) | .timestampUtc
          ],
          segments: [
            ["pre-outage","post-recovery"][] as $segment |
            ([
              $refreshes[] |
              select(.symbol == $symbol and .segment == $segment)
            ]) as $segmentRows |
            ($segmentRows | map(.timestampUtc | fromdateiso8601) | sort) as $times |
            ([
              $observations[] |
              select(.symbol == $symbol) |
              select(.phase as $phase | segment_phases($segment) | index($phase)) |
              (.timestampUtc | fromdateiso8601)
            ] | sort) as $windowTimes |
            (
              if ($times | length) == 0 or ($windowTimes | length) == 0 then []
              else
                ([range(1; $times | length) |
                  $times[.] - $times[. - 1]]) +
                [([0, ($times[0] - $windowTimes[0])] | max)] +
                [([0, (($windowTimes | last) - ($times | last))] | max)]
              end
            ) as $gaps |
            {
              segment: $segment,
              count: ($times | length),
              timestampsUtc: ($segmentRows | map(.timestampUtc)),
              directionCounts: {
                tradingBuys: (
                  [$segmentRows[] | select(.direction == "trading-buys")] | length
                ),
                counterpartyBuys: (
                  [$segmentRows[] | select(.direction == "counterparty-buys")] | length
                )
              },
              windowStartUtc: (
                if ($windowTimes | length) == 0 then null
                else ($windowTimes[0] | todateiso8601)
                end
              ),
              windowEndUtc: (
                if ($windowTimes | length) == 0 then null
                else (($windowTimes | last) | todateiso8601)
                end
              ),
              maxGapSeconds: (
                if ($gaps | length) == 0 then null else ($gaps | max) end
              ),
              passed: (
                ($times | length) > 0 and
                ($windowTimes | length) > 0 and
                ($gaps | length) > 0 and
                ($gaps | max) <= ($interval + $margin) and
                ($gaps | max) < $maxAge and
                (([
                  ([$segmentRows[] | select(.direction == "trading-buys")] | length) -
                    ([$segmentRows[] | select(.direction == "counterparty-buys")] | length),
                  ([$segmentRows[] | select(.direction == "counterparty-buys")] | length) -
                    ([$segmentRows[] | select(.direction == "trading-buys")] | length)
                ] | max) <= 1)
              )
            }
          ]
        }
      ] as $refreshSummary |
      [
        $symbols[] as $symbol |
        {
          symbol: $symbol,
          sampleCount: ([$observations[] | select(.symbol == $symbol)] | length),
          failedSamples: [
            $observations[] |
            select(.symbol == $symbol and (.passed | not))
          ],
          passed: (
            ([$observations[] | select(.symbol == $symbol)] | length) > 0 and
            all($observations[] | select(.symbol == $symbol); .passed)
          )
        }
      ] as $symbolFreshness |
      {
        passed: (
          ($symbols | length) > 0 and
          $interval > 0 and
          $margin > 0 and
          ($interval + $margin) < $maxAge and
          all($refreshSummary[]; .count > 0 and all(.segments[]; .passed)) and
          all($symbolFreshness[]; .passed)
        ),
        expected: {
          configuredSymbols: $symbols,
          maxReferenceAgeSeconds: $maxAge,
          refreshIntervalSeconds: $interval,
          refreshMarginSeconds: $margin,
          maximumPermittedObservedGapSeconds: ($interval + $margin),
          excludedIntentionalPhases: $excludedPhases,
          requirement:
            "every configured symbol remains eligible and below MaxReferenceAge outside intentional outage/reconnect phases"
        },
        refreshSummary: $refreshSummary,
        symbolFreshness: $symbolFreshness,
        observations: $observations
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
