// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using CloudSmith.Relay.Messages;

namespace CloudSmith.Relay.Lan;

/// <summary>
/// Body of <c>POST /lan/v1/agents/{agentId}/inventory</c>.
/// Shape mirrors <see cref="InventoryPush"/> so the Relay can forward it unchanged
/// over the PaaS WebSocket.
/// </summary>
public sealed class AgentInventoryRequest
{
    [JsonPropertyName("clusterId")]
    public string ClusterId { get; set; } = string.Empty;

    [JsonPropertyName("vms")]
    public List<VmSnapshot> Vms { get; set; } = new();
}
