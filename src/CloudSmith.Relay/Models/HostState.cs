// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Models;

/// <summary>
/// Lifecycle state of a managed host with respect to domain membership.
/// Drives credential-path selection inside <see cref="Interfaces.IPSRemoteExecutor"/>.
/// See ADR-007 (2026-05-23 update).
/// </summary>
public enum HostState
{
    /// <summary>State has not been observed/enrolled.</summary>
    Unknown = 0,

    /// <summary>Standalone workgroup host (not domain-joined, no pending join).</summary>
    Workgroup = 1,

    /// <summary>Domain-join workflow in progress; credentials may still be local.</summary>
    Joining = 2,

    /// <summary>Host is fully domain-joined and Kerberos auth is available.</summary>
    DomainJoined = 3,
}
