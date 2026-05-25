// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace CloudSmith.Relay.Lan;

public sealed class AgentJobResultRequest
{
    [JsonPropertyName("jobId")]    public string JobId    { get; set; } = string.Empty;
    [JsonPropertyName("succeeded")] public bool  Succeeded { get; set; }
    [JsonPropertyName("exitCode")] public int   ExitCode  { get; set; }
    [JsonPropertyName("output")]   public string? Output   { get; set; }
    [JsonPropertyName("error")]    public string? Error    { get; set; }
}
