#!/bin/sh
# Renders /etc/nginx/nginx.conf from the checked-in template.
set -e

origin_from_url() {
    value=$1
    [ -z "$value" ] && return 0
    case "$value" in
        http://*|https://*|ws://*|wss://*) ;;
        *) return 0 ;;
    esac
    printf '%s' "$value" | sed -E 's#^([a-zA-Z][a-zA-Z0-9+.-]*://[^/?#]+).*$#\1#'
}

https_origin_from_host() {
    value=$(printf '%s' "$1" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
    [ -z "$value" ] && return 0
    case "$value" in
        http://*|https://*) origin_from_url "$value" ;;
        *://*) return 0 ;;
        *) printf 'https://%s' "$value" ;;
    esac
}

append_source() {
    list=$1
    source=$2
    [ -z "$source" ] && { printf '%s' "$list"; return; }
    case " $list " in
        *" $source "*) printf '%s' "$list" ;;
        *) printf '%s %s' "$list" "$source" ;;
    esac
}

: "${NGINX_RESOLVER:=127.0.0.11}"
: "${TRADING_UPSTREAM:=trading-host:5000}"
: "${MARKETDATA_WS_URL:=}"
: "${AUTH_AUTHORITY:=}"
: "${AUTH_KNOWN_AUTHORITIES:=}"

CSP_CONNECT_SOURCES=""
CSP_FRAME_SOURCES=""

market_origin=$(origin_from_url "$MARKETDATA_WS_URL")
if [ -n "$market_origin" ]; then
    CSP_CONNECT_SOURCES=$(append_source "$CSP_CONNECT_SOURCES" "$market_origin")
elif [ -z "$MARKETDATA_WS_URL" ]; then
    CSP_CONNECT_SOURCES=$(append_source "$CSP_CONNECT_SOURCES" "ws://localhost:8081")
    CSP_CONNECT_SOURCES=$(append_source "$CSP_CONNECT_SOURCES" "ws://127.0.0.1:8081")
fi

auth_origin=$(origin_from_url "$AUTH_AUTHORITY")
if [ -n "$auth_origin" ]; then
    CSP_CONNECT_SOURCES=$(append_source "$CSP_CONNECT_SOURCES" "$auth_origin")
    CSP_FRAME_SOURCES=$(append_source "$CSP_FRAME_SOURCES" "$auth_origin")
fi

old_ifs=$IFS
IFS=','
for host in $AUTH_KNOWN_AUTHORITIES; do
    known_origin=$(https_origin_from_host "$host")
    if [ -n "$known_origin" ]; then
        CSP_CONNECT_SOURCES=$(append_source "$CSP_CONNECT_SOURCES" "$known_origin")
        CSP_FRAME_SOURCES=$(append_source "$CSP_FRAME_SOURCES" "$known_origin")
    fi
done
IFS=$old_ifs

export NGINX_RESOLVER TRADING_UPSTREAM CSP_CONNECT_SOURCES CSP_FRAME_SOURCES

envsubst '${NGINX_RESOLVER} ${TRADING_UPSTREAM} ${CSP_CONNECT_SOURCES} ${CSP_FRAME_SOURCES}' \
    < /etc/nginx/nginx.conf.template \
    > /etc/nginx/nginx.conf
