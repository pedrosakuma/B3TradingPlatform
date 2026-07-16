FROM node:20-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build

FROM nginx:alpine
# nginx.conf is a template; the entrypoint script below renders it into
# /etc/nginx/nginx.conf at container start so NGINX_RESOLVER, TRADING_UPSTREAM,
# CSP authority sources and market-data sources can be set per environment.
COPY --from=build /app/dist/nginx.conf.template /etc/nginx/nginx.conf.template
COPY --from=build /app/dist/25-render-nginx-conf.sh /docker-entrypoint.d/25-render-nginx-conf.sh
RUN chmod +x /docker-entrypoint.d/25-render-nginx-conf.sh
# js/env.js is likewise rendered at container start from public deploy-time
# config. It contains no secret: Entra SPA auth uses MSAL Authorization Code +
# PKCE with a public client id and delegated API scope.
COPY --from=build /app/dist/env.js.template /etc/nginx/env.js.template
COPY --from=build /app/dist/20-render-env-js.sh /docker-entrypoint.d/20-render-env-js.sh
RUN chmod +x /docker-entrypoint.d/20-render-env-js.sh
COPY --from=build /app/dist/index.html /usr/share/nginx/html/
COPY --from=build /app/dist/css /usr/share/nginx/html/css
COPY --from=build /app/dist/js  /usr/share/nginx/html/js
ENV NGINX_RESOLVER=127.0.0.11
ENV TRADING_UPSTREAM=trading-host:5000
ENV MARKETDATA_WS_URL=""
ENV APP_TITLE="B3TradingPlatform"
ENV AUTH_MODE="Local"
ENV AUTH_LOCAL_LOGIN_ENABLED=""
ENV AUTH_SIGNUP_ENABLED=""
ENV AUTH_TOTP_ENABLED=""
ENV AUTH_AUTHORITY=""
ENV AUTH_CLIENT_ID=""
ENV AUTH_API_SCOPE=""
ENV AUTH_REDIRECT_URI=""
ENV AUTH_LOGOUT_URI=""
ENV AUTH_KNOWN_AUTHORITIES=""
EXPOSE 80
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD wget -qO- http://127.0.0.1/ >/dev/null || exit 1
