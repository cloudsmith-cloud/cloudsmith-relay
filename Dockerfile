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

# Runtime stage — glibc (bookworm-slim) is required because Microsoft's omi /
# libpsrpclient.so (WSMan PowerShell Remoting native client) ships only for
# glibc. Alpine (musl) has no PSRP/WSMan native build, which made the Relay
# unable to open a runspace against Windows hosts (AB#1685).
FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS runtime
WORKDIR /app

# Install PowerShell remoting (WSMan) native client libs via the Microsoft
# debian/12 prod repo. The `omi` package provides /opt/omi/lib/libpsrpclient.so
# (and libmi.so), which Microsoft.PowerShell.SDK's RunspaceFactory probes when
# opening a WSMan session. We symlink the libs into /usr/lib so the loader
# resolves them without OMI_HOME wiring.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates \
      curl \
      gnupg \
      libssl3 \
      libkrb5-3 \
 && install -d -m 0755 /usr/share/keyrings \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
      | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg \
 && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft.gpg] https://packages.microsoft.com/debian/12/prod bookworm main" \
      > /etc/apt/sources.list.d/microsoft.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends omi \
 && ln -sf /opt/omi/lib/libpsrpclient.so /usr/lib/libpsrpclient.so \
 && ln -sf /opt/omi/lib/libmi.so /usr/lib/libmi.so \
 && ldconfig \
 && apt-get purge -y curl gnupg \
 && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/*

# Smoke test: fail the build if libpsrpclient.so isn't on the loader path.
RUN ldconfig -p | grep -q libpsrpclient.so \
 || (echo "FATAL: libpsrpclient.so not found in ldconfig cache" && exit 1)

# Relay's local LAN listen port for Agents (mTLS terminating)
EXPOSE 8443

ENV RELAY_LISTEN_PORT=8443 \
    RELAY_IDENTITY_DIR=/var/lib/cloudsmith-relay/identity \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=true

# Run as non-root; identity dir is owned by the relay user so chmod 600 sticks.
RUN groupadd --system relay \
 && useradd  --system --gid relay --shell /usr/sbin/nologin relay \
 && mkdir -p /var/lib/cloudsmith-relay/identity \
 && chown -R relay:relay /var/lib/cloudsmith-relay \
 && chmod 700 /var/lib/cloudsmith-relay/identity
USER relay

COPY --from=build --chown=relay:relay /app/publish .

VOLUME ["/var/lib/cloudsmith-relay/identity"]

ENTRYPOINT ["dotnet", "CloudSmith.Relay.dll"]
