// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Models;

namespace CloudSmith.Relay.State;

/// <summary>
/// In-memory <see cref="IHostStateTracker"/>. Persistence across restarts is
/// next-sprint scope (AB#1666-followup).
/// </summary>
public sealed class HostStateTracker : IHostStateTracker
{
    private readonly ConcurrentDictionary<string, HostState> _states = new(StringComparer.OrdinalIgnoreCase);

    public void UpdateState(string hostId, HostState newState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        _states[hostId] = newState;
    }

    public HostState GetState(string hostId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        return _states.TryGetValue(hostId, out var s) ? s : HostState.Unknown;
    }

    public IReadOnlyDictionary<string, HostState> Snapshot()
        => new Dictionary<string, HostState>(_states, StringComparer.OrdinalIgnoreCase);
}
