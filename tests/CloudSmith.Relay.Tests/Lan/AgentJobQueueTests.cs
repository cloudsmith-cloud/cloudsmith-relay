// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Lan;
using CloudSmith.Relay.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudSmith.Relay.Tests.Lan;

/// <summary>
/// Tests for the SQLite-persisted <see cref="AgentJobQueue"/> (AB#4840) —
/// enqueue/dequeue lifecycle, duplicate detection, restart survival, and
/// stale-delivery redelivery.
/// </summary>
public sealed class AgentJobQueueTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"jobs-queue-test-{Guid.NewGuid():N}.db");

    private readonly List<AgentJobQueue> _queues = new();

    private AgentJobQueue NewQueue(TimeSpan? redeliveryGrace = null)
    {
        var q = new AgentJobQueue(_dbPath, NullLogger<AgentJobQueue>.Instance, redeliveryGrace);
        _queues.Add(q);
        return q;
    }

    public void Dispose()
    {
        foreach (var q in _queues) q.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static JobDispatch MakeDispatch(Guid? jobId = null) => new(
        jobId ?? Guid.NewGuid(),
        "cluster.validate-network",
        "{\"scriptName\":\"Validate-Network.ps1\"}",
        IdempotencyKey: "op-1",
        Traceparent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");

    // ------------------------------------------------------------------

    [Fact]
    public void Enqueue_ThenDequeue_ReturnsCanonicalFields()
    {
        var queue = NewQueue();
        var dispatch = MakeDispatch();

        var outcome = queue.Enqueue("agent-1", dispatch);
        Assert.Equal(EnqueueOutcome.Accepted, outcome);

        var jobs = queue.Dequeue("agent-1");
        var job = Assert.Single(jobs);
        Assert.Equal(dispatch.JobId, job.JobId);
        Assert.Equal("agent-1", job.AgentId);
        Assert.Equal(dispatch.JobType, job.JobType);
        Assert.Equal(dispatch.PayloadJson, job.PayloadJson);
        Assert.Equal(dispatch.IdempotencyKey, job.IdempotencyKey);
        Assert.Equal(dispatch.Traceparent, job.Traceparent);
    }

    [Fact]
    public void Dequeue_MarksDelivered_SecondPollReturnsNothing()
    {
        var queue = NewQueue();
        queue.Enqueue("agent-1", MakeDispatch());

        Assert.Single(queue.Dequeue("agent-1"));
        Assert.Empty(queue.Dequeue("agent-1"));
        Assert.Equal(0, queue.PendingCount("agent-1"));
    }

    [Fact]
    public void Enqueue_SameJobIdTwice_ReturnsDuplicate()
    {
        var queue = NewQueue();
        var dispatch = MakeDispatch();

        Assert.Equal(EnqueueOutcome.Accepted, queue.Enqueue("agent-1", dispatch));
        Assert.Equal(EnqueueOutcome.Duplicate, queue.Enqueue("agent-1", dispatch));
        Assert.Equal(1, queue.PendingCount("agent-1"));
    }

    [Fact]
    public void Enqueue_DuplicateAfterDelivery_StillReturnsDuplicate()
    {
        var queue = NewQueue();
        var dispatch = MakeDispatch();

        queue.Enqueue("agent-1", dispatch);
        queue.Dequeue("agent-1"); // delivered

        Assert.Equal(EnqueueOutcome.Duplicate, queue.Enqueue("agent-1", dispatch));
    }

    [Fact]
    public void Restart_PendingJobsSurvive_AndAreServedToAgent()
    {
        var dispatch = MakeDispatch();

        var first = NewQueue();
        first.Enqueue("agent-1", dispatch);
        first.Dispose(); // simulated relay crash/restart — job was acked but never polled

        var second = NewQueue();
        Assert.Equal(1, second.PendingCount("agent-1"));

        var jobs = second.Dequeue("agent-1");
        var job = Assert.Single(jobs);
        Assert.Equal(dispatch.JobId, job.JobId);
        Assert.Equal(dispatch.PayloadJson, job.PayloadJson);
    }

    [Fact]
    public void Restart_DeliveredJob_IsNotServedAgainWithinGrace()
    {
        var first = NewQueue();
        first.Enqueue("agent-1", MakeDispatch());
        first.Dequeue("agent-1"); // delivered
        first.Dispose();

        var second = NewQueue();
        Assert.Empty(second.Dequeue("agent-1")); // within redelivery grace — retained, not re-served
    }

    [Fact]
    public void Dequeue_StaleDeliveredJob_IsRedeliveredAfterGrace()
    {
        var queue = NewQueue(redeliveryGrace: TimeSpan.Zero);
        var dispatch = MakeDispatch();

        queue.Enqueue("agent-1", dispatch);
        Assert.Single(queue.Dequeue("agent-1"));

        // Grace of zero → the delivered-but-unresulted job is immediately eligible again.
        var redelivered = queue.Dequeue("agent-1");
        Assert.Equal(dispatch.JobId, Assert.Single(redelivered).JobId);
    }

    [Fact]
    public void CompleteJob_TerminatesRedelivery()
    {
        var queue = NewQueue(redeliveryGrace: TimeSpan.Zero);
        var dispatch = MakeDispatch();

        queue.Enqueue("agent-1", dispatch);
        queue.Dequeue("agent-1");

        Assert.True(queue.CompleteJob(dispatch.JobId, succeeded: true));
        Assert.Empty(queue.Dequeue("agent-1"));
    }

    [Fact]
    public void CompleteJob_UnknownJobId_ReturnsFalse()
    {
        var queue = NewQueue();
        Assert.False(queue.CompleteJob(Guid.NewGuid(), succeeded: true));
    }

    [Fact]
    public void DequeueForResume_ReturnsDeliveredJobs_IgnoringGrace()
    {
        var dispatch = MakeDispatch();

        var first = NewQueue();
        first.Enqueue("psremote", dispatch);
        first.Dequeue("psremote"); // delivered (execution started), then crash
        first.Dispose();

        var second = NewQueue();
        var resumed = second.DequeueForResume("psremote");
        Assert.Equal(dispatch.JobId, Assert.Single(resumed).JobId);
    }

    [Fact]
    public void Dequeue_IsScopedPerAgent()
    {
        var queue = NewQueue();
        queue.Enqueue("agent-1", MakeDispatch());
        queue.Enqueue("agent-2", MakeDispatch());

        Assert.Single(queue.Dequeue("agent-1"));
        Assert.Single(queue.Dequeue("agent-2"));
    }
}
