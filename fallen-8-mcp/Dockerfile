# Fallen-8 MCP server (feature mcp-server): a SEPARATE deployable that bridges the Model
# Context Protocol to a reachable Fallen-8 over its REST API. It loads no engine assemblies
# and, by construction here, is credentialed and reachable — see docker-compose.yml's f8-mcp
# service (profile: mcp). Run it against any Fallen-8, local or cross-host.

# ---- build ----
# Runs on the BUILD host's architecture and emits architecture-neutral IL, so a multi-arch
# image build compiles once; only the runtime stage is per-architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# No .git in the build context, so MinVer (Directory.Build.props) cannot derive the tag
# version; the release workflow passes it in, local builds keep the honest dev default.
ARG VERSION=0.0.0-dev
COPY Directory.Build.props ./
COPY fallen-8-mcp/ fallen-8-mcp/
RUN dotnet publish fallen-8-mcp -c Release -o /app -p:MinVerVersionOverride=${VERSION}

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl is the compose healthcheck probe (the aspnet base image ships without it).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./

# A container must bind 0.0.0.0 to be reachable (loopback-default is a no-op here), so the
# packaged deployment is credentialed by construction: the compose service pre-wires
# Mcp__Auth__Mode=StaticToken + a token, and the server fails closed on remote+anonymous.
ENV Mcp__Transport=http \
    Mcp__Port=8090 \
    Mcp__Security__BindAddress=0.0.0.0
EXPOSE 8090

ENTRYPOINT ["dotnet", "fallen-8-mcp.dll"]
