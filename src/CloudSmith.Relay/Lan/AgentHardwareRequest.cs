// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace CloudSmith.Relay.Lan;

public sealed class AgentHardwareRequest
{
    [JsonPropertyName("hostId")]           public string  HostId            { get; set; } = string.Empty;
    [JsonPropertyName("processorCount")]   public int     ProcessorCount    { get; set; }
    [JsonPropertyName("logicalCoreCount")] public int     LogicalCoreCount  { get; set; }
    [JsonPropertyName("processorName")]    public string? ProcessorName     { get; set; }
    [JsonPropertyName("totalMemoryBytes")] public long    TotalMemoryBytes  { get; set; }
}
