// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Workers;

/// <summary>
/// Top-level Relay process configuration, populated from environment variables
/// in <c>Program.cs</c>.
/// </summary>
public sealed class RelayOptions
{
    /// <summary>PaaS base URL — RELAY_PAAS_URL.</summary>
    public required string PaasUrl { get; init; }

    /// <summary>One-time enrollment token — RELAY_ENROLLMENT_TOKEN. Optional after first enrollment.</summary>
    public string? EnrollmentToken { get; init; }

    /// <summary>Display name registered with PaaS — RELAY_DISPLAY_NAME.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Local LAN listen port for Agents — RELAY_LISTEN_PORT (default 8443).</summary>
    public int ListenPort { get; init; } = 8443;

    /// <summary>On-disk path holding the persisted private key + identity.json.</summary>
    public string IdentityDirectory { get; init; } =
        Enrollment.RelayEnrollmentClient.DefaultIdentityDirectory;

    /// <summary>Cluster id reported alongside inventory pushes — RELAY_CLUSTER_ID (default "demo").</summary>
    public string ClusterId { get; init; } = "demo";

    /// <summary>Heartbeat cadence to PaaS.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Inventory scan cadence.</summary>
    public TimeSpan InventoryScanInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Site identifier this Relay is associated with — RELAY_SITE_ID.
    /// When set, the Relay includes this value in registration and heartbeat payloads
    /// so the API can correlate relay activity back to the site.
    /// Optional: when absent the Relay operates without site association.
    /// </summary>
    public string? SiteId { get; init; }

    /// <summary>
    /// Hyper-V hosts to scan directly via PSRemote when no Agent is enrolled.
    /// Populated from <c>RELAY_HYPER_V_HOSTS</c> (comma-separated hostnames/IPs).
    /// Empty list = liveness-only mode (pushes empty inventory).
    /// </summary>
    public IReadOnlyList<string> HyperVHosts { get; init; } = Array.Empty<string>();
}
