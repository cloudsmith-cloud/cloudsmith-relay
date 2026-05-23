# syntax=docker/dockerfile:1.6
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY nuget.config .
COPY src/CloudSmith.Relay/CloudSmith.Relay.csproj src/CloudSmith.Relay/
COPY tests/CloudSmith.Relay.Tests/CloudSmith.Relay.Tests.csproj tests/CloudSmith.Relay.Tests/
RUN --mount=type=secret,id=nuget_token \
    TOKEN=$(cat /run/secrets/nuget_token) && \
    dotnet nuget update source cloudsmith-github --username x-access-token --password "$TOKEN" --store-password-in-clear-text --configfile nuget.config && \
    dotnet restore src/CloudSmith.Relay/CloudSmith.Relay.csproj
COPY . .
RUN dotnet publish src/CloudSmith.Relay/CloudSmith.Relay.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
WORKDIR /app

# Relay's local LAN listen port for Agents (mTLS terminating)
EXPOSE 8443

ENV RELAY_LISTEN_PORT=8443 \
    RELAY_IDENTITY_DIR=/var/lib/cloudsmith-relay/identity \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=true

# Run as non-root; identity dir is owned by the relay user so chmod 600 sticks.
RUN addgroup -S relay && adduser -S -G relay relay && \
    mkdir -p /var/lib/cloudsmith-relay/identity && \
    chown -R relay:relay /var/lib/cloudsmith-relay && \
    chmod 700 /var/lib/cloudsmith-relay/identity
USER relay

COPY --from=build --chown=relay:relay /app/publish .

VOLUME ["/var/lib/cloudsmith-relay/identity"]

ENTRYPOINT ["dotnet", "CloudSmith.Relay.dll"]
