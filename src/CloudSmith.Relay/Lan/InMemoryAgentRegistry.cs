// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Models;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Relay.Lan;

/// <summary>
/// In-memory <see cref="IAgentRegistry"/> backed by a concurrent dictionary.
///
/// Enrollment validates a shared token from <c>RELAY_AGENT_ENROLLMENT_TOKEN</c>
/// and issues each Agent a UUID + shared secret. Phase V replaces this with
/// cert issuance (AB#1666-followup).
///
/// The registry is intentionally ephemeral (resets on Relay restart) — persistence
/// sprint is scoped to Phase V. Agents re-enroll automatically on next boot if
/// the Relay restarted.
/// </summary>
public sealed class InMemoryAgentRegistry : IAgentRegistry
{
    private readonly string _enrollmentToken;
    private readonly ILogger<InMemoryAgentRegistry> _logger;

    // agentId → (Agent record, secret)
    private readonly ConcurrentDictionary<string, (Agent Agent, string Secret)> _byAgentId = new(StringComparer.OrdinalIgnoreCase);

    // hostId (computerName) → agentId — for fast lookup by host
    private readonly ConcurrentDictionary<string, string> _hostToAgent = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryAgentRegistry(string enrollmentToken, ILogger<InMemoryAgentRegistry> logger)
    {
        _enrollmentToken = enrollmentToken;
        _logger = logger;
    }

    /// <summary>
    /// Validates the one-time token, creates a new Agent record, and persists it in memory.
    /// Returns the issued agentId + secret so the caller can write the response body.
    /// </summary>
    public Task<(string AgentId, string Secret)> EnrollAsync(AgentEnrollRequest req, CancellationToken ct)
    {
        if (req.EnrollmentToken != _enrollmentToken)
        {
            _logger.LogWarning("Agent enroll rejected — bad enrollment token from {Host}", req.HostInfo.ComputerName);
            throw new UnauthorizedAccessException("Invalid enrollment token.");
        }

        var agentId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;

        var agent = new Agent(
            AgentId:       agentId,
            HostId:        req.HostInfo.ComputerName,
            Hostname:      req.HostInfo.ComputerName,
            EnrolledAtUtc: now,
            LastSeenUtc:   now);

        _byAgentId[agentId] = (agent, secret);
        _hostToAgent[req.HostInfo.ComputerName] = agentId;

        _logger.LogInformation("Agent enrolled: agentId={AgentId} host={Host}", agentId, req.HostInfo.ComputerName);
        return Task.FromResult((agentId, secret));
    }

    /// <inheritdoc />
    public Task RegisterAgentAsync(AgentEnrollmentRequest req, CancellationToken ct)
    {
        // This interface method is the relay-facing enrollment path (used by StubAgentRegistry
        // contract). For the LAN listener we go through EnrollAsync above. This method is kept
        // to satisfy the interface; the LAN controller bypasses it.
        throw new NotSupportedException("Use EnrollAsync on InMemoryAgentRegistry directly.");
    }

    /// <inheritdoc />
    public Task<Agent?> GetAgentForHostAsync(string hostId, CancellationToken ct)
    {
        Agent? result = null;
        if (_hostToAgent.TryGetValue(hostId, out var agentId) &&
            _byAgentId.TryGetValue(agentId, out var entry))
        {
            result = entry.Agent;
        }
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public IEnumerable<Agent> ListAgents() =>
        _byAgentId.Values.Select(e => e.Agent);

    /// <summary>
    /// Update the last-seen timestamp for an existing agent.
    /// Returns false if the agentId is unknown.
    /// </summary>
    public bool Heartbeat(string agentId)
    {
        if (!_byAgentId.TryGetValue(agentId, out var entry)) return false;

        var updated = entry.Agent with { LastSeenUtc = DateTimeOffset.UtcNow };
        _byAgentId[agentId] = (updated, entry.Secret);
        return true;
    }

    /// <summary>
    /// Validate that the presented secret matches the one issued at enrollment.
    /// Returns false if the agentId is unknown or the secret is wrong.
    /// </summary>
    public bool ValidateSecret(string agentId, string secret)
    {
        if (!_byAgentId.TryGetValue(agentId, out var entry)) return false;
        return entry.Secret == secret;
    }
}
