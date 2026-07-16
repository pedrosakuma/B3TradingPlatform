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

: "${ENV_JS_TEMPLATE:=/etc/nginx/env.js.template}"
: "${ENV_JS_OUTPUT:=/usr/share/nginx/html/js/env.js}"

awk '
    BEGIN {
        tokens[1] = "__MARKETDATA_WS_URL_JSON__"; values[tokens[1]] = ENVIRON["MARKETDATA_WS_URL_JSON"];
        tokens[2] = "__APP_TITLE_JSON__"; values[tokens[2]] = ENVIRON["APP_TITLE_JSON"];
        tokens[3] = "__AUTH_MODE_JSON__"; values[tokens[3]] = ENVIRON["AUTH_MODE_JSON"];
        tokens[4] = "__AUTH_LOCAL_LOGIN_ENABLED_JSON__"; values[tokens[4]] = ENVIRON["AUTH_LOCAL_LOGIN_ENABLED_JSON"];
        tokens[5] = "__AUTH_SIGNUP_ENABLED_JSON__"; values[tokens[5]] = ENVIRON["AUTH_SIGNUP_ENABLED_JSON"];
        tokens[6] = "__AUTH_TOTP_ENABLED_JSON__"; values[tokens[6]] = ENVIRON["AUTH_TOTP_ENABLED_JSON"];
        tokens[7] = "__AUTH_AUTHORITY_JSON__"; values[tokens[7]] = ENVIRON["AUTH_AUTHORITY_JSON"];
        tokens[8] = "__AUTH_CLIENT_ID_JSON__"; values[tokens[8]] = ENVIRON["AUTH_CLIENT_ID_JSON"];
        tokens[9] = "__AUTH_API_SCOPE_JSON__"; values[tokens[9]] = ENVIRON["AUTH_API_SCOPE_JSON"];
        tokens[10] = "__AUTH_REDIRECT_URI_JSON__"; values[tokens[10]] = ENVIRON["AUTH_REDIRECT_URI_JSON"];
        tokens[11] = "__AUTH_LOGOUT_URI_JSON__"; values[tokens[11]] = ENVIRON["AUTH_LOGOUT_URI_JSON"];
        tokens[12] = "__AUTH_KNOWN_AUTHORITIES_JSON__"; values[tokens[12]] = ENVIRON["AUTH_KNOWN_AUTHORITIES_JSON"];
        token_count = 12;
    }
    {
        line = $0;
        for (i = 1; i <= length(line); ) {
            matched = 0;
            for (t = 1; t <= token_count; t++) {
                token = tokens[t];
                if (substr(line, i, length(token)) == token) {
                    printf "%s", values[token];
                    i += length(token);
                    matched = 1;
                    break;
                }
            }
            if (!matched) {
                printf "%s", substr(line, i, 1);
                i++;
            }
        }
        printf "\n";
    }
' "$ENV_JS_TEMPLATE" > "$ENV_JS_OUTPUT"
