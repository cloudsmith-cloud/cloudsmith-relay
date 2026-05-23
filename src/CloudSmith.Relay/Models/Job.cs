// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Models;

/// <summary>
/// A job dispatched from PaaS to the Relay for execution against an Agent or via PSRemote.
/// </summary>
/// <param name="JobId">Stable job identifier (GUID string).</param>
/// <param name="HostId">Target host.</param>
/// <param name="Kind">Logical job kind (e.g. "PSRemote.Invoke", "Agent.RunModule").</param>
/// <param name="Payload">Opaque JSON payload — interpreted by the dispatch target.</param>
public sealed record Job(
    string JobId,
    string HostId,
    string Kind,
    string Payload);

/// <summary>
/// Result of a Job, returned upstream to PaaS.
/// </summary>
/// <param name="JobId">Matches <see cref="Job.JobId"/>.</param>
/// <param name="Success">True iff the job completed without terminating errors.</param>
/// <param name="Output">Result body (JSON-encoded).</param>
/// <param name="Error">Error detail if <see cref="Success"/> is false.</param>
/// <param name="CompletedAtUtc">When the result was produced.</param>
public sealed record JobResult(
    string JobId,
    bool Success,
    string? Output,
    string? Error,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Event args raised by <see cref="Interfaces.IRelayConnection.OnJobAssigned"/>.
/// </summary>
public sealed class JobAssignedEventArgs(Job job) : EventArgs
{
    public Job Job { get; } = job;
}
