// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Execution;

/// <summary>
/// Credential used by <see cref="PSRemoteExecutor"/> when the target host is
/// NOT domain-joined (Workgroup / Joining / Unknown states). Both values come
/// from environment variables: <c>RELAY_PSREMOTE_USERNAME</c> and
/// <c>RELAY_PSREMOTE_PASSWORD</c>.
///
/// For domain-joined hosts, Kerberos is used instead and this credential is
/// ignored (see ADR-007 credential-path selection).
/// </summary>
public sealed class PSRemoteCredential
{
    /// <summary>Username for local WinRM authentication (e.g. <c>Administrator</c>).</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Plaintext password. Must be zeroised after use by the OS process boundary.</summary>
    public string Password { get; init; } = string.Empty;
}
