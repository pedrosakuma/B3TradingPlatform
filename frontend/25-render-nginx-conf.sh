#!/bin/sh
# Renders /etc/nginx/nginx.conf from the checked-in template, substituting
# only ${NGINX_RESOLVER} (default 127.0.0.11, Docker's embedded DNS). Runs as
# part of the stock nginx image's /docker-entrypoint.d/ hook chain, so it
# executes on every `docker run` / Kubernetes pod start before nginx boots.
#
# Numbered 25- (before the stock 30-tune-worker-processes.sh) so that
# NGINX_ENTRYPOINT_WORKER_PROCESSES_AUTOTUNE, if set, still gets to sed-patch
# `worker_processes` in the *rendered* nginx.conf instead of a nonexistent
# file — see code review on #562/#563.
#
# NGINX_RESOLVER lets non-Docker orchestrators (e.g. Kubernetes/AKS, whose
# cluster DNS is a ClusterIP such as CoreDNS/kube-dns, not 127.0.0.11) point
# nginx at the right resolver without patching nginx.conf. See #562.
#
# TRADING_UPSTREAM lets non-Docker orchestrators set the trading-host
# upstream as an FQDN (e.g. "trading-host.<namespace>.svc.cluster.local:5000")
# instead of the bare short name that only Docker's embedded DNS can resolve
# via nginx's `resolver` directive. See #564.
set -e

: "${NGINX_RESOLVER:=127.0.0.11}"
: "${TRADING_UPSTREAM:=trading-host:5000}"

envsubst '${NGINX_RESOLVER} ${TRADING_UPSTREAM}' \
    < /etc/nginx/nginx.conf.template \
    > /etc/nginx/nginx.conf
