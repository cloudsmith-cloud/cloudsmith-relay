// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Messages;
using CloudSmith.Relay.Models;

namespace CloudSmith.Relay.Interfaces;

/// <summary>
/// Executes a PowerShell script against a remote managed host over WSMan.
/// Implementations apply the dual-credential state machine:
/// Kerberos for <see cref="HostState.DomainJoined"/> hosts; local /
/// certificate creds for <see cref="HostState.Workgroup"/> and
/// <see cref="HostState.Joining"/> hosts. See ADR-007 (2026-05-23 update).
/// </summary>
public interface IPSRemoteExecutor
{
    /// <summary>
    /// Invoke <paramref name="script"/> on the host identified by <paramref name="hostId"/>.
    /// </summary>
    /// <param name="hostId">Stable host identifier — must already be tracked.</param>
    /// <param name="script">PowerShell script text to execute on the target.</param>
    /// <param name="args">Optional named script parameters.</param>
    /// <param name="ct">Cancellation token for the runspace invocation.</param>
    Task<PSResult> InvokeAsync(
        string hostId,
        string script,
        IDictionary<string, object>? args,
        CancellationToken ct);

    /// <summary>
    /// Run <c>Get-VM</c> against <paramref name="hostId"/> via WinRM and return
    /// a <see cref="VmSnapshot"/> per discovered virtual machine. (AB#1680)
    /// </summary>
    /// <param name="hostId">Hostname or IP of the Hyper-V host to scan.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<VmSnapshot>> GetInventoryAsync(string hostId, CancellationToken ct);
}
