#!/bin/sh
# Renders /usr/share/nginx/html/js/env.js from the checked-in template.
# Values are public browser config only; never add client secrets here.
set -e

js_string_literal() {
    printf '%s' "$1" | awk '
        BEGIN { printf "\"" }
        {
            if (NR > 1) printf "\\n";
            for (i = 1; i <= length($0); i++) {
                ch = substr($0, i, 1);
                if (ch == "\\")      printf "\\\\";
                else if (ch == "\"") printf "\\\"";
                else if (ch == "\b") printf "\\b";
                else if (ch == "\f") printf "\\f";
                else if (ch == "\r") printf "\\r";
                else if (ch == "\t") printf "\\t";
                else                 printf "%s", ch;
            }
        }
        END { printf "\"" }
    '
}

js_bool_or_null() {
    case "$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')" in
        true|1|yes|on) printf 'true' ;;
        false|0|no|off) printf 'false' ;;
        *) printf 'null' ;;
    esac
}

js_csv_array() {
    value=$1
    if [ -z "$value" ]; then
        printf '[]'
        return
    fi
    printf '['
    first=1
    old_ifs=$IFS
    IFS=','
    for item in $value; do
        trimmed=$(printf '%s' "$item" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
        [ -z "$trimmed" ] && continue
        if [ "$first" -eq 0 ]; then printf ','; fi
        js_string_literal "$trimmed"
        first=0
    done
    IFS=$old_ifs
    printf ']'
}

: "${MARKETDATA_WS_URL:=}"
: "${APP_TITLE:=B3TradingPlatform}"
: "${AUTH_MODE:=Local}"
: "${AUTH_LOCAL_LOGIN_ENABLED:=}"
: "${AUTH_SIGNUP_ENABLED:=}"
: "${AUTH_TOTP_ENABLED:=}"
: "${AUTH_AUTHORITY:=}"
: "${AUTH_CLIENT_ID:=}"
: "${AUTH_API_SCOPE:=}"
: "${AUTH_REDIRECT_URI:=}"
: "${AUTH_LOGOUT_URI:=}"
: "${AUTH_KNOWN_AUTHORITIES:=}"

MARKETDATA_WS_URL_JSON=$(js_string_literal "$MARKETDATA_WS_URL")
APP_TITLE_JSON=$(js_string_literal "$APP_TITLE")
AUTH_MODE_JSON=$(js_string_literal "$AUTH_MODE")
AUTH_LOCAL_LOGIN_ENABLED_JSON=$(js_bool_or_null "$AUTH_LOCAL_LOGIN_ENABLED")
AUTH_SIGNUP_ENABLED_JSON=$(js_bool_or_null "$AUTH_SIGNUP_ENABLED")
AUTH_TOTP_ENABLED_JSON=$(js_bool_or_null "$AUTH_TOTP_ENABLED")
AUTH_AUTHORITY_JSON=$(js_string_literal "$AUTH_AUTHORITY")
AUTH_CLIENT_ID_JSON=$(js_string_literal "$AUTH_CLIENT_ID")
AUTH_API_SCOPE_JSON=$(js_string_literal "$AUTH_API_SCOPE")
AUTH_REDIRECT_URI_JSON=$(js_string_literal "$AUTH_REDIRECT_URI")
AUTH_LOGOUT_URI_JSON=$(js_string_literal "$AUTH_LOGOUT_URI")
AUTH_KNOWN_AUTHORITIES_JSON=$(js_csv_array "$AUTH_KNOWN_AUTHORITIES")

export MARKETDATA_WS_URL_JSON APP_TITLE_JSON AUTH_MODE_JSON \
    AUTH_LOCAL_LOGIN_ENABLED_JSON AUTH_SIGNUP_ENABLED_JSON AUTH_TOTP_ENABLED_JSON \
    AUTH_AUTHORITY_JSON AUTH_CLIENT_ID_JSON AUTH_API_SCOPE_JSON \
    AUTH_REDIRECT_URI_JSON AUTH_LOGOUT_URI_JSON AUTH_KNOWN_AUTHORITIES_JSON

awk '
    function replace_all(line, token, value) {
        while (index(line, token) > 0) {
            line = substr(line, 1, index(line, token) - 1) value substr(line, index(line, token) + length(token));
        }
        return line;
    }
    {
        line = $0;
        line = replace_all(line, "__MARKETDATA_WS_URL_JSON__", ENVIRON["MARKETDATA_WS_URL_JSON"]);
        line = replace_all(line, "__APP_TITLE_JSON__", ENVIRON["APP_TITLE_JSON"]);
        line = replace_all(line, "__AUTH_MODE_JSON__", ENVIRON["AUTH_MODE_JSON"]);
        line = replace_all(line, "__AUTH_LOCAL_LOGIN_ENABLED_JSON__", ENVIRON["AUTH_LOCAL_LOGIN_ENABLED_JSON"]);
        line = replace_all(line, "__AUTH_SIGNUP_ENABLED_JSON__", ENVIRON["AUTH_SIGNUP_ENABLED_JSON"]);
        line = replace_all(line, "__AUTH_TOTP_ENABLED_JSON__", ENVIRON["AUTH_TOTP_ENABLED_JSON"]);
        line = replace_all(line, "__AUTH_AUTHORITY_JSON__", ENVIRON["AUTH_AUTHORITY_JSON"]);
        line = replace_all(line, "__AUTH_CLIENT_ID_JSON__", ENVIRON["AUTH_CLIENT_ID_JSON"]);
        line = replace_all(line, "__AUTH_API_SCOPE_JSON__", ENVIRON["AUTH_API_SCOPE_JSON"]);
        line = replace_all(line, "__AUTH_REDIRECT_URI_JSON__", ENVIRON["AUTH_REDIRECT_URI_JSON"]);
        line = replace_all(line, "__AUTH_LOGOUT_URI_JSON__", ENVIRON["AUTH_LOGOUT_URI_JSON"]);
        line = replace_all(line, "__AUTH_KNOWN_AUTHORITIES_JSON__", ENVIRON["AUTH_KNOWN_AUTHORITIES_JSON"]);
        print line;
    }
' /etc/nginx/env.js.template > /usr/share/nginx/html/js/env.js
