// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;

namespace CloudSmith.Relay.Execution;

/// <summary>
/// Credential used by <see cref="PSRemoteExecutor"/> when the target host is
/// NOT domain-joined (Workgroup / Joining / Unknown states). Username/Password come
/// from environment variables: <c>RELAY_PSREMOTE_USERNAME</c> and
/// <c>RELAY_PSREMOTE_PASSWORD</c>.
///
/// For domain-joined hosts, Kerberos is used instead and Username/Password are
/// ignored (see ADR-007 credential-path selection).
///
/// AB#1666: <see cref="ClientCertificate"/> is now optional. When set, the
/// <see cref="PSRemoteTransport"/> certificate-auth path is preferred over Basic auth.
/// The certificate is consumed only during connection setup and is not retained by
/// <see cref="PSRemoteSession"/> beyond the connection lifetime.
/// </summary>
public sealed class PSRemoteCredential
{
    /// <summary>Username for local WinRM authentication (e.g. <c>Administrator</c>).</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Plaintext password. Must be zeroised after use by the OS process boundary.</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Optional client X.509 certificate for certificate-based WinRM auth (AB#1666).
    /// When non-null, <see cref="PSRemoteExecutor"/> uses
    /// <see cref="PSRemoteTransport"/> certificate path instead of Basic auth.
    /// Must not be stored beyond the connection lifetime.
    /// </summary>
    public X509Certificate2? ClientCertificate { get; init; }
}
