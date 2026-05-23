// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Messages;
using CloudSmith.Relay.Models;

namespace CloudSmith.Relay.Stubs;

/// <summary>
/// Placeholder <see cref="IPSRemoteExecutor"/> used when no PSRemote credential is
/// configured. Returns empty results rather than throwing, so the scan worker
/// degrades gracefully instead of crashing.
/// </summary>
public sealed class StubPSRemoteExecutor : IPSRemoteExecutor
{
    public Task<PSResult> InvokeAsync(
        string hostId,
        string script,
        IDictionary<string, object>? args,
        CancellationToken ct) =>
        Task.FromResult(new PSResult([], [], Success: false));

    public Task<IReadOnlyList<VmSnapshot>> GetInventoryAsync(
        string hostId,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VmSnapshot>>(Array.Empty<VmSnapshot>());
}
