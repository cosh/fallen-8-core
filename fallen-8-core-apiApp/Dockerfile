#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:3.1 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:3.1 AS build
WORKDIR /src
COPY ["fallen-8-core-apiApp/fallen-8-core-apiApp.csproj", "fallen-8-core-apiApp/"]
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