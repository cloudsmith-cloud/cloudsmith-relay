// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Models;

namespace CloudSmith.Relay.Interfaces;

/// <summary>
/// Tracks Agents enrolled with this Relay. Backed by local persistent storage
/// (next-sprint scope) so that enrollment survives Relay restart.
/// </summary>
public interface IAgentRegistry
{
    /// <summary>
    /// Look up the Agent that handles <paramref name="hostId"/>, or null if none.
    /// </summary>
    Task<Agent?> GetAgentForHostAsync(string hostId, CancellationToken ct);

    /// <summary>
    /// Register a new Agent. Validates the one-time enrollment token,
    /// issues a long-lived client cert, and persists the Agent record.
    /// </summary>
    Task RegisterAgentAsync(AgentEnrollmentRequest req, CancellationToken ct);

    /// <summary>
    /// Enumerate every currently-registered Agent.
    /// </summary>
    IEnumerable<Agent> ListAgents();
}
