// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Models;

namespace CloudSmith.Relay.Stubs;

/// <summary>
/// Placeholder <see cref="IAgentRegistry"/>. Throws on every call; replaced by the
/// real persistent registry in the agent-registry sprint.
/// </summary>
public sealed class StubAgentRegistry : IAgentRegistry
{
    public Task<Agent?> GetAgentForHostAsync(string hostId, CancellationToken ct) =>
        throw new NotImplementedException(
            "AB#XXXX not yet implemented — agent registry lookup (Roadmap phase 3).");

    public Task RegisterAgentAsync(AgentEnrollmentRequest req, CancellationToken ct) =>
        throw new NotImplementedException(
            "AB#XXXX not yet implemented — agent enrollment (Roadmap phase 1 + 3).");

    public IEnumerable<Agent> ListAgents() =>
        throw new NotImplementedException(
            "AB#XXXX not yet implemented — agent registry enumeration (Roadmap phase 3).");
}
