FROM nginx:alpine
# nginx.conf is a template; the entrypoint script below renders it into
# /etc/nginx/nginx.conf at container start so NGINX_RESOLVER can be set
# per-orchestrator (Docker vs Kubernetes/AKS — see #562).
COPY nginx.conf.template /etc/nginx/nginx.conf.template
COPY 25-render-nginx-conf.sh /docker-entrypoint.d/25-render-nginx-conf.sh
RUN chmod +x /docker-entrypoint.d/25-render-nginx-conf.sh
# js/env.js is likewise a template, rendered at container start so
# MARKETDATA_WS_URL and APP_TITLE can ship deploy-time defaults for the
# marketdata WS endpoint and browser/login/app-shell branding (see #572,
# #596).
COPY env.js.template /etc/nginx/env.js.template
COPY 20-render-env-js.sh /docker-entrypoint.d/20-render-env-js.sh
RUN chmod +x /docker-entrypoint.d/20-render-env-js.sh
COPY index.html /usr/share/nginx/html/
COPY css /usr/share/nginx/html/css
COPY js  /usr/share/nginx/html/js
ENV NGINX_RESOLVER=127.0.0.11
ENV TRADING_UPSTREAM=trading-host:5000
ENV MARKETDATA_WS_URL=""
ENV APP_TITLE="B3TradingPlatform"
EXPOSE 80
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD wget -qO- http://127.0.0.1/ >/dev/null || exit 1
