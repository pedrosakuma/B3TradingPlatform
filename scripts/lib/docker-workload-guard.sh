#!/usr/bin/env bash

docker_workload_guard_require_commands() {
    local command_name
    for command_name in "$@"; do
        command -v "$command_name" >/dev/null 2>&1 || {
            echo "ERROR: required command not found: $command_name" >&2
            return 2
        }
    done
}

docker_workload_guard_acquire() {
    local owner="$1" lock_file lock_dir owner_file owner_tmp
    [[ "$owner" =~ ^[[:alnum:]_.:/-]+$ ]] || {
        echo "ERROR: Docker workload lock owner contains unsupported characters" >&2
        return 3
    }

    docker_workload_guard_require_commands flock || return $?
    lock_file="${B3TP_DOCKER_WORKLOAD_LOCK_FILE:-${XDG_RUNTIME_DIR:-/tmp}/b3tp-docker-workload-${UID}.lock}"
    lock_dir="$(dirname "$lock_file")"
    mkdir -p "$lock_dir" || return 2
    owner_file="${lock_file}.owner"

    exec {B3TP_DOCKER_WORKLOAD_LOCK_FD}>"$lock_file" || return 2
    if ! flock -n "$B3TP_DOCKER_WORKLOAD_LOCK_FD"; then
        echo "ERROR: another Docker workload owns the shared-host lock: $lock_file" >&2
        if [[ -f "$owner_file" ]]; then
            sed 's/^/  /' "$owner_file" >&2
        fi
        exec {B3TP_DOCKER_WORKLOAD_LOCK_FD}>&-
        unset B3TP_DOCKER_WORKLOAD_LOCK_FD
        return 2
    fi

    owner_tmp="$(mktemp "${owner_file}.XXXXXX")" || {
        exec {B3TP_DOCKER_WORKLOAD_LOCK_FD}>&-
        unset B3TP_DOCKER_WORKLOAD_LOCK_FD
        return 2
    }
    if ! {
        printf 'owner=%s\n' "$owner"
        printf 'pid=%s\n' "$$"
        printf 'startedAtUtc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    } >"$owner_tmp" || ! mv "$owner_tmp" "$owner_file"; then
        rm -f "$owner_tmp"
        exec {B3TP_DOCKER_WORKLOAD_LOCK_FD}>&-
        unset B3TP_DOCKER_WORKLOAD_LOCK_FD
        return 2
    fi
}

docker_workload_guard_require_free_ports() {
    local port
    local -A seen_ports=()
    docker_workload_guard_require_commands ss || return $?
    for port in "$@"; do
        [[ "$port" =~ ^[0-9]+$ ]] && ((port >= 1 && port <= 65535)) || {
            echo "ERROR: invalid required host port: $port" >&2
            return 3
        }
        if [[ -n "${seen_ports[$port]:-}" ]]; then
            echo "ERROR: required host port is configured more than once: $port" >&2
            return 3
        fi
        seen_ports["$port"]=1
    done
    for port in "$@"; do
        if [[ -n "$(ss -H -ltn "sport = :$port")" ]]; then
            echo "ERROR: required host port is already allocated: $port" >&2
            return 2
        fi
    done
}
