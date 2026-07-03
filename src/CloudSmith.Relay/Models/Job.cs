// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Models;

/// <summary>
/// A job accepted from PaaS and queued on this Relay for delivery to an Agent
/// (LAN poll) or the PSRemote execution path. Field shapes are canonical per
/// the frozen job dispatch contract (AB#4839).
/// </summary>
/// <param name="JobId">Primary key of the core.jobs row - echoed on every hop.</param>
/// <param name="AgentId">Target agent, or the PSRemote pseudo-agent id.</param>
/// <param name="JobType">Logical operation identifier, e.g. <c>cluster.validate-network</c>.</param>
/// <param name="PayloadJson">Opaque JSON payload - parsed only by the executing target.</param>
/// <param name="IdempotencyKey">Client-supplied dedupe key (nullable).</param>
/// <param name="Traceparent">W3C trace context (nullable).</param>
public sealed record QueuedJob(
    Guid JobId,
    string AgentId,
    string JobType,
    string PayloadJson,
    string? IdempotencyKey,
    string? Traceparent);
