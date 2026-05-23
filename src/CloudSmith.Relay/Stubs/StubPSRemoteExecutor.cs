// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Models;

namespace CloudSmith.Relay.Stubs;

/// <summary>
/// Placeholder <see cref="IPSRemoteExecutor"/>. Throws on every call; replaced by the
/// real dual-credential WSMan implementation in the PSRemote sprint.
/// </summary>
public sealed class StubPSRemoteExecutor : IPSRemoteExecutor
{
    public Task<PSResult> InvokeAsync(
        string hostId,
        string script,
        IDictionary<string, object>? args,
        CancellationToken ct) =>
        throw new NotImplementedException(
            "AB#XXXX not yet implemented — PSRemote dual-credential executor (Roadmap phase 5).");
}
