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
    /// Shared secret used by the Agent on subsequent requests.
    /// Phase V will replace with client cert / mTLS (AB#1666-followup).
    /// </summary>
    [JsonPropertyName("agentSecret")]
    public string AgentSecret { get; set; } = string.Empty;
}
