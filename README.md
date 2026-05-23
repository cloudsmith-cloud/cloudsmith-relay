# cloudsmith-relay

Per-site Linux bridge between CloudSmith PaaS and local-site Agents / PSRemote-managed Hosts.

Packaged as a **container or Linux service** — not a dedicated VM appliance.

## Purpose

The Relay is the single point of presence CloudSmith places inside a customer site
to broker work between the cloud control plane and the things that actually live
on the local network: Hyper-V hosts, Windows Server Failover Clusters, Azure Local
clusters, and other managed nodes.

A Relay:

- Holds an outbound, long-lived, mTLS-authenticated connection to PaaS — no inbound firewall holes.
- Listens on the local LAN for Agents (port `8443`) and accepts their enrolment / heartbeats / job results.
- Executes PowerShell Remoting (WSMan / WinRM over HTTPS) against hosts that don't run an Agent — using either Kerberos (domain-joined) or local/certificate credentials (workgroup), selected via the dual-credential state machine.
- Reports host state, job results, and telemetry back upstream.

See **[ADR-007 — Relay topology (2026-05-23 update)](../cloudsmith-internal/adrs/adr-007-relay-topology.md)** for the architectural decision driving this repo.

## Architecture

```
                            +------------------------+
                            |    CloudSmith PaaS     |
                            |  (cloudsmith-api etc.) |
                            +-----------+------------+
                                        |
                              mTLS / WebSocket
                              outbound from site
                                        |
                          +-------------v-------------+
                          |    cloudsmith-relay       |   <-- this repo
                          |    (Linux container)      |
                          |  - enrollment             |
                          |  - agent registry         |
                          |  - job dispatch           |
                          |  - PSRemote executor      |
                          +------+------------+-------+
                                 |            |
                  local LAN      |            |   local LAN
                  port 8443      |            |   WSMan 5986
                                 |            |
                       +---------v--+     +---v----------+
                       |   Agent    |     | Host (no     |
                       |  (Win/Lin) |     | agent — Win) |
                       | reports +  |     | runs PS only |
                       | executes   |     | via PSRemote |
                       +------------+     +--------------+
```

Two execution paths from Relay -> Host:

1. **Agent path** — preferred. Agent is installed and registered; Relay sends jobs over its open back-channel.
2. **PSRemote path** — for un-agented hosts. Relay opens a WSMan session and runs PowerShell directly, picking credentials based on the host's join state.

## Build

### Container

```bash
docker build -t cloudsmith-relay:dev .
```

### .NET (local dev)

```bash
dotnet restore
dotnet build
dotnet test
```

## Run

### docker run (single-site)

Mount a host-writable directory at `/var/lib/cloudsmith-relay/identity` so the
private key + identity survive container restarts. On first run, `RELAY_ENROLLMENT_TOKEN`
is consumed once and the resulting identity persists in the mounted volume —
subsequent restarts reconnect without re-enrolling.

```bash
docker volume create cloudsmith-relay-identity

docker run -d --name cloudsmith-relay \
  -e RELAY_PAAS_URL=https://api.cloudsmith.cloud \
  -e RELAY_ENROLLMENT_TOKEN=ott_2gI9...redacted... \
  -e RELAY_DISPLAY_NAME="site-eastus-relay-01" \
  -e RELAY_CLUSTER_ID=eastus-cluster-01 \
  -e RELAY_LISTEN_PORT=8443 \
  -p 8443:8443 \
  -v cloudsmith-relay-identity:/var/lib/cloudsmith-relay/identity \
  ghcr.io/cloudsmith-cloud/cloudsmith-relay:latest
```

Inspect logs:

```bash
docker logs -f cloudsmith-relay
# expected: "Relay enrolled: RelayId=relay-..." (first run)
#           "Loaded persisted Relay identity: relay-..." (subsequent runs)
#           "Relay WebSocket connected to wss://api.cloudsmith.cloud/api/v1/relays/.../connect"
#           "Heartbeat ..." every 30s
```

### docker-compose

```bash
RELAY_PAAS_URL=https://api.cloudsmith.cloud \
RELAY_ENROLLMENT_TOKEN=<token> \
RELAY_DISPLAY_NAME=site-01-relay \
docker compose up
```

### bare dotnet (local dev)

```bash
export RELAY_PAAS_URL=https://api.cloudsmith.cloud
export RELAY_ENROLLMENT_TOKEN=<token>
export RELAY_DISPLAY_NAME=dev-relay
export RELAY_LISTEN_PORT=8443
export RELAY_IDENTITY_DIR=/tmp/cloudsmith-relay-identity
dotnet run --project src/CloudSmith.Relay
```

### What this build does

- **First run**: generates an RSA-2048 keypair, POSTs `{token, displayName, publicKeyPem}` to `${RELAY_PAAS_URL}/api/v1/relays/enroll`, persists the returned `relayId` + private key under `RELAY_IDENTITY_DIR` (chmod 600 on Linux).
- **Steady state**: holds an outbound WebSocket to `wss://${PAAS}/api/v1/relays/{relayId}/connect` with jittered exponential-backoff reconnect (1s → 2s → … → capped at 2m).
- **Inbound**: accepts `JobDispatch` messages and ACKs them with `JobAck`. Actual job execution is wired in AB#1666-followup.
- **Outbound**: heartbeats every 30s; pushes an `InventoryPush` every 5m (empty list until Agent enrollment lands).
- **Out of scope tonight**: the LAN-side Agent enrollment listener on `:8443` — `IAgentRegistry` and `IPSRemoteExecutor` are still stubs.

## Roadmap

Implementation phases, each tracked as a separate ADO Feature / Story sprint:

1. **Enrollment** — single-use token -> long-lived client cert / identity; Relay registers itself with PaaS.
2. **mTLS upstream channel** — persistent outbound WebSocket to PaaS; reconnect / backoff / heartbeats.
3. **Agent registry** — local listener on `:8443`, agent enrollment + heartbeat tracking + revocation.
4. **Job dispatch** — receive jobs from PaaS, route to the right Agent, return results upstream.
5. **PSRemote dual-credential state machine** — domain-joined => Kerberos; workgroup => local creds / cert; tracks join-state transitions per host.

Each phase ships behind a feature flag so partial Relays can deploy safely.

## License

Apache 2.0 — see [LICENSE](LICENSE).
