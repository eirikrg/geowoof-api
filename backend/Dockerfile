# syntax=docker/dockerfile:1

# ── Build ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet restore src/GeoWoof.Api/GeoWoof.Api.csproj
RUN dotnet publish src/GeoWoof.Api/GeoWoof.Api.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

# ── Runtime ──────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "GeoWoof.Api.dll"]
