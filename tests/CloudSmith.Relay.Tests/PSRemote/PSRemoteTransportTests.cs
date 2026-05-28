// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CloudSmith.Relay.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSmith.Relay.Tests.PSRemote;

/// <summary>
/// AB#1666 — integration tests for <see cref="PSRemoteTransport"/>.
///
/// Tests verify the auth-negotiation rules without opening a real WinRM connection:
///   1. Kerberos path is selected when USERDNSDOMAIN is set.
///   2. Certificate path is selected as fallback when USERDNSDOMAIN is not set
///      and a client cert is provided.
///   3. HTTP:5985 is never used — all connections are on HTTPS:5986.
///   4. Missing Kerberos + missing cert → InvalidOperationException (not HTTP downgrade).
///   5. Explicit Kerberos mode ignores USERDNSDOMAIN.
///   6. Explicit Certificate mode without a cert → InvalidOperationException.
/// </summary>
public sealed class PSRemoteTransportTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Create a transport whose domain-env resolver returns <paramref name="domain"/>.
    /// Avoids mutating the real process environment in tests.
    /// </summary>
    private static PSRemoteTransport MakeTransport(string? domain)
        => new(NullLogger<PSRemoteTransport>.Instance, () => domain);

    /// <summary>
    /// Generate a throw-away self-signed cert whose thumbprint can be used in tests.
    /// The cert is not added to any store — we only need the thumbprint for the
    /// <see cref="WSManConnectionInfo.CertificateThumbprint"/> assertion.
    /// </summary>
    private static X509Certificate2 MakeSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=test-relay",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
    }

    // -------------------------------------------------------------------------
    // 1. Kerberos path — domain env set
    // -------------------------------------------------------------------------

    [Fact]
    public void Auto_DomainEnvSet_SelectsKerberos_OnHttps5986()
    {
        var transport = MakeTransport("corp.local");
        var opts = new PSRemoteConnectionOptions
        {
            Hostname = "hv-host-01.corp.local",
            AuthMode = PSRemoteAuthMode.Auto,
        };

        var (info, modeUsed) = transport.BuildConnectionInfo(opts);

        Assert.Equal(PSRemoteAuthMode.Kerberos, modeUsed);
        Assert.Equal("https", info.Scheme);
        Assert.Equal(5986, info.Port);
        Assert.Equal(AuthenticationMechanism.Kerberos, info.AuthenticationMechanism);
        Assert.Null(info.Credential);         // ambient identity — no explicit cred
        Assert.True(info.SkipCACheck);
        Assert.True(info.SkipCNCheck);
    }

    [Fact]
    public void ExplicitKerberos_NoDomainEnv_StillSelectsKerberos()
    {
        // Explicit mode overrides auto-detection — USERDNSDOMAIN not required.
        var transport = MakeTransport(null);
        var opts = new PSRemoteConnectionOptions
        {
            Hostname = "hv-host-02.corp.local",
            AuthMode = PSRemoteAuthMode.Kerberos,
        };

        var (info, modeUsed) = transport.BuildConnectionInfo(opts);

        Assert.Equal(PSRemoteAuthMode.Kerberos, modeUsed);
        Assert.Equal("https", info.Scheme);
        Assert.Equal(5986, info.Port);
        Assert.Equal(AuthenticationMechanism.Kerberos, info.AuthenticationMechanism);
    }

    // -------------------------------------------------------------------------
    // 2. Certificate fallback — no domain env, cert provided
    // -------------------------------------------------------------------------

    [Fact]
    public void Auto_NoDomainEnv_WithCert_SelectsCertificate_OnHttps5986()
    {
        var cert      = MakeSelfSignedCert();
        var transport = MakeTransport(null);
        var opts = new PSRemoteConnectionOptions
        {
            Hostname          = "10.0.0.5",
            AuthMode          = PSRemoteAuthMode.Auto,
            ClientCertificate = cert,
        };

        var (info, modeUsed) = transport.BuildConnectionInfo(opts);

        Assert.Equal(PSRemoteAuthMode.Certificate, modeUsed);
        Assert.Equal("https", info.Scheme);
        Assert.Equal(5986, info.Port);
        Assert.Equal(AuthenticationMechanism.Default, info.AuthenticationMechanism);
        Assert.Equal(cert.Thumbprint, info.CertificateThumbprint);
        Assert.True(info.SkipCACheck);
        Assert.True(info.SkipCNCheck);
    }

    [Fact]
    public void ExplicitCertificate_WithCert_SelectsCertificate()
    {
        var cert      = MakeSelfSignedCert();
        var transport = MakeTransport("corp.local"); // domain env set but mode is explicit cert
        var opts = new PSRemoteConnectionOptions
        {
            Hostname          = "10.0.0.6",
            AuthMode          = PSRemoteAuthMode.Certificate,
            ClientCertificate = cert,
        };

        var (info, modeUsed) = transport.BuildConnectionInfo(opts);

        Assert.Equal(PSRemoteAuthMode.Certificate, modeUsed);
        Assert.Equal(AuthenticationMechanism.Default, info.AuthenticationMechanism);
        Assert.Equal(cert.Thumbprint, info.CertificateThumbprint);
    }

    // -------------------------------------------------------------------------
    // 3. HTTP:5985 is never used
    // -------------------------------------------------------------------------

    [Fact]
    public void Kerberos_AlwaysUsesHttps5986_NeverHttp5985()
    {
        var transport = MakeTransport("corp.local");
        var opts = new PSRemoteConnectionOptions
        {
            Hostname = "hv-host-03.corp.local",
            AuthMode = PSRemoteAuthMode.Kerberos,
        };

        var (info, _) = transport.BuildConnectionInfo(opts);

        Assert.Equal("https", info.Scheme);
        Assert.NotEqual(5985, info.Port);
        Assert.Equal(5986, info.Port);
    }

    [Fact]
    public void Certificate_AlwaysUsesHttps5986_NeverHttp5985()
    {
        var cert      = MakeSelfSignedCert();
        var transport = MakeTransport(null);
        var opts = new PSRemoteConnectionOptions
        {
            Hostname          = "10.0.0.7",
            AuthMode          = PSRemoteAuthMode.Certificate,
            ClientCertificate = cert,
        };

        var (info, _) = transport.BuildConnectionInfo(opts);

        Assert.Equal("https", info.Scheme);
        Assert.Equal(5986, info.Port);
    }

    // -------------------------------------------------------------------------
    // 4. No Kerberos + no cert → exception, not HTTP downgrade
    // -------------------------------------------------------------------------

    [Fact]
    public void Auto_NoDomainEnv_NoCert_ThrowsInvalidOperation_NotHttp()
    {
        var transport = MakeTransport(null);
        var opts = new PSRemoteConnectionOptions
        {
            Hostname = "10.0.0.8",
            AuthMode = PSRemoteAuthMode.Auto,
            // No ClientCertificate
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => transport.BuildConnectionInfo(opts));

        // Message must explain the refusal and mention HTTP:5985 explicitly.
        Assert.Contains("HTTP:5985", ex.Message);
        Assert.Contains("10.0.0.8", ex.Message);
    }

    // -------------------------------------------------------------------------
    // 5. Explicit Certificate without cert → exception
    // -------------------------------------------------------------------------

    [Fact]
    public void ExplicitCertificate_NoCert_ThrowsInvalidOperation()
    {
        var transport = MakeTransport(null);
        var opts = new PSRemoteConnectionOptions
        {
            Hostname = "10.0.0.9",
            AuthMode = PSRemoteAuthMode.Certificate,
            // No ClientCertificate
        };

        Assert.Throws<InvalidOperationException>(
            () => transport.BuildConnectionInfo(opts));
    }

    // -------------------------------------------------------------------------
    // 6. Timeouts are forwarded correctly
    // -------------------------------------------------------------------------

    [Fact]
    public void Kerberos_TimeoutsForwardedToConnectionInfo()
    {
        var transport = MakeTransport("corp.local");
        var opts = new PSRemoteConnectionOptions
        {
            Hostname          = "hv-host-04.corp.local",
            AuthMode          = PSRemoteAuthMode.Kerberos,
            OperationTimeoutMs = 90_000,
            OpenTimeoutMs      = 45_000,
        };

        var (info, _) = transport.BuildConnectionInfo(opts);

        Assert.Equal(90_000, info.OperationTimeout);
        Assert.Equal(45_000, info.OpenTimeout);
    }
}
