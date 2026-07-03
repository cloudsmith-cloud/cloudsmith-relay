// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Connection;
using CloudSmith.Relay.Lan;
using CloudSmith.Relay.Messages;
using CloudSmith.Relay.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CloudSmith.Relay.Tests.Workers;

/// <summary>
/// Tests for <see cref="JobResultForwarder"/> (AB#4841) — job.result frames are
/// forwarded upstream when the WebSocket is connected, retained durably while it
/// is down, and replayed after reconnect.
/// </summary>
public sealed class JobResultForwarderTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"results-fwd-test-{Guid.NewGuid():N}.db");

    private readonly AgentJobQueue _queue;
    private readonly Mock<IRelayConnection> _connection = new();

    public JobResultForwarderTests()
        => _queue = new AgentJobQueue(_dbPath, NullLogger<AgentJobQueue>.Instance);

    public void Dispose()
    {
        _queue.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private JobResultForwarder NewForwarder() => new(
        _queue, _connection.Object, NullLogger<JobResultForwarder>.Instance);

    private JobResult QueueResult(bool succeeded = true)
    {
        var dispatch = new JobDispatch(Guid.NewGuid(), "cluster.validate-network", "{}");
        _queue.Enqueue("agent-1", dispatch);
        var result = new JobResult(dispatch.JobId, succeeded, succeeded ? 0 : -1,
            "out", succeeded ? null : "err", DateTimeOffset.UtcNow);
        _queue.CompleteJob(result);
        return result;
    }

    // ------------------------------------------------------------------

    [Fact]
    public async Task Forward_Connected_SendsJobResultFrameAndMarksForwarded()
    {
        var sent = new List<RelayMessage>();
        _connection.SetupGet(c => c.IsConnected).Returns(true);
        _connection
            .Setup(c => c.SendAsync(It.IsAny<RelayMessage>(), It.IsAny<CancellationToken>()))
            .Callback<RelayMessage, CancellationToken>((m, _) => sent.Add(m))
            .Returns(Task.CompletedTask);

        var expected = QueueResult();
        var forwarded = await NewForwarder().TryForwardPendingAsync(CancellationToken.None);

        Assert.Equal(1, forwarded);
        var frame = Assert.IsType<JobResult>(Assert.Single(sent));
        Assert.Equal(expected.JobId, frame.JobId);
        Assert.Equal(expected.Output, frame.Output);
        Assert.Empty(_queue.GetUnforwardedResults());
    }

    [Fact]
    public async Task Forward_Disconnected_KeepsResultQueued()
    {
        _connection.SetupGet(c => c.IsConnected).Returns(false);

        QueueResult();
        var forwarded = await NewForwarder().TryForwardPendingAsync(CancellationToken.None);

        Assert.Equal(0, forwarded);
        Assert.Single(_queue.GetUnforwardedResults());
        _connection.Verify(
            c => c.SendAsync(It.IsAny<RelayMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Forward_SendThrows_ResultRetainedForRetry()
    {
        _connection.SetupGet(c => c.IsConnected).Returns(true);
        _connection
            .Setup(c => c.SendAsync(It.IsAny<RelayMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Relay WebSocket is not open."));

        QueueResult();
        var forwarded = await NewForwarder().TryForwardPendingAsync(CancellationToken.None);

        Assert.Equal(0, forwarded);
        Assert.Single(_queue.GetUnforwardedResults());
    }

    [Fact]
    public async Task Forward_AfterReconnect_ReplaysQueuedResults()
    {
        // Phase 1: disconnected — results accumulate durably.
        _connection.SetupGet(c => c.IsConnected).Returns(false);
        var forwarder = NewForwarder();

        var r1 = QueueResult();
        var r2 = QueueResult(succeeded: false);
        Assert.Equal(0, await forwarder.TryForwardPendingAsync(CancellationToken.None));
        Assert.Equal(2, _queue.GetUnforwardedResults().Count);

        // Phase 2: reconnected — both are replayed in order.
        var sent = new List<JobResult>();
        _connection.SetupGet(c => c.IsConnected).Returns(true);
        _connection
            .Setup(c => c.SendAsync(It.IsAny<RelayMessage>(), It.IsAny<CancellationToken>()))
            .Callback<RelayMessage, CancellationToken>((m, _) => sent.Add((JobResult)m))
            .Returns(Task.CompletedTask);

        Assert.Equal(2, await forwarder.TryForwardPendingAsync(CancellationToken.None));
        Assert.Equal(new[] { r1.JobId, r2.JobId }, sent.Select(r => r.JobId).ToArray());
        Assert.Empty(_queue.GetUnforwardedResults());
    }
}
