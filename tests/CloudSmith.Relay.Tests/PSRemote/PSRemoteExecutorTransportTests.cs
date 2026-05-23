// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Management.Automation.Runspaces;
using CloudSmith.Relay.Execution;
using CloudSmith.Relay.Models;
using Xunit;

namespace CloudSmith.Relay.Tests.PSRemote;

/// <summary>
/// AB#1686 — unit tests that assert <see cref="PSRemoteExecutor.BuildConnectionInfo"/>
/// produces a <see cref="WSManConnectionInfo"/> with the correct scheme, port,
/// authentication mechanism, and skip-cert flags for each transport selection.
///
/// Server 2025 NTLM hardening + PSWSMan's forked WSMan natives make
/// Negotiate-over-HTTP infeasible from a Linux container, so the MVP default
/// is HTTPS:5986 + Basic + skip-cert (the Ansible-on-Windows pattern).
/// </summary>
public sealed class PSRemoteExecutorTransportTests
{
    private static readonly PSRemoteCredential SampleCredential = new()
    {
        Username = "csadmin",
        Password = "pa55w0rd!",
    };

    [Fact]
    public void HttpsBasic_ForWorkgroupHost_BuildsHttps5986BasicSkipCert()
    {
        var info = PSRemoteExecutor.BuildConnectionInfo(
            hostId:     "10.0.0.5",
            state:      HostState.Workgroup,
            credential: SampleCredential,
            transport:  PSRemoteExecutor.TransportHttpsBasic);

        Assert.Equal("https", info.Scheme);
        Assert.Equal(5986, info.Port);
        Assert.Equal("/wsman", info.AppName);
        Assert.Equal(AuthenticationMechanism.Basic, info.AuthenticationMechanism);
        Assert.True(info.SkipCACheck, "SkipCACheck must be true for MVP self-signed certs");
        Assert.True(info.SkipCNCheck, "SkipCNCheck must be true for MVP self-signed certs");
        Assert.NotNull(info.Credential);
        Assert.Equal("csadmin", info.Credential!.UserName);
        Assert.Equal(60_000, info.OperationTimeout);
        Assert.Equal(30_000, info.OpenTimeout);
    }

    [Fact]
    public void HttpsBasic_ForUnknownHost_AlsoUsesHttps5986Basic()
    {
        var info = PSRemoteExecutor.BuildConnectionInfo(
            hostId:     "10.0.0.6",
            state:      HostState.Unknown,
            credential: SampleCredential,
            transport:  PSRemoteExecutor.TransportHttpsBasic);

        Assert.Equal("https", info.Scheme);
        Assert.Equal(5986, info.Port);
        Assert.Equal(AuthenticationMechanism.Basic, info.AuthenticationMechanism);
        Assert.True(info.SkipCACheck);
        Assert.True(info.SkipCNCheck);
    }

    [Fact]
    public void HttpNegotiate_ForWorkgroupHost_BuildsHttp5985Negotiate()
    {
        var info = PSRemoteExecutor.BuildConnectionInfo(
            hostId:     "10.0.0.5",
            state:      HostState.Workgroup,
            credential: SampleCredential,
            transport:  PSRemoteExecutor.TransportHttpNegotiate);

        Assert.Equal("http", info.Scheme);
        Assert.Equal(5985, info.Port);
        Assert.Equal(AuthenticationMechanism.Negotiate, info.AuthenticationMechanism);
        Assert.NotNull(info.Credential);
    }

    [Fact]
    public void DomainJoined_AlwaysUsesHttp5985Kerberos_RegardlessOfTransport()
    {
        // AB#1686 — the transport selector only affects non-domain-joined hosts.
        // Kerberos is unaffected by Server 2025 NTLM hardening, so domain-joined
        // hosts continue to use HTTP:5985 + Kerberos in both modes.
        foreach (var transport in new[] { PSRemoteExecutor.TransportHttpsBasic, PSRemoteExecutor.TransportHttpNegotiate })
        {
            var info = PSRemoteExecutor.BuildConnectionInfo(
                hostId:     "hv-host-01.corp.local",
                state:      HostState.DomainJoined,
                credential: SampleCredential,
                transport:  transport);

            Assert.Equal("http", info.Scheme);
            Assert.Equal(5985, info.Port);
            Assert.Equal(AuthenticationMechanism.Kerberos, info.AuthenticationMechanism);
            Assert.Null(info.Credential);
        }
    }
}
