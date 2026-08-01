#!/usr/bin/env bash
set -euo pipefail

readonly ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
readonly GUARD="$ROOT/scripts/lib/docker-workload-guard.sh"
readonly TEST_ROOT="$(mktemp -d)"
readonly LOCK_FILE="$TEST_ROOT/docker-workload.lock"
readonly READY_FILE="$TEST_ROOT/holder.ready"
readonly RELEASE_PIPE="$TEST_ROOT/holder.release"
holder_pid=""

cleanup() {
    if [[ -n "$holder_pid" ]] && kill -0 "$holder_pid" 2>/dev/null; then
        kill "$holder_pid"
        wait "$holder_pid" 2>/dev/null || true
    fi
    rm -rf "$TEST_ROOT"
}
trap cleanup EXIT

source "$GUARD"
mkfifo "$RELEASE_PIPE"

docker_workload_guard_require_commands bash flock ss
if docker_workload_guard_require_commands command-that-must-not-exist >/dev/null 2>&1; then
    echo "ERROR: missing command was accepted" >&2
    exit 1
fi

B3TP_DOCKER_WORKLOAD_LOCK_FILE="$LOCK_FILE" \
GUARD_READY_FILE="$READY_FILE" GUARD_RELEASE_PIPE="$RELEASE_PIPE" \
    bash -c '
      set -euo pipefail
      source "$1"
      docker_workload_guard_acquire holder
      printf "ready\n" >"$GUARD_READY_FILE"
      read -r _ <"$GUARD_RELEASE_PIPE"
    ' bash "$GUARD" &
holder_pid=$!

for _ in {1..50}; do
    [[ -f "$READY_FILE" ]] && break
    sleep 0.1
done
[[ -f "$READY_FILE" ]]

if B3TP_DOCKER_WORKLOAD_LOCK_FILE="$LOCK_FILE" \
    bash -c 'source "$1"; docker_workload_guard_acquire contender' bash "$GUARD" \
    >"$TEST_ROOT/contender.out" 2>"$TEST_ROOT/contender.err"; then
    echo "ERROR: concurrent Docker workload acquired the same lock" >&2
    exit 1
fi
grep -q 'owner=holder' "$TEST_ROOT/contender.err"

kill "$holder_pid"
wait "$holder_pid" 2>/dev/null || true
holder_pid=""
B3TP_DOCKER_WORKLOAD_LOCK_FILE="$LOCK_FILE" \
    bash -c 'source "$1"; docker_workload_guard_acquire successor' bash "$GUARD"
grep -q 'owner=successor' "${LOCK_FILE}.owner"

mock_bin="$TEST_ROOT/bin"
mkdir -p "$mock_bin"
cat >"$mock_bin/ss" <<'EOF'
#!/usr/bin/env bash
if [[ "$*" == *":43210"* ]]; then
    printf 'LISTEN 0 4096 0.0.0.0:43210 0.0.0.0:*\n'
fi
EOF
chmod +x "$mock_bin/ss"

PATH="$mock_bin:$PATH" docker_workload_guard_require_free_ports 43211
if PATH="$mock_bin:$PATH" docker_workload_guard_require_free_ports 43210 \
    >"$TEST_ROOT/port.out" 2>"$TEST_ROOT/port.err"; then
    echo "ERROR: allocated host port was accepted" >&2
    exit 1
fi
grep -q '43210' "$TEST_ROOT/port.err"
if docker_workload_guard_require_free_ports 0 >/dev/null 2>&1; then
    echo "ERROR: invalid host port was accepted" >&2
    exit 1
fi
if PATH="$mock_bin:$PATH" docker_workload_guard_require_free_ports 43211 43211 \
    >/dev/null 2>"$TEST_ROOT/duplicate-port.err"; then
    echo "ERROR: duplicate host port was accepted" >&2
    exit 1
fi
grep -q 'configured more than once: 43211' "$TEST_ROOT/duplicate-port.err"
if PATH="$mock_bin:$PATH" docker_workload_guard_require_free_ports 43210 43210 \
    >/dev/null 2>"$TEST_ROOT/duplicate-busy-port.err"; then
    echo "ERROR: duplicate allocated host port was accepted" >&2
    exit 1
fi
grep -q 'configured more than once: 43210' "$TEST_ROOT/duplicate-busy-port.err"

echo "PASS: Docker workload guard self-tests"
