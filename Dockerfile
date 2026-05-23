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

# --- WSMan native libs stage (AB#1685) ------------------------------------
# Microsoft.PowerShell.SDK opens WSMan runspaces via libpsrpclient.so + libmi.so.
# Neither the SDK nupkg nor the PowerShell 7.x distribution still ships these
# on Linux — they were forked into the community-maintained PSWSMan module.
# We extract the glibc-3 variant from the PSWSMan nupkg and stage them so the
# runtime image can place them on the loader path.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS wsmanlibs
WORKDIR /wsman
ARG PSWSMAN_VERSION=2.3.1
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl unzip ca-certificates \
 && curl -fsSL "https://www.powershellgallery.com/api/v2/package/PSWSMan/${PSWSMAN_VERSION}" -o pswsman.nupkg \
 && unzip -q pswsman.nupkg -d pswsman \
 && cp pswsman/bin/glibc-3/libpsrpclient.so /wsman/libpsrpclient.so \
 && cp pswsman/bin/glibc-3/libmi.so         /wsman/libmi.so \
 && test -f /wsman/libpsrpclient.so \
 && test -f /wsman/libmi.so

# --- Runtime stage --------------------------------------------------------
# glibc base (bookworm-slim) is required because Microsoft's PSWSMan natives
# are built for glibc only; the linux-musl-x64 variant of libpsrpclient does
# not exist in any official source (root cause of AB#1685).
FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS runtime
WORKDIR /app

# Runtime deps for the PSWSMan natives:
#   * libssl3   — TLS for the WSMan client
#   * libkrb5-3 — Kerberos auth path (domain-joined hosts; ADR-007 amendment)
#   * libpam0g  — required by libmi.so's authentication shim
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates \
      libssl3 \
      libkrb5-3 \
      libpam0g \
 && rm -rf /var/lib/apt/lists/*

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

# Publish output goes first so that we can drop the WSMan natives into
# /app/runtimes/linux-x64/native/ after (the SDK's NativeLibrary loader probes
# that path — same one the iter 1 stack trace showed).
COPY --from=build --chown=relay:relay /app/publish .

# Drop the WSMan native libs into the dotnet NativeLibrary probe path, and
# symlink them into /usr/lib so anything that bypasses the runtimes/ probe
# (direct dlopen by base name) can also resolve them.
COPY --from=wsmanlibs --chown=relay:relay /wsman/libpsrpclient.so /app/runtimes/linux-x64/native/libpsrpclient.so
COPY --from=wsmanlibs --chown=relay:relay /wsman/libmi.so         /app/runtimes/linux-x64/native/libmi.so
RUN ln -sf /app/runtimes/linux-x64/native/libpsrpclient.so /usr/lib/libpsrpclient.so \
 && ln -sf /app/runtimes/linux-x64/native/libmi.so         /usr/lib/libmi.so \
 && ldconfig

# Smoke test: ldconfig must report libpsrpclient.so and libmi.so. If not the
# build is broken and we want it to fail here, not at runtime.
RUN (ldconfig -p | grep -q libpsrpclient.so) \
 && (ldconfig -p | grep -q libmi.so) \
 || (echo "FATAL: WSMan native libs missing from ldconfig cache" \
     && (ldconfig -p | grep -E 'libpsrp|libmi' || true) \
     && exit 1)

USER relay

VOLUME ["/var/lib/cloudsmith-relay/identity"]

ENTRYPOINT ["dotnet", "CloudSmith.Relay.dll"]
