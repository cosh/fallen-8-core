# Fallen-8 agent host (feature agent-host): a SEPARATE deployable that RUNS agents against the
# Fallen-8 it fronts. It is the other side of fallen-8-mcp - where that server exposes this graph's
# tools to somebody else's agent, this one runs agents here - and it reaches the graph only as an
# MCP CLIENT of that server, never over a graph REST route of its own.
#
# It holds NO model configuration and NO provider credential. An agent asks this instance's own
# POST /chat with purpose: agent, so the operator configures a model once, on the instance, and the
# host inherits whatever that resolves to. That is why there is no model setting anywhere below:
# adding one here would be a second place to configure the same thing, and the one that could
# disagree.
#
# Its port is deliberately NOT published to the host - see docker-compose.yml's f8-agents service
# (profile: agents). An agent decides for itself which tools to call, so nothing on the host's
# network may talk to it directly; the browser reaches it through the apiApp, which is already the
# authenticated front door.

# ---- build ----
# Runs on the BUILD host's architecture and emits architecture-neutral IL, so a multi-arch
# image build compiles once; only the runtime stage is per-architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# No .git in the build context, so MinVer (Directory.Build.props) cannot derive the tag
# version; the release workflow passes it in, local builds keep the honest dev default.
ARG VERSION=0.0.0-dev
COPY Directory.Build.props ./
# The REST-client seam this host shares with fallen-8-mcp and fallen-8-integrations; a
# ProjectReference cannot resolve unless its directory is in the build context too.
COPY fallen-8-rest-client/ fallen-8-rest-client/
COPY fallen-8-agents/ fallen-8-agents/
RUN dotnet publish fallen-8-agents -c Release -o /app -p:MinVerVersionOverride=${VERSION}

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl is the compose healthcheck probe (the aspnet base image ships without it).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./

# A container binding loopback is unreachable, so the image binds 0.0.0.0 while the code's own
# default stays loopback. The compose service publishes no host port, so nothing outside f8-net
# reaches this listener and the browser comes through the apiApp's authenticated proxy. Inside
# f8-net every service can reach it and this listener authenticates none of them, which is the
# posture f8-integrations ships with too; AgentsOptions.BindAddress is the one home for what it
# means, and the startup line warns about a non-loopback bind rather than claiming this file.
ENV Agents__BindAddress=0.0.0.0 \
    Agents__Port=8120
EXPOSE 8120

ENTRYPOINT ["dotnet", "fallen-8-agents.dll"]
