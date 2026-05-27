// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Management.Automation.Runspaces;

namespace CloudSmith.Relay.Execution;

/// <summary>
/// An open PSRemote session returned by <see cref="PSRemoteTransport.ConnectAsync"/>.
/// Wraps the underlying WSMan <see cref="Runspace"/> and disposes it (closing the
/// session) when the caller is done.  Credentials are not retained by this type —
/// they are consumed only during the <see cref="PSRemoteTransport.ConnectAsync"/>
/// call and are not stored beyond the connection lifetime.
/// </summary>
public sealed class PSRemoteSession : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// The open WSMan runspace.  Callers may create a <c>PowerShell</c> instance
    /// against it and invoke scripts, then dispose this session to close the connection.
    /// </summary>
    public Runspace Runspace { get; }

    /// <summary>Auth mode that was actually used to open this session.</summary>
    public PSRemoteAuthMode AuthModeUsed { get; }

    internal PSRemoteSession(Runspace runspace, PSRemoteAuthMode authModeUsed)
    {
        Runspace     = runspace;
        AuthModeUsed = authModeUsed;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Runspace.Dispose();
    }
}
