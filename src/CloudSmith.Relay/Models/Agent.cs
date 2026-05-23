// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Models;

/// <summary>
/// A registered Agent connected to this Relay. Identity is the agent's enrollment cert thumbprint.
/// </summary>
/// <param name="AgentId">Stable agent identifier (GUID string).</param>
/// <param name="HostId">Host the agent runs on / manages.</param>
/// <param name="Hostname">DNS or NetBIOS name reported by the agent.</param>
/// <param name="EnrolledAtUtc">When enrollment completed.</param>
/// <param name="LastSeenUtc">Last heartbeat timestamp.</param>
public sealed record Agent(
    string AgentId,
    string HostId,
    string Hostname,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset LastSeenUtc);
