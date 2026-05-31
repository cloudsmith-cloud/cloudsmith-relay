// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace CloudSmith.Relay.Lan;

/// <summary>
/// Response from <c>POST /lan/v1/agents/enroll</c>.
/// </summary>
public sealed class AgentEnrollResponse
{
    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Per-agent JWT signed by the relay's RSA private key (RS256).
    /// The agent presents this as the <c>X-Agent-Token</c> header on all subsequent
    /// LAN requests.  The JSON property name is kept as <c>agentSecret</c> for
    /// backward compatibility with existing agent clients.
    /// </summary>
    [JsonPropertyName("agentSecret")]
    public string AgentSecret { get; set; } = string.Empty;
}
