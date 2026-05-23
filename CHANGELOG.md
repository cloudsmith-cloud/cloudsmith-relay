# Changelog

All notable changes to **cloudsmith-relay** will be documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.0.1] - 2026-05-23

### Added

- Initial repo scaffold.
- Worker Service host with Serilog structured logging and OpenTelemetry tracing/metrics.
- Functional Relay enrollment client: RSA-2048 keygen, `POST` to the PaaS enrollment endpoint, and identity persistence with `chmod 600` on the key material.
- WebSocket connection to the PaaS with reconnect, exponential backoff, and jitter.
- Polymorphic `RelayMessage` union covering `JobDispatch`, `JobAck`, `InventoryPush`, `HealthProbePush`, and `Heartbeat`.
- PSRemote dual-credential scaffold: `Workgroup` and `Joining` paths use `LocalCertBased`; `DomainJoined` uses `Kerberos`.
- xUnit tests covering enrollment, `RelayMessage` JSON round-trip, and `CredentialResolver`.
