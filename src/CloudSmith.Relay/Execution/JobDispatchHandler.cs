// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Lan;
using CloudSmith.Relay.Messages;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Relay.Execution;

/// <summary>
/// Routes inbound <c>job.dispatch</c> frames to an execution path (AB#2961):
///
///   - <c>psremote.*</c> jobTypes — the Relay itself is the executing target:
///     the payload is parsed here (in-contract — only the executing target parses
///     <c>payloadJson</c>) and run via <see cref="IPSRemoteExecutor"/>.
///   - every other jobType — enqueued for the most-recently-seen enrolled Agent,
///     which picks it up on its next LAN poll.
///
/// Ack semantics per the frozen contract (AB#4839 §1.2): <c>accepted</c> only
/// after the job is persisted to the local queue; <c>duplicate</c> for a jobId
/// already queued; <c>rejected</c> (with detail) when the job cannot be routed.
/// </summary>
public sealed class JobDispatchHandler
{
    /// <summary>Pseudo-agent id under which PSRemote-executed jobs are queued.</summary>
    public const string PsRemoteAgentId = "psremote";

    /// <summary>jobType prefix that selects the PSRemote execution path.</summary>
    public const string PsRemoteJobTypePrefix = "psremote.";

    public const string AckAccepted = "accepted";
    public const string AckRejected = "rejected";
    public const string AckDuplicate = "duplicate";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly AgentJobQueue _queue;
    private readonly IAgentRegistry _registry;
    private readonly IPSRemoteExecutor _psRemote;
    private readonly ILogger<JobDispatchHandler> _logger;

    public JobDispatchHandler(
        AgentJobQueue queue,
        IAgentRegistry registry,
        IPSRemoteExecutor psRemote,
        ILogger<JobDispatchHandler> logger)
    {
        _queue = queue;
        _registry = registry;
        _psRemote = psRemote;
        _logger = logger;
    }

    /// <summary>
    /// Handle a <c>job.dispatch</c> frame and return the <c>job.ack</c> to send upstream.
    /// Never throws for routine routing failures — those map to a <c>rejected</c> ack.
    /// </summary>
    public Task<JobAck> HandleAsync(JobDispatch dispatch, CancellationToken ct)
    {
        if (dispatch.JobId == Guid.Empty)
            return Task.FromResult(new JobAck(dispatch.JobId, AckRejected, "jobId must be a non-empty GUID"));

        if (string.IsNullOrWhiteSpace(dispatch.JobType))
            return Task.FromResult(new JobAck(dispatch.JobId, AckRejected, "jobType is required"));

        if (dispatch.PayloadJson is null)
            return Task.FromResult(new JobAck(dispatch.JobId, AckRejected, "payloadJson is required"));

        if (dispatch.JobType.StartsWith(PsRemoteJobTypePrefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(HandlePsRemote(dispatch));

        return Task.FromResult(HandleAgent(dispatch));
    }

    /// <summary>
    /// Resume PSRemote jobs that were interrupted by a relay restart (AB#4840 /
    /// contract §6.2). Agent-queued jobs need no resume — they are served from
    /// SQLite on the agent's next poll — but psremote jobs execute in-process,
    /// so an undelivered/unresulted one after restart must be re-run here
    /// (at-least-once; contract §4.4 requires re-execution-safe payloads).
    /// </summary>
    public void ResumePsRemoteJobs()
    {
        var interrupted = _queue.DequeueForResume(PsRemoteAgentId);
        if (interrupted.Count == 0) return;

        _logger.LogInformation("Resuming {Count} interrupted psremote job(s) after restart", interrupted.Count);
        foreach (var job in interrupted)
        {
            var dispatch = new JobDispatch(job.JobId, job.JobType, job.PayloadJson,
                job.IdempotencyKey, job.Traceparent);

            PsRemotePayload? payload = null;
            try
            {
                payload = JsonSerializer.Deserialize<PsRemotePayload>(job.PayloadJson, JsonOpts);
            }
            catch (JsonException) { /* handled below */ }

            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.HostId) ||
                string.IsNullOrWhiteSpace(payload.Script))
            {
                _logger.LogWarning("Cannot resume psremote job {JobId} — payload no longer parseable", job.JobId);
                _queue.CompleteJob(job.JobId, succeeded: false);
                continue;
            }

            _ = Task.Run(() => ExecutePsRemoteAsync(dispatch, payload), CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------
    // Agent path
    // ------------------------------------------------------------------

    private JobAck HandleAgent(JobDispatch dispatch)
    {
        // Target selection: the (site_id, env) routing decision already happened
        // API-side when this Relay was chosen. Within the site, pick the
        // most-recently-seen enrolled Agent (same tie-break spirit as the
        // relay-selection rule in the contract §5).
        var target = _registry.ListAgents()
            .OrderByDescending(a => a.LastSeenUtc)
            .FirstOrDefault();

        if (target is null)
        {
            _logger.LogWarning("Rejecting job {JobId} — no enrolled agent available", dispatch.JobId);
            return new JobAck(dispatch.JobId, AckRejected, "no enrolled agent available");
        }

        var outcome = _queue.Enqueue(target.AgentId, dispatch);
        return outcome == EnqueueOutcome.Duplicate
            ? new JobAck(dispatch.JobId, AckDuplicate)
            : new JobAck(dispatch.JobId, AckAccepted);
    }

    // ------------------------------------------------------------------
    // PSRemote path
    // ------------------------------------------------------------------

    /// <summary>Payload contract for <c>psremote.*</c> jobTypes (owned by the Relay).</summary>
    private sealed record PsRemotePayload(
        string? HostId,
        string? Script,
        Dictionary<string, object>? Arguments);

    private JobAck HandlePsRemote(JobDispatch dispatch)
    {
        PsRemotePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PsRemotePayload>(dispatch.PayloadJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Rejecting job {JobId} — malformed psremote payload", dispatch.JobId);
            return new JobAck(dispatch.JobId, AckRejected, "payloadJson is not valid JSON for a psremote job");
        }

        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.HostId) ||
            string.IsNullOrWhiteSpace(payload.Script))
        {
            return new JobAck(dispatch.JobId, AckRejected,
                "psremote payload requires hostId and script fields");
        }

        var outcome = _queue.Enqueue(PsRemoteAgentId, dispatch);
        if (outcome == EnqueueOutcome.Duplicate)
            return new JobAck(dispatch.JobId, AckDuplicate);

        // Execute in the background — the ack must not wait on script completion.
        _ = Task.Run(() => ExecutePsRemoteAsync(dispatch, payload), CancellationToken.None);

        return new JobAck(dispatch.JobId, AckAccepted);
    }

    private async Task ExecutePsRemoteAsync(JobDispatch dispatch, PsRemotePayload payload)
    {
        try
        {
            var result = await _psRemote.InvokeAsync(
                payload.HostId!, payload.Script!, payload.Arguments, CancellationToken.None)
                .ConfigureAwait(false);

            _logger.LogInformation("PSRemote job {JobId} on {HostId} finished success={Success}",
                dispatch.JobId, payload.HostId, result.Success);
            _queue.CompleteJob(dispatch.JobId, result.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PSRemote job {JobId} on {HostId} threw", dispatch.JobId, payload.HostId);
            _queue.CompleteJob(dispatch.JobId, succeeded: false);
        }
    }
}
