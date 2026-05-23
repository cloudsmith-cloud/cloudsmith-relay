// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Models;

namespace CloudSmith.Relay.Interfaces;

/// <summary>
/// Tracks each managed host's <see cref="HostState"/> (Workgroup / Joining /
/// DomainJoined / Unknown). Drives credential selection inside
/// <see cref="IPSRemoteExecutor"/>.
/// </summary>
public interface IHostStateTracker
{
    /// <summary>Update the cached state for a host. Idempotent.</summary>
    void UpdateState(string hostId, HostState newState);

    /// <summary>
    /// Read the cached state. Returns <see cref="HostState.Unknown"/> if the host
    /// has never been observed.
    /// </summary>
    HostState GetState(string hostId);

    /// <summary>Snapshot of every tracked host's state.</summary>
    IReadOnlyDictionary<string, HostState> Snapshot();
}
