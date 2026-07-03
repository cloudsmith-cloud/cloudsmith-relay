// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Execution;
using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Lan;
using CloudSmith.Relay.Messages;
using CloudSmith.Relay.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CloudSmith.Relay.Tests.Execution;

/// <summary>
/// Tests for <see cref="JobDispatchHandler"/> — ack semantics (accepted / duplicate /
/// rejected), agent-path enqueue, and PSRemote-path routing (AB#2961).
/// </summary>
public sealed class JobDispatchHandlerTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"jobs-handler-test-{Guid.NewGuid():N}.db");

    private readonly AgentJobQueue _queue;
    private readonly Mock<IAgentRegistry> _registry = new();
    private readonly Mock<IPSRemoteExecutor> _psRemote = new();

    public JobDispatchHandlerTests()
        => _queue = new AgentJobQueue(_dbPath, NullLogger<AgentJobQueue>.Instance);

    public void Dispose()
    {
        _queue.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private JobDispatchHandler NewHandler() => new(
        _queue,
        _registry.Object,
        _psRemote.Object,
        NullLogger<JobDispatchHandler>.Instance);

    private static Agent MakeAgent(string agentId, DateTimeOffset lastSeen) => new(
        AgentId: agentId,
        HostId: "host-1",
        Hostname: "HYPER-V-HOST-01",
        EnrolledAtUtc: lastSeen.AddDays(-1),
        LastSeenUtc: lastSeen);

    private static JobDispatch MakeDispatch(
        Guid? jobId = null,
        string jobType = "cluster.validate-network",
        string payloadJson = "{}") =>
        new(jobId ?? Guid.NewGuid(), jobType, payloadJson);

    // ------------------------------------------------------------------

    [Fact]
    public async Task Handle_AgentJob_EnqueuesAndAcksAccepted()
    {
        _registry.Setup(r => r.ListAgents())
            .Returns(new[] { MakeAgent("agent-1", DateTimeOffset.UtcNow) });

        var dispatch = MakeDispatch(payloadJson: "{\"scriptName\":\"x.ps1\"}");
        var ack = await NewHandler().HandleAsync(dispatch, CancellationToken.None);

        Assert.Equal(JobDispatchHandler.AckAccepted, ack.AckStatus);
        Assert.Equal(dispatch.JobId, ack.JobId);

        var queued = _queue.Dequeue("agent-1");
        var job = Assert.Single(queued);
        Assert.Equal(dispatch.JobId, job.JobId);
        Assert.Equal(dispatch.JobType, job.JobType);
        Assert.Equal(dispatch.PayloadJson, job.PayloadJson);
    }

    [Fact]
    public async Task Handle_AgentJob_PicksMostRecentlySeenAgent()
    {
        var now = DateTimeOffset.UtcNow;
        _registry.Setup(r => r.ListAgents()).Returns(new[]
        {
            MakeAgent("agent-stale",  now.AddMinutes(-30)),
            MakeAgent("agent-fresh",  now),
        });

        await NewHandler().HandleAsync(MakeDispatch(), CancellationToken.None);

        Assert.Equal(1, _queue.PendingCount("agent-fresh"));
        Assert.Equal(0, _queue.PendingCount("agent-stale"));
    }

    [Fact]
    public async Task Handle_DuplicateJobId_AcksDuplicate()
    {
        _registry.Setup(r => r.ListAgents())
            .Returns(new[] { MakeAgent("agent-1", DateTimeOffset.UtcNow) });

        var handler = NewHandler();
        var dispatch = MakeDispatch();

        var first = await handler.HandleAsync(dispatch, CancellationToken.None);
        var second = await handler.HandleAsync(dispatch, CancellationToken.None);

        Assert.Equal(JobDispatchHandler.AckAccepted, first.AckStatus);
        Assert.Equal(JobDispatchHandler.AckDuplicate, second.AckStatus);
        Assert.Equal(1, _queue.PendingCount("agent-1"));
    }

    [Fact]
    public async Task Handle_NoEnrolledAgents_AcksRejected()
    {
        _registry.Setup(r => r.ListAgents()).Returns(Array.Empty<Agent>());

        var ack = await NewHandler().HandleAsync(MakeDispatch(), CancellationToken.None);

        Assert.Equal(JobDispatchHandler.AckRejected, ack.AckStatus);
        Assert.NotNull(ack.Detail);
    }

    [Fact]
    public async Task Handle_EmptyJobId_AcksRejected()
    {
        var ack = await NewHandler().HandleAsync(
            MakeDispatch(jobId: Guid.Empty), CancellationToken.None);

        Assert.Equal(JobDispatchHandler.AckRejected, ack.AckStatus);
    }

    [Fact]
    public async Task Handle_PsRemoteJob_ExecutesAndAcksAccepted()
    {
        var invoked = new TaskCompletionSource<(string HostId, string Script)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _psRemote
            .Setup(p => p.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, IDictionary<string, object>?, CancellationToken>(
                (hostId, script, _, _) => invoked.TrySetResult((hostId, script)))
            .ReturnsAsync(new PSResult(true, Array.Empty<object>(), null, TimeSpan.Zero));

        var dispatch = MakeDispatch(
            jobType: "psremote.invoke",
            payloadJson: "{\"hostId\":\"hv-01\",\"script\":\"Get-Date\"}");

        var ack = await NewHandler().HandleAsync(dispatch, CancellationToken.None);
        Assert.Equal(JobDispatchHandler.AckAccepted, ack.AckStatus);

        var (hostId, script) = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hv-01", hostId);
        Assert.Equal("Get-Date", script);
    }

    [Fact]
    public async Task Handle_PsRemoteJob_MalformedPayload_AcksRejected()
    {
        var ack = await NewHandler().HandleAsync(
            MakeDispatch(jobType: "psremote.invoke", payloadJson: "not-json"),
            CancellationToken.None);

        Assert.Equal(JobDispatchHandler.AckRejected, ack.AckStatus);
    }

    [Fact]
    public async Task Handle_PsRemoteJob_MissingHostId_AcksRejected()
    {
        var ack = await NewHandler().HandleAsync(
            MakeDispatch(jobType: "psremote.invoke", payloadJson: "{\"script\":\"Get-Date\"}"),
            CancellationToken.None);

        Assert.Equal(JobDispatchHandler.AckRejected, ack.AckStatus);
    }
}
