FROM nginx:alpine
# nginx.conf is a template; the entrypoint script below renders it into
# /etc/nginx/nginx.conf at container start so NGINX_RESOLVER can be set
# per-orchestrator (Docker vs Kubernetes/AKS — see #562).
COPY nginx.conf.template /etc/nginx/nginx.conf.template
COPY 25-render-nginx-conf.sh /docker-entrypoint.d/25-render-nginx-conf.sh
RUN chmod +x /docker-entrypoint.d/25-render-nginx-conf.sh
COPY index.html /usr/share/nginx/html/
COPY css /usr/share/nginx/html/css
COPY js  /usr/share/nginx/html/js
ENV NGINX_RESOLVER=127.0.0.11
EXPOSE 80
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD wget -qO- http://127.0.0.1/ >/dev/null || exit 1
