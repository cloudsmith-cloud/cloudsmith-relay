// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Relay.Execution;

/// <summary>
/// Separate transport abstraction for PSRemote connections (AB#1666).
///
/// Responsibilities:
///   • Build a <see cref="WSManConnectionInfo"/> using the correct auth mechanism.
///   • Open the WSMan <see cref="Runspace"/> asynchronously.
///   • Return a disposable <see cref="PSRemoteSession"/> so the caller controls
///     the connection lifetime and credentials are not retained longer than needed.
///
/// Auth negotiation order (when <see cref="PSRemoteAuthMode.Auto"/>):
///   1. Kerberos  — used if <see cref="PSRemoteConnectionOptions.AuthMode"/> ==
///      <see cref="PSRemoteAuthMode.Kerberos"/> OR if the relay process is running
///      in a domain context (the <c>USERDNSDOMAIN</c> environment variable is set
///      and non-empty).
///   2. Certificate — used if <see cref="PSRemoteAuthMode.Certificate"/> is forced,
///      or as the Kerberos fallback when Auto is selected but the relay is not
///      domain-joined. Requires <see cref="PSRemoteConnectionOptions.ClientCertificate"/>.
///   3. NEVER HTTP:5985 — the transport uses HTTPS:5986 exclusively. If neither
///      Kerberos nor certificate auth is available, <see cref="ConnectAsync"/> throws
///      <see cref="InvalidOperationException"/> rather than downgrading to HTTP.
///
/// This class is kept separate from <see cref="PSRemoteExecutor"/> so that the
/// auth/transport logic can be unit-tested without instantiating a runspace and
/// so that alternative callers (e.g. a future job-runner) can use it directly.
/// </summary>
public sealed class PSRemoteTransport
{
    private readonly ILogger<PSRemoteTransport> _logger;

    // Seam for testing: override the USERDNSDOMAIN look-up.
    private readonly Func<string?> _getDomainEnv;

    /// <summary>Production constructor — reads <c>USERDNSDOMAIN</c> from the real env.</summary>
    public PSRemoteTransport(ILogger<PSRemoteTransport> logger)
        : this(logger, () => Environment.GetEnvironmentVariable("USERDNSDOMAIN"))
    {
    }

    /// <summary>
    /// Internal constructor used by tests to inject a custom domain-env resolver,
    /// eliminating the need to mutate the process environment in tests.
    /// </summary>
    internal PSRemoteTransport(ILogger<PSRemoteTransport> logger, Func<string?> getDomainEnv)
    {
        _logger      = logger;
        _getDomainEnv = getDomainEnv;
    }

