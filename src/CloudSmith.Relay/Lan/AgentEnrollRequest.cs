// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace CloudSmith.Relay.Lan;

/// <summary>
/// Body of <c>POST /lan/v1/agents/enroll</c> sent by an Agent on first boot.
/// </summary>
public sealed class AgentEnrollRequest
{
    [JsonPropertyName("enrollmentToken")]
    public string EnrollmentToken { get; set; } = string.Empty;

    [JsonPropertyName("hostInfo")]
    public AgentHostInfo HostInfo { get; set; } = new();
}

/// <summary>
/// Host metadata included in the enroll request.
/// </summary>
public sealed class AgentHostInfo
{
    [JsonPropertyName("computerName")]
    public string ComputerName { get; set; } = string.Empty;

    [JsonPropertyName("ipAddresses")]
    public List<string> IpAddresses { get; set; } = new();

    [JsonPropertyName("os")]
    public string Os { get; set; } = string.Empty;
}
