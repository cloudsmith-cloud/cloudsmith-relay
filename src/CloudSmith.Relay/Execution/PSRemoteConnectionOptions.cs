// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;

namespace CloudSmith.Relay.Execution;

/// <summary>
/// Connection parameters for <see cref="PSRemoteTransport"/>.
///
/// Transport is always HTTPS:5986 — HTTP:5985 is refused.
/// Auth negotiation order (when <see cref="AuthMode"/> is <see cref="PSRemoteAuthMode.Auto"/>):
///   1. Kerberos if the relay process has a domain context (<c>USERDNSDOMAIN</c> env var is set).
///   2. Certificate auth using <see cref="ClientCertificate"/>.
///   3. Fail (InvalidOperationException) if neither is available.
/// </summary>
public sealed class PSRemoteConnectionOptions
{
    /// <summary>FQDN or IP address of the target Hyper-V host.</summary>
    public required string Hostname { get; init; }

    /// <summary>
    /// Auth mode. Use <see cref="PSRemoteAuthMode.Auto"/> (the default) to let
    /// <see cref="PSRemoteTransport"/> negotiate in order: Kerberos → Certificate.
    /// </summary>
    public PSRemoteAuthMode AuthMode { get; init; } = PSRemoteAuthMode.Auto;

    /// <summary>
    /// Username for Kerberos auth, if the ambient process identity is not
    /// sufficient (e.g. running inside a Linux container with a keytab).
    /// Leave <c>null</c> to rely on the ambient Kerberos context.
    /// </summary>
    public string? KerberosUsername { get; init; }

    /// <summary>
    /// Client X.509 certificate used for certificate auth
    /// (<see cref="PSRemoteAuthMode.Certificate"/>). Must not be <c>null</c>
    /// when <see cref="AuthMode"/> is <see cref="PSRemoteAuthMode.Certificate"/>
    /// or when Auto-negotiation falls back to cert.
    /// </summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>WSMan operation timeout in milliseconds. Default 60 000 ms.</summary>
    public int OperationTimeoutMs { get; init; } = 60_000;

    /// <summary>WSMan open (connect) timeout in milliseconds. Default 30 000 ms.</summary>
    public int OpenTimeoutMs { get; init; } = 30_000;

    /// <summary>
    /// SHA-1 thumbprint of the CA certificate (or self-signed server certificate) used
    /// to authenticate the WinRM TLS endpoint on the target host.
    ///
    /// When non-null and non-empty, TLS server validation is <b>enabled</b>:
    /// <see cref="System.Management.Automation.Runspaces.WSManConnectionInfo.SkipCACheck"/>
    /// and <see cref="System.Management.Automation.Runspaces.WSManConnectionInfo.SkipCNCheck"/>
    /// are both set to <c>false</c>, and
    /// <see cref="System.Management.Automation.Runspaces.WSManConnectionInfo.CertificateThumbprint"/>
    /// is set to this value.
    ///
    /// When <c>null</c> (the default), TLS validation is skipped and a
    /// <see cref="Microsoft.Extensions.Logging.LogLevel.Warning"/> is emitted so that
    /// insecure configurations are visible in operator logs. This mode is intended only
    /// for development/test environments where no PKI has been provisioned.
    ///
    /// Phase V will require this to be non-null for all production deployments
    /// (see ADR-057).
    /// </summary>
    public string? CaThumbprint { get; init; }
}
