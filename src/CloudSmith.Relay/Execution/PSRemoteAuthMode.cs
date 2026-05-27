// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Execution;

/// <summary>
/// Auth mechanism to use when opening a PSRemote connection.
/// Resolved by <see cref="PSRemoteTransport"/> according to the auth-negotiation
/// order defined in AB#1666.
/// </summary>
public enum PSRemoteAuthMode
{
    /// <summary>
    /// Let <see cref="PSRemoteTransport"/> auto-detect: try Kerberos if the relay
    /// process is running in a domain context (<c>USERDNSDOMAIN</c> is set), then
    /// fall back to certificate auth.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Force Kerberos (Negotiate). The relay's ambient machine/user identity is
    /// used — no explicit credential object. Requires the host to be reachable on
    /// HTTPS:5986.
    /// </summary>
    Kerberos = 1,

    /// <summary>
    /// Client-certificate auth over HTTPS:5986. <see cref="PSRemoteConnectionOptions.ClientCertificate"/>
    /// must be supplied.
    /// </summary>
    Certificate = 2,
}
