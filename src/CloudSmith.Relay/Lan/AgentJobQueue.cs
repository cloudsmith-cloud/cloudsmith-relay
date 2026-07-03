// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Messages;
using CloudSmith.Relay.Models;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Relay.Lan;

/// <summary>Outcome of persisting a dispatched job to the local queue.</summary>
public enum EnqueueOutcome
{
    /// <summary>The job was newly persisted to the queue.</summary>
    Accepted,

    /// <summary>The jobId is already known to the queue — safe re-dispatch (contract §4.2).</summary>
    Duplicate,
}

/// <summary>
/// Per-agent job queue. PaaS dispatches jobs via the control WebSocket
/// (<c>job.dispatch</c>); Agents poll via GET /lan/v1/agents/{agentId}/jobs.
/// Results flow back via POST /lan/v1/agents/{agentId}/jobs/{jobId}/result.
///
/// Field shapes are canonical per the frozen job dispatch contract (AB#4839).
/// The queue never fabricates results — timeout adjudication belongs to the API.
/// </summary>
public sealed class AgentJobQueue
{
    private readonly ILogger<AgentJobQueue> _logger;
    private readonly object _gate = new();

    // jobId → job record. Retained after dequeue so duplicate dispatches are detected.
    private readonly Dictionary<Guid, QueuedJob> _jobs = new();

    // agentId → ordered pending (undelivered) jobIds.
    private readonly Dictionary<string, List<Guid>> _pendingByAgent =
        new(StringComparer.OrdinalIgnoreCase);

    public AgentJobQueue(ILogger<AgentJobQueue> logger) => _logger = logger;

    /// <summary>
    /// Persist a dispatched job for the target agent. Idempotent on jobId:
    /// a re-dispatch of a known job returns <see cref="EnqueueOutcome.Duplicate"/>.
    /// </summary>
    public EnqueueOutcome Enqueue(string agentId, JobDispatch dispatch)
    {
        lock (_gate)
        {
            if (_jobs.ContainsKey(dispatch.JobId))
            {
                _logger.LogInformation("Duplicate dispatch for job {JobId} — already queued", dispatch.JobId);
                return EnqueueOutcome.Duplicate;
            }

            var job = new QueuedJob(
                dispatch.JobId,
                agentId,
                dispatch.JobType,
                dispatch.PayloadJson,
                dispatch.IdempotencyKey,
                dispatch.Traceparent);

            _jobs[job.JobId] = job;
            if (!_pendingByAgent.TryGetValue(agentId, out var pending))
                _pendingByAgent[agentId] = pending = new List<Guid>();
            pending.Add(job.JobId);

            _logger.LogInformation("Job {JobId} ({JobType}) enqueued for agent {AgentId}",
                job.JobId, job.JobType, agentId);
            return EnqueueOutcome.Accepted;
        }
    }

    /// <summary>
    /// Dequeue all pending jobs for an agent and mark them delivered. Called by Agent poll.
    /// </summary>
    public IReadOnlyList<QueuedJob> Dequeue(string agentId)
    {
        lock (_gate)
        {
            if (!_pendingByAgent.TryGetValue(agentId, out var pending) || pending.Count == 0)
                return Array.Empty<QueuedJob>();

            var jobs = pending.Select(id => _jobs[id]).ToList();
            pending.Clear();
            return jobs;
        }
    }

    /// <summary>
    /// Mark a job completed when the agent (or PSRemote path) reports a result.
    /// Returns false if the jobId is unknown.
    /// </summary>
    public bool CompleteJob(Guid jobId, bool succeeded)
    {
        lock (_gate)
        {
            if (!_jobs.ContainsKey(jobId))
            {
                _logger.LogWarning("Job result received for unknown jobId={JobId}", jobId);
                return false;
            }
            _logger.LogInformation("Job {JobId} completed succeeded={Succeeded}", jobId, succeeded);
            return true;
        }
    }

    /// <summary>Number of pending (undelivered) jobs for an agent — diagnostics/tests.</summary>
    public int PendingCount(string agentId)
    {
        lock (_gate)
        {
            return _pendingByAgent.TryGetValue(agentId, out var pending) ? pending.Count : 0;
        }
    }
}
