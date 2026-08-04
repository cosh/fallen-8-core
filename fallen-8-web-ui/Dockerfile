# Fallen-8 F8 Studio: STANDALONE static image (feature standalone-ui).
#
# The DECOUPLED counterpart to the root all-in-one Dockerfile (which bakes the SPA into the
# apiApp's wwwroot). Here a tiny nginx serves the built SPA and is pointed at an arbitrary
# Fallen-8 REST data plane at CONTAINER START: the entrypoint rewrites config.js from the
# F8_API_URL environment variable before nginx launches, so one built image can front any data
# plane without a rebuild.
#
# Build context is the REPO ROOT (docker build -f fallen-8-web-ui/Dockerfile .), mirroring the
# all-in-one build, so the OpenAPI snapshot and the sample datasets (both outside fallen-8-web-ui/)
# resolve and the root .dockerignore applies. The SPA is built in-container: a host-built dist/ is
# stripped by .dockerignore (**/dist).

# ---- UI build (mirrors the all-in-one ui-build stage) ----
FROM node:22-alpine AS ui-build
WORKDIR /src/fallen-8-web-ui
COPY fallen-8-web-ui/package.json fallen-8-web-ui/package-lock.json ./
RUN npm ci --no-fund --no-audit
COPY fallen-8-web-ui/ ./
# The client contract test imports the OpenAPI snapshot relative to the repo layout.
COPY features/done/web-ui/openapi-v0.1.json /src/features/done/web-ui/openapi-v0.1.json
# Sample datasets (feature sample-graphs) ride along: the build copies them into the SPA output
# (dist/samples) and nginx serves them at /samples. The plugin reads them from ../samples.
COPY samples/ /src/samples/
RUN npm run build

# ---- runtime (static nginx) ----
FROM nginx:1.27-alpine
# Serve the built SPA (dist/ already includes dist/samples).
COPY --from=ui-build /src/fallen-8-web-ui/dist /usr/share/nginx/html
COPY fallen-8-web-ui/nginx.conf /etc/nginx/conf.d/default.conf
# The official nginx image runs every /docker-entrypoint.d/*.sh before starting nginx; this one
# rewrites config.js from F8_API_URL (feature standalone-ui).
COPY fallen-8-web-ui/docker-entrypoint-config.sh /docker-entrypoint.d/40-f8-config.sh
RUN chmod +x /docker-entrypoint.d/40-f8-config.sh

# The browser-reachable Fallen-8 REST origin the Studio talks to. Empty = same-origin (rarely
# useful standalone). For any non-loopback deployment set this to the data plane's PUBLIC url;
# `localhost` resolves to the visitor's own machine, not the server.
ENV F8_API_URL=""

EXPOSE 80
HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=5 \
  CMD wget -q -O /dev/null http://localhost/config.js || exit 1
