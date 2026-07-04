// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Lan;
using CloudSmith.Relay.Messages;
using CloudSmith.Relay.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSmith.Relay.Tests.Lan;

/// <summary>
/// Tests for <see cref="SqliteAgentRegistry"/> — persistence, JWT issuance/validation,
/// heartbeat, and enrollment-token gating.
/// </summary>
public sealed class SqliteAgentRegistryTests : IDisposable
{
    private const string ValidToken = "test-site-token";

    private readonly string _dbPath;
    private readonly SqliteAgentRegistry _registry;
    private readonly RelayJwtService _jwt;

    public SqliteAgentRegistryTests()
    {
        // Use a temp file per test-class instance so tests don't share state.
        _dbPath = Path.Combine(Path.GetTempPath(), $"agents-test-{Guid.NewGuid():N}.db");

        // Ephemeral RSA key — fine for tests.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        _jwt = RelayJwtService.FromPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem());

        _registry = new SqliteAgentRegistry(
            ValidToken,
            _jwt,
            _dbPath,
            NullLogger<SqliteAgentRegistry>.Instance);
    }

    public void Dispose()
    {
        _registry.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

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

    // ------------------------------------------------------------------

    [Fact]
    public async Task EnrollAsync_ValidToken_ReturnsAgentIdAndJwt()
    {
        var (agentId, jwt) = await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);

        Assert.NotEmpty(agentId);
        Assert.NotEmpty(jwt);
        // JWT should be three base64url segments.
        Assert.Equal(3, jwt.Split('.').Length);
    }

    [Fact]
    public async Task EnrollAsync_InvalidToken_ThrowsUnauthorized()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _registry.EnrollAsync(MakeRequest(token: "wrong"), CancellationToken.None));
    }

    [Fact]
    public async Task ValidateToken_AfterEnroll_ReturnsTrue()
    {
        var (agentId, jwt) = await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);

        Assert.True(_registry.ValidateToken(agentId, jwt));
    }

    [Fact]
    public async Task ValidateToken_WrongAgentId_ReturnsFalse()
    {
        var (_, jwt) = await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);

        Assert.False(_registry.ValidateToken("different-agent-id", jwt));
    }

    [Fact]
    public async Task ValidateToken_TamperedToken_ReturnsFalse()
    {
        var (agentId, jwt) = await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);
        var parts = jwt.Split('.');
        // Corrupt the signature.
        parts[2] = "invalidsig";
        var tampered = string.Join('.', parts);

        Assert.False(_registry.ValidateToken(agentId, tampered));
    }

    [Fact]
    public async Task GetAgentForHostAsync_AfterEnroll_ReturnsAgent()
    {
        await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);

        var agent = await _registry.GetAgentForHostAsync("HYPER-V-HOST-01", CancellationToken.None);

        Assert.NotNull(agent);
        Assert.Equal("HYPER-V-HOST-01", agent!.Hostname);
    }

    [Fact]
    public async Task GetAgentForHostAsync_UnknownHost_ReturnsNull()
    {
        var agent = await _registry.GetAgentForHostAsync("NO-SUCH-HOST", CancellationToken.None);
        Assert.Null(agent);
    }

    [Fact]
    public async Task Heartbeat_KnownAgent_ReturnsTrue()
    {
        var (agentId, _) = await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);
        Assert.True(_registry.Heartbeat(agentId));
    }

    [Fact]
    public void Heartbeat_UnknownAgent_ReturnsFalse()
    {
        Assert.False(_registry.Heartbeat("no-such-agent"));
    }

    [Fact]
    public async Task ListAgents_AfterEnroll_ContainsAgent()
    {
        await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);
        var agents = _registry.ListAgents().ToList();

        Assert.Single(agents);
        Assert.Equal("HYPER-V-HOST-01", agents[0].Hostname);
    }

    [Fact]
    public async Task Registry_SurvivesReopenOfDatabase()
    {
        // Enroll on first registry instance.
        var (agentId, _) = await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);
        _registry.Dispose();

        // Open a second registry against the same DB file.
        using var registry2 = new SqliteAgentRegistry(
            ValidToken,
            _jwt,
            _dbPath,
            NullLogger<SqliteAgentRegistry>.Instance);

        var agent = await registry2.GetAgentForHostAsync("HYPER-V-HOST-01", CancellationToken.None);

        Assert.NotNull(agent);
        Assert.Equal(agentId, agent!.AgentId);
    }

    [Fact]
    public async Task EnrollAsync_SameHost_ReusesExistingAgentId()
    {
        var first = await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);
        var second = await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);

        Assert.Equal(first.AgentId, second.AgentId);
        Assert.True(_registry.ValidateToken(first.AgentId, second.AgentJwt));

        var agents = _registry.ListAgents().ToList();
        Assert.Single(agents);
        Assert.Equal(first.AgentId, agents[0].AgentId);
    }

    [Fact]
    public async Task EnrollAsync_SameHost_KeepsQueuedJobsAddressableAfterReEnroll()
    {
        var (agentId, _) = await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);
        using var queue = new AgentJobQueue(_dbPath, NullLogger<AgentJobQueue>.Instance);

        var dispatch = new JobDispatch(
            Guid.NewGuid(),
            "reference.echo",
            "{\"message\":\"hello\"}",
            IdempotencyKey: "ab4845",
            Traceparent: null);

        queue.Enqueue(agentId, dispatch);

        var reenrolled = await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);
        Assert.Equal(agentId, reenrolled.AgentId);

        var dequeued = queue.DequeueForResume(reenrolled.AgentId);
        Assert.Equal(dispatch.JobId, Assert.Single(dequeued).JobId);
    }

    [Fact]
    public async Task RestartRecovery_ReassignUndeliveredJob_FromStaleAgentId_ToActiveAgent()
    {
        using var staleRsa = System.Security.Cryptography.RSA.Create(2048);
        var staleJwt = RelayJwtService.FromPrivateKeyPem(staleRsa.ExportPkcs8PrivateKeyPem());

        string staleAgentId;
        using (var staleRegistry = new SqliteAgentRegistry(
                   ValidToken,
                   staleJwt,
                   _dbPath,
                   NullLogger<SqliteAgentRegistry>.Instance))
        {
            staleAgentId = (await staleRegistry.EnrollAsync(MakeRequest(), CancellationToken.None)).AgentId;
        }

        using var queue = new AgentJobQueue(_dbPath, NullLogger<AgentJobQueue>.Instance);
        var dispatch = new JobDispatch(
            Guid.NewGuid(),
            "reference.echo",
            "{\"message\":\"hello\"}");
        queue.Enqueue(staleAgentId, dispatch);

        var active = await _registry.EnrollAsync(MakeRequest(), CancellationToken.None);
        Assert.NotEqual(staleAgentId, active.AgentId);
        Assert.Single(_registry.ListAgents());

        var moved = queue.ReassignUndeliveredJobs(active.AgentId);
        Assert.Equal(1, moved);

        var recovered = queue.DequeueForResume(active.AgentId);
        Assert.Equal(dispatch.JobId, Assert.Single(recovered).JobId);
    }
}
