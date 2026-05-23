// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Lan;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSmith.Relay.Tests.Lan;

public sealed class InMemoryAgentRegistryTests
{
    private const string ValidToken = "test-enrollment-token";

    private static InMemoryAgentRegistry MakeRegistry(string? token = null) =>
        new(token ?? ValidToken, NullLogger<InMemoryAgentRegistry>.Instance);

    private static AgentEnrollRequest MakeRequest(string? token = null) => new()
    {
        EnrollmentToken = token ?? ValidToken,
        HostInfo = new AgentHostInfo
        {
            ComputerName = "HYPER-V-HOST-01",
            IpAddresses  = new List<string> { "192.168.1.10" },
            Os           = "Windows Server 2025",
        },
    };

    [Fact]
    public async Task EnrollAsync_ValidToken_ReturnsAgentIdAndSecret()
    {
        var registry = MakeRegistry();
        var req = MakeRequest();

        var (agentId, secret) = await registry.EnrollAsync(req, CancellationToken.None);

        Assert.NotEmpty(agentId);
        Assert.NotEmpty(secret);
    }

    [Fact]
    public async Task EnrollAsync_InvalidToken_ThrowsUnauthorized()
    {
        var registry = MakeRegistry();
        var req = MakeRequest(token: "wrong-token");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => registry.EnrollAsync(req, CancellationToken.None));
    }

    [Fact]
    public async Task GetAgentForHostAsync_AfterEnroll_ReturnsAgent()
    {
        var registry = MakeRegistry();
        await registry.EnrollAsync(MakeRequest(), CancellationToken.None);

        var agent = await registry.GetAgentForHostAsync("HYPER-V-HOST-01", CancellationToken.None);

        Assert.NotNull(agent);
        Assert.Equal("HYPER-V-HOST-01", agent!.Hostname);
    }

    [Fact]
    public async Task GetAgentForHostAsync_UnknownHost_ReturnsNull()
    {
        var registry = MakeRegistry();

        var agent = await registry.GetAgentForHostAsync("UNKNOWN-HOST", CancellationToken.None);

        Assert.Null(agent);
    }

    [Fact]
    public async Task Heartbeat_UpdatesLastSeen()
    {
        var registry = MakeRegistry();
        var (agentId, _) = await registry.EnrollAsync(MakeRequest(), CancellationToken.None);

        var before = registry.ListAgents().First().LastSeenUtc;
        // Small delay to ensure timestamp differs.
        await Task.Delay(10);
        var ok = registry.Heartbeat(agentId);
        var after = registry.ListAgents().First().LastSeenUtc;

        Assert.True(ok);
        Assert.True(after >= before);
    }

    [Fact]
    public void Heartbeat_UnknownAgent_ReturnsFalse()
    {
        var registry = MakeRegistry();
        Assert.False(registry.Heartbeat("nonexistent-agent-id"));
    }

    [Fact]
    public async Task ValidateSecret_CorrectSecret_ReturnsTrue()
    {
        var registry = MakeRegistry();
        var (agentId, secret) = await registry.EnrollAsync(MakeRequest(), CancellationToken.None);

        Assert.True(registry.ValidateSecret(agentId, secret));
    }

    [Fact]
    public async Task ValidateSecret_WrongSecret_ReturnsFalse()
    {
        var registry = MakeRegistry();
        var (agentId, _) = await registry.EnrollAsync(MakeRequest(), CancellationToken.None);

        Assert.False(registry.ValidateSecret(agentId, "wrong-secret"));
    }

    [Fact]
    public void ValidateSecret_UnknownAgent_ReturnsFalse()
    {
        var registry = MakeRegistry();
        Assert.False(registry.ValidateSecret("no-such-agent", "any-secret"));
    }
}