    /// <summary>
    /// Open a PSRemote session to the host described by <paramref name="options"/>.
    /// </summary>
    /// <param name="options">Connection parameters (hostname, auth mode, certificate).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A connected, open <see cref="PSRemoteSession"/>. The caller is responsible for
    /// disposing it; disposal closes the WSMan runspace.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when neither Kerberos nor certificate auth is available (HTTP:5985 is never used).
    /// </exception>
    public Task<PSRemoteSession> ConnectAsync(PSRemoteConnectionOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            var (connInfo, authModeUsed) = BuildConnectionInfo(options);

            _logger.LogInformation(
                "PSRemoteTransport: connecting to {Host} via {AuthMode} on HTTPS:5986",
                options.Hostname, authModeUsed);

            var runspace = RunspaceFactory.CreateRunspace(connInfo);
            runspace.Open();

            _logger.LogInformation(
                "PSRemoteTransport: connected to {Host} (auth={AuthMode})",
                options.Hostname, authModeUsed);

            return new PSRemoteSession(runspace, authModeUsed);
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Connection-info builder — internal so tests can call it directly without
    // opening a real runspace.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Build a <see cref="WSManConnectionInfo"/> for the given options.
    /// Returns the info object and the auth mode that was actually chosen.
    /// </summary>
    internal (WSManConnectionInfo Info, PSRemoteAuthMode AuthModeUsed) BuildConnectionInfo(
        PSRemoteConnectionOptions options)
    {
        var resolvedMode = ResolveAuthMode(options);

        WSManConnectionInfo connInfo = resolvedMode switch
        {
            PSRemoteAuthMode.Kerberos    => BuildKerberosInfo(options),
            PSRemoteAuthMode.Certificate => BuildCertificateInfo(options),
            _ => throw new InvalidOperationException(
                $"Unhandled resolved auth mode: {resolvedMode}")
        };

        connInfo.OperationTimeout                    = options.OperationTimeoutMs;
        connInfo.OpenTimeout                         = options.OpenTimeoutMs;
        connInfo.MaximumReceivedDataSizePerCommand    = 50 * 1024 * 1024; // 50 MB

        return (connInfo, resolvedMode);
    }

    // -------------------------------------------------------------------------
    // Auth-mode resolver
    // -------------------------------------------------------------------------

    private PSRemoteAuthMode ResolveAuthMode(PSRemoteConnectionOptions options)
    {
        if (options.AuthMode == PSRemoteAuthMode.Kerberos)
            return PSRemoteAuthMode.Kerberos;

        if (options.AuthMode == PSRemoteAuthMode.Certificate)
        {
            EnsureCertAvailable(options);
            return PSRemoteAuthMode.Certificate;
        }

        // Auto-detect: Kerberos if domain env is set, cert otherwise.
        var domain = _getDomainEnv();
        if (!string.IsNullOrWhiteSpace(domain))
        {
            _logger.LogDebug(
                "PSRemoteTransport auto-detect: USERDNSDOMAIN={Domain} — selecting Kerberos",
                domain);
            return PSRemoteAuthMode.Kerberos;
        }

        // Fallback to certificate auth.
        if (options.ClientCertificate is not null)
        {
            _logger.LogDebug(
                "PSRemoteTransport auto-detect: no domain context — falling back to certificate auth");
            return PSRemoteAuthMode.Certificate;
        }

        // Neither Kerberos nor cert — refuse rather than downgrade to HTTP.
        throw new InvalidOperationException(
            $"PSRemoteTransport: cannot connect to '{options.Hostname}' — " +
            "no Kerberos domain context (USERDNSDOMAIN not set) and no client certificate provided. " +
            "HTTP:5985 is not permitted. " +
            "Supply a ClientCertificate or run the relay in a domain-joined context.");
    }

    private static void EnsureCertAvailable(PSRemoteConnectionOptions options)
    {
        if (options.ClientCertificate is null)
            throw new InvalidOperationException(
                $"PSRemoteTransport: AuthMode is Certificate but no ClientCertificate was supplied " +
                $"for host '{options.Hostname}'.");
    }

    // -------------------------------------------------------------------------
    // WSManConnectionInfo builders (HTTPS:5986 only)
    // -------------------------------------------------------------------------

    private static WSManConnectionInfo BuildKerberosInfo(PSRemoteConnectionOptions options)
    {
        // Kerberos over HTTPS:5986.
        // No explicit PSCredential — the relay's ambient Kerberos identity (machine
        // account or keytab-injected TGT) is used.  KerberosUsername is ignored here;
        // Kerberos SPN resolution is handled by the WSMan layer.
        var connInfo = new WSManConnectionInfo(
            new Uri($"https://{options.Hostname}:5986/wsman"),
            "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
            credential: null);

        connInfo.AuthenticationMechanism = AuthenticationMechanism.Kerberos;

        // Skip cert validation — Phase V will introduce proper PKI.
        connInfo.SkipCACheck = true;
        connInfo.SkipCNCheck = true;

        return connInfo;
    }

    private static WSManConnectionInfo BuildCertificateInfo(PSRemoteConnectionOptions options)
    {
        // Certificate auth over HTTPS:5986.
        // WSManConnectionInfo.CertificateThumbprint is the standard way to specify
        // a client cert for certificate-based WinRM auth.
        var connInfo = new WSManConnectionInfo(
            new Uri($"https://{options.Hostname}:5986/wsman"),
            "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
            credential: null);

        connInfo.AuthenticationMechanism = AuthenticationMechanism.Certificate;
        connInfo.CertificateThumbprint   = options.ClientCertificate!.Thumbprint;

        // Skip CA/CN check — caller is responsible for cert trust in Phase V.
        connInfo.SkipCACheck = true;
        connInfo.SkipCNCheck = true;

        return connInfo;
    }
}
