// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using CloudSmith.Relay.Messages;

namespace CloudSmith.Relay.Lan;

/// <summary>
/// Body of <c>POST /lan/v1/agents/{agentId}/health</c>.
/// Shape mirrors <see cref="HealthProbePush"/> so the Relay can forward it unchanged.
/// </summary>
public sealed class AgentHealthRequest
{
    [JsonPropertyName("clusterId")]
    public string ClusterId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("checks")]
    public List<HealthCheck> Checks { get; set; } = new();
}
