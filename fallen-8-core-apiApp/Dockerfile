#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# curl backs the compose healthcheck (GET /status). The aspnet base ships without it (the all-in-one
# root Dockerfile installs it too). Needed for the split topology (feature standalone-ui), where this
# UI-less image is the data plane and f8-mcp depends on it being healthy.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
# .NET 8+ container images run as a non-root user and Kestrel listens on 8080 by
# default (ASPNETCORE_HTTP_PORTS=8080), replacing the .NET Core 3.1 default of 80.
EXPOSE 8080

# The build stage runs on the BUILD host's architecture and emits architecture-neutral IL,
# so a multi-arch image build compiles once; only the runtime stage is per-architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# No .git in the build context, so MinVer (Directory.Build.props) cannot derive the tag
# version; the release workflow passes it in, local builds keep the honest dev default.
ARG VERSION=0.0.0-dev
COPY ["Directory.Build.props", "./"]
COPY ["fallen-8-core-apiApp/fallen-8-core-apiApp.csproj", "fallen-8-core-apiApp/"]
COPY ["fallen-8-core/fallen-8-core.csproj", "fallen-8-core/"]
RUN dotnet restore "fallen-8-core-apiApp/fallen-8-core-apiApp.csproj"
COPY . .
WORKDIR "/src/fallen-8-core-apiApp"
RUN dotnet build "fallen-8-core-apiApp.csproj" -c Release -o /app/build -p:MinVerVersionOverride=${VERSION}

FROM build AS publish
ARG VERSION=0.0.0-dev
RUN dotnet publish "fallen-8-core-apiApp.csproj" -c Release -o /app/publish -p:MinVerVersionOverride=${VERSION}

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "fallen-8-core-apiApp.dll"]
