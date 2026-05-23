// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Messages;

namespace CloudSmith.Relay.Connection;

/// <summary>
/// Persistent outbound WebSocket connection from this Relay to PaaS.
/// Implementations:
///   - Connect to <c>wss://{paas}/api/v1/relays/{relayId}/connect</c>.
///   - Authenticate via the persisted Relay private key / client cert.
///   - Raise <see cref="OnMessageReceived"/> for every inbound <see cref="RelayMessage"/>.
///   - Auto-reconnect with exponential backoff on disconnect.
/// </summary>
public interface IRelayConnection : IAsyncDisposable
{
    /// <summary>
    /// Open the upstream WebSocket and start the receive + reconnect loops.
    /// Idempotent — calling again while open is a no-op.
    /// </summary>
    Task OpenAsync(CancellationToken ct);

    /// <summary>
    /// Send a message to PaaS over the open WebSocket. Throws if the connection
    /// is currently down — callers may buffer/retry.
    /// </summary>
    Task SendAsync(RelayMessage msg, CancellationToken ct);

    /// <summary>True iff the WebSocket is currently in the Open state.</summary>
    bool IsConnected { get; }

    /// <summary>Raised for every inbound <see cref="RelayMessage"/>.</summary>
    event AsyncEventHandler<RelayMessage> OnMessageReceived;
}
