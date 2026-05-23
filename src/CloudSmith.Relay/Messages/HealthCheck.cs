// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Messages;

/// <summary>
/// A single named probe result inside a <see cref="HealthProbePush"/>.
/// </summary>
/// <param name="Name">Probe identifier (e.g. "cluster.heartbeat", "csv.online").</param>
/// <param name="Status">"Healthy" | "Degraded" | "Unhealthy".</param>
/// <param name="Detail">Optional human-readable detail.</param>
public sealed record HealthCheck(
    string Name,
    string Status,
    string? Detail);
