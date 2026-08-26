# Fallen-8 integrations runtime (feature integrations): a SEPARATE deployable that runs one
# integration job per request - it reads a system on the user's own network, describes what it
# saw, and writes that description into one Fallen-8 namespace over the public REST API. It
# loads no engine assemblies; the graph it feeds is reached over REST only, exactly as
# fallen-8-mcp does, because a container that is handed somebody's network-admin credential must
# not load the engine in process.
#
# Its port is deliberately NOT published to the host - see docker-compose.yml's f8-integrations
# service (profile: integrations). Jobs hand this container third-party credentials, so nothing on
# the host's network may talk to it directly; the browser reaches it through the apiApp, which is
# already the authenticated front door. It stores none of them: a credential arrives with the job
# that needs it and is gone when the run ends, so there is no credential mount here.

# ---- build ----
# Runs on the BUILD host's architecture and emits architecture-neutral IL, so a multi-arch
# image build compiles once; only the runtime stage is per-architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# No .git in the build context, so MinVer (Directory.Build.props) cannot derive the tag
# version; the release workflow passes it in, local builds keep the honest dev default.
ARG VERSION=0.0.0-dev
COPY Directory.Build.props ./
# The REST-client seam this runtime shares with fallen-8-mcp; a ProjectReference cannot resolve
# unless its directory is in the build context too.
COPY fallen-8-rest-client/ fallen-8-rest-client/
COPY fallen-8-integrations/ fallen-8-integrations/
RUN dotnet publish fallen-8-integrations -c Release -o /app -p:MinVerVersionOverride=${VERSION}

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl is the compose healthcheck probe (the aspnet base image ships without it).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./

# A container binding loopback is unreachable, so the image binds 0.0.0.0 while the code's own
# default stays loopback. That is not a widening of the surface: the compose service publishes
# no host port, so the only caller that can reach this listener is the apiApp on f8-net.
ENV Integrations__BindAddress=0.0.0.0 \
    Integrations__Port=8110
EXPOSE 8110

ENTRYPOINT ["dotnet", "fallen-8-integrations.dll"]
