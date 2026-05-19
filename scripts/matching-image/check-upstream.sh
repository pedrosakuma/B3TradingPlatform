#!/usr/bin/env bash
# Resolves the current sha256 digest of the upstream
# ghcr.io/pedrosakuma/b3-matching image for a given tag (default
# `latest`) and prints a human-readable diff against the digest pinned
# in docker/docker-compose.yml.
#
# Exit codes:
#   0 — pinned digest already matches upstream (no action needed)
#   1 — usage / IO error
#   2 — drift detected (caller should open a bump PR)
#
# Usage:
#   scripts/matching-image/check-upstream.sh                 # tag=latest
#   scripts/matching-image/check-upstream.sh v1.2.3          # specific tag
#   IMAGE=ghcr.io/other/img scripts/matching-image/check-upstream.sh
set -euo pipefail

IMAGE="${IMAGE:-ghcr.io/pedrosakuma/b3-matching}"
TAG="${1:-latest}"
COMPOSE_FILE="${COMPOSE_FILE:-$(dirname "$0")/../../docker/docker-compose.yml}"

if [[ ! -f "$COMPOSE_FILE" ]]; then
  echo "error: compose file not found at $COMPOSE_FILE" >&2
  exit 1
fi

REPO="${IMAGE#ghcr.io/}"

echo "Resolving ${IMAGE}:${TAG} via ghcr.io registry API..."
TOKEN=$(curl -fsSL "https://ghcr.io/token?scope=repository:${REPO}:pull" \
  | python3 -c "import json,sys;print(json.load(sys.stdin)['token'])")

UPSTREAM_DIGEST=$(curl -fsSL -I \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "Accept: application/vnd.docker.distribution.manifest.v2+json,application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.oci.image.index.v1+json" \
  "https://ghcr.io/v2/${REPO}/manifests/${TAG}" \
  | awk -F': ' 'tolower($1)=="docker-content-digest"{print $2}' \
  | tr -d '\r\n')

if [[ -z "$UPSTREAM_DIGEST" ]]; then
  echo "error: could not resolve upstream digest for ${IMAGE}:${TAG}" >&2
  exit 1
fi

PINNED_DIGEST=$(grep -oE 'b3-matching@sha256:[0-9a-f]{64}' "$COMPOSE_FILE" \
  | head -n1 \
  | sed 's/.*@//')

if [[ -z "$PINNED_DIGEST" ]]; then
  echo "error: no @sha256:... digest pin found in $COMPOSE_FILE" >&2
  exit 1
fi

echo "pinned  : ${PINNED_DIGEST}"
echo "upstream: ${UPSTREAM_DIGEST}"

if [[ "$PINNED_DIGEST" == "$UPSTREAM_DIGEST" ]]; then
  echo "✔ pin is current — no bump needed."
  exit 0
fi

echo "✗ upstream has advanced. Run the matching-image-bump workflow (or"
echo "  bump manually): sed -i \"s|${PINNED_DIGEST}|${UPSTREAM_DIGEST}|g\" $COMPOSE_FILE"
echo "${UPSTREAM_DIGEST}"
exit 2
