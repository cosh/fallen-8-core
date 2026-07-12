#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# .NET 8+ container images run as a non-root user and Kestrel listens on 8080 by
# default (ASPNETCORE_HTTP_PORTS=8080), replacing the .NET Core 3.1 default of 80.
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["fallen-8-core-apiApp/fallen-8-core-apiApp.csproj", "fallen-8-core-apiApp/"]
COPY ["fallen-8-core/fallen-8-core.csproj", "fallen-8-core/"]
RUN dotnet restore "fallen-8-core-apiApp/fallen-8-core-apiApp.csproj"
COPY . .
WORKDIR "/src/fallen-8-core-apiApp"
RUN dotnet build "fallen-8-core-apiApp.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "fallen-8-core-apiApp.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "fallen-8-core-apiApp.dll"]
