# Fallen-8 all-in-one image: engine + REST API + F8 Studio UI (feature web-ui).
#
# The SPA is built in a node stage, the apiApp published in an sdk stage, and both land
# in one aspnet runtime image; the apiApp serves the UI from wwwroot (gap G-1). The
# NL-assist SLM is deliberately NOT baked in (nl-assist spec FR-26.2: F8 bundles no
# weights/runtime) - docker-compose.yml wires the official Ollama image + the MIT
# default model next to this container.

# Build stages run on the BUILD host's architecture (both outputs are architecture-neutral:
# static JS and .NET IL), so a multi-arch image build compiles once instead of emulating the
# toolchain per target platform; only the runtime stage is per-architecture.

# ---- UI build ----
FROM --platform=$BUILDPLATFORM node:22-alpine AS ui-build
WORKDIR /src/fallen-8-web-ui
COPY fallen-8-web-ui/package.json fallen-8-web-ui/package-lock.json ./
RUN npm ci --no-fund --no-audit
COPY fallen-8-web-ui/ ./
# The client contract test imports the OpenAPI snapshot relative to the repo layout.
COPY features/done/web-ui/openapi-v0.1.json /src/features/done/web-ui/openapi-v0.1.json
# Sample datasets (feature sample-graphs) ship with the app: the build copies them into the SPA
# output and the apiApp serves them same-origin at /samples. The plugin reads them from ../samples.
COPY samples/ /src/samples/
RUN npm run build

# ---- API build ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
# No .git in the build context, so MinVer (Directory.Build.props) cannot derive the tag
# version; the release workflow passes it in, local builds keep the honest dev default.
ARG VERSION=0.0.0-dev
COPY Directory.Build.props ./
COPY fallen-8-core/ fallen-8-core/
COPY fallen-8-core-apiApp/ fallen-8-core-apiApp/
RUN dotnet publish fallen-8-core-apiApp -c Release -o /app -p:MinVerVersionOverride=${VERSION}

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl is the compose healthcheck probe (the aspnet base image ships without it).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=api-build /app ./
COPY --from=ui-build /src/fallen-8-web-ui/dist ./wwwroot

# Durable by default: checkpoints + WAL live on the /data volume.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Fallen8__Durability__StorageDirectory=/data
VOLUME /data
EXPOSE 8080

# Security posture is opt-in via environment, exactly like a bare-metal run:
#   Fallen8__Security__ApiKey=...                    (authentication — set before exposing; the
#                                                     /path + /subgraph endpoints run in-process code)
#   Fallen8__Security__EnableDynamicPluginLoading=true  (source plugin registration: POST /plugins/*)
#   Fallen8__Security__AllowedCorsOrigins__0=...     (cross-origin instances)

ENTRYPOINT ["dotnet", "fallen-8-core-apiApp.dll"]
