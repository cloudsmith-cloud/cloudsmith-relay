// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using CloudSmith.Relay.Models;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Relay.Lan;

/// <summary>
/// Per-agent job queue. PaaS enqueues jobs via WebSocket dispatch;
/// Agents poll via GET /lan/v1/agents/{agentId}/jobs.
/// Results flow back via POST /lan/v1/agents/{agentId}/jobs/{jobId}/result.
/// </summary>
public sealed class AgentJobQueue
{
    private readonly ILogger<AgentJobQueue> _logger;

    // agentId → ordered queue of pending jobs
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Job>> _queues =
        new(StringComparer.OrdinalIgnoreCase);

    // jobId → TaskCompletionSource for result waiting (PaaS side)
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JobResult>> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    public AgentJobQueue(ILogger<AgentJobQueue> logger) => _logger = logger;

    /// <summary>
    /// Enqueue a job for the target agent. Called when PaaS dispatches a job via WebSocket.
    /// Returns a Task that completes when the agent reports a result (or times out).
    /// </summary>
    public Task<JobResult> EnqueueAsync(string agentId, Job job, TimeSpan timeout, CancellationToken ct)
    {
        var queue = _queues.GetOrAdd(agentId, _ => new ConcurrentQueue<Job>());
        queue.Enqueue(job);

        var tcs = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[job.JobId] = tcs;

        _logger.LogInformation("Job {JobId} enqueued for agent {AgentId}", job.JobId, agentId);

        // Auto-expire if agent never picks it up or reports a result.
        _ = Task.Delay(timeout, ct).ContinueWith(t =>
        {
            if (_pending.TryRemove(job.JobId, out var expiredTcs))
                expiredTcs.TrySetResult(new JobResult(job.JobId, false, null, "Timed out", DateTimeOffset.UtcNow));
        }, TaskScheduler.Default);

        return tcs.Task;
    }

    /// <summary>
    /// Dequeue all pending jobs for an agent. Called by Agent poll.
    /// </summary>
    public IReadOnlyList<Job> Dequeue(string agentId)
    {
        if (!_queues.TryGetValue(agentId, out var queue))
            return Array.Empty<Job>();

        var jobs = new List<Job>();
        while (queue.TryDequeue(out var job))
            jobs.Add(job);
        return jobs;
    }

    /// <summary>
    /// Complete the TCS for a job when the agent reports a result.
    /// </summary>
    public bool CompleteJob(JobResult result)
    {
        if (!_pending.TryRemove(result.JobId, out var tcs))
        {
            _logger.LogWarning("Job result received for unknown/expired jobId={JobId}", result.JobId);
            return false;
        }
        tcs.TrySetResult(result);
        return true;
    }
}
