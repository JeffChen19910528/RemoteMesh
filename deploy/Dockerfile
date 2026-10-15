# Build:  docker build -f deploy/Dockerfile -t remotemesh-server .
# Run:    see deploy/docker-compose.yml (the server expects a TLS-terminating proxy in front of it).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/server/RemoteDesktop.Server.Api/RemoteDesktop.Server.Api.csproj \
      -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Unprivileged user (provided by the base image) and nothing writable.
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "RemoteDesktop.Server.Api.dll"]
