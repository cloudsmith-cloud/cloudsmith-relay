// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Connection;

/// <summary>
/// Configuration consumed by <see cref="WebSocketRelayConnection"/>.
/// </summary>
public sealed class RelayConnectionOptions
{
    /// <summary>Base PaaS URL (https://...). Will be rewritten to wss:// for the WebSocket.</summary>
    public required string PaasUrl { get; init; }

    /// <summary>
    /// Relay identifier returned from enrollment. Settable so the hosted service
    /// can patch it in after first-run enrollment completes — the WebSocket loop
    /// reads this each connect attempt.
    /// </summary>
    public string RelayId { get; set; } = "pending";

    /// <summary>Path to the PEM-encoded RSA private key for client authentication.</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>Initial backoff delay after a disconnect.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum backoff delay between reconnect attempts.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Keepalive ping interval for the underlying WebSocket.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Receive buffer size in bytes.</summary>
    public int ReceiveBufferSize { get; init; } = 64 * 1024;
}
