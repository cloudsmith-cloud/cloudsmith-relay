// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Connection;
using CloudSmith.Relay.Messages;
using CloudSmith.Relay.Models;
using CloudSmith.Relay.Workers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CloudSmith.Relay.Lan;

/// <summary>
/// Inbound LAN HTTP API for Agents on the same network as this Relay.
///
/// Routes (all under <c>/lan/v1/</c>):
///   POST /lan/v1/agents/enroll                   — first-boot enrollment
///   POST /lan/v1/agents/{agentId}/heartbeat       — keep-alive + last-seen update
///   POST /lan/v1/agents/{agentId}/inventory       — inventory push, forwarded to PaaS via WS
///   POST /lan/v1/agents/{agentId}/health          — health probe, forwarded to PaaS via WS
///   POST /lan/v1/agents/{agentId}/hardware        — hardware snapshot, forwarded to PaaS via WS
///   GET  /lan/v1/agents/{agentId}/jobs            — job poll (dequeue pending jobs)
///   POST /lan/v1/agents/{agentId}/jobs/{jobId}/result — job completion report
///
/// Auth model (AB#2491):
///   - enroll: validates <c>enrollmentToken</c> in body against <c>RELAY_AGENT_ENROLLMENT_TOKEN</c>
///     (site-scoped gate); issues a per-agent JWT signed by the relay's RSA key in response.
///   - heartbeat / inventory / health / hardware / jobs: validates <c>X-Agent-Token</c> header
///     containing the per-agent JWT.  Falls back to <c>X-Agent-Secret</c> header for backward
///     compatibility with agents that have not yet upgraded.
/// </summary>
[ApiController]
[Route("lan/v1/agents")]
public sealed class LanController : ControllerBase
{
    private readonly SqliteAgentRegistry _registry;
    private readonly AgentJobQueue _jobQueue;
    private readonly IRelayConnection _connection;
    private readonly JobResultForwarder _forwarder;
    private readonly ILogger<LanController> _logger;
    private readonly ConcurrentDictionary<string, byte> _resumeServedAgents =
        new(StringComparer.OrdinalIgnoreCase);

    public LanController(
        SqliteAgentRegistry registry,
        AgentJobQueue jobQueue,
        IRelayConnection connection,
        JobResultForwarder forwarder,
        ILogger<LanController> logger)
    {
        _registry = registry;
        _jobQueue = jobQueue;
        _connection = connection;
        _forwarder = forwarder;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // POST /lan/v1/agents/enroll
    // -------------------------------------------------------------------------

    [HttpPost("enroll")]
    public async Task<IActionResult> EnrollAsync(
        [FromBody] AgentEnrollRequest req,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.EnrollmentToken))
            return BadRequest(new { error = "enrollmentToken is required." });

        try
        {
            var (agentId, agentJwt) = await _registry.EnrollAsync(req, ct);
            _logger.LogInformation("Enrollment accepted: agentId={AgentId} host={Host}",
                agentId, req.HostInfo.ComputerName);
            return Ok(new AgentEnrollResponse { AgentId = agentId, AgentSecret = agentJwt });
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new { error = "Invalid enrollment token." });
        }
    }

    // -------------------------------------------------------------------------
    // POST /lan/v1/agents/{agentId}/heartbeat
    // -------------------------------------------------------------------------

    [HttpPost("{agentId}/heartbeat")]
    public IActionResult Heartbeat(string agentId)
    {
        if (!AuthenticateAgent(agentId, out var problem))
            return problem!;

        var ok = _registry.Heartbeat(agentId);
        if (!ok)
        {
            _logger.LogWarning("Heartbeat for unknown agentId={AgentId}", agentId);
            return NotFound(new { error = "Agent not found." });
        }

        _logger.LogDebug("Heartbeat: agentId={AgentId}", agentId);
        return Ok(new { ack = true, serverUtc = DateTimeOffset.UtcNow });
    }

    // -------------------------------------------------------------------------
    // POST /lan/v1/agents/{agentId}/inventory
    // -------------------------------------------------------------------------

    [HttpPost("{agentId}/inventory")]
    public async Task<IActionResult> InventoryAsync(
        string agentId,
        [FromBody] AgentInventoryRequest req,
        CancellationToken ct)
    {
        if (!AuthenticateAgent(agentId, out var problem))
            return problem!;

        if (req is null)
            return BadRequest(new { error = "Request body is required." });

        _logger.LogInformation("Inventory from agentId={AgentId}: clusterId={Cluster} vms={Count}",
            agentId, req.ClusterId, req.Vms?.Count ?? 0);

        var push = new InventoryPush(
            req.ClusterId,
            req.Vms ?? new List<Messages.VmSnapshot>());

        await ForwardAsync(push, ct);
        return Ok(new { ack = true });
    }

    // -------------------------------------------------------------------------
    // POST /lan/v1/agents/{agentId}/health
    // -------------------------------------------------------------------------

    [HttpPost("{agentId}/health")]
    public async Task<IActionResult> HealthAsync(
        string agentId,
        [FromBody] AgentHealthRequest req,
        CancellationToken ct)
    {
        if (!AuthenticateAgent(agentId, out var problem))
            return problem!;

        if (req is null)
            return BadRequest(new { error = "Request body is required." });

        _logger.LogInformation("Health from agentId={AgentId}: clusterId={Cluster} status={Status}",
            agentId, req.ClusterId, req.Status);

        var push = new HealthProbePush(
            req.ClusterId,
            req.Status,
            req.Checks ?? new List<Messages.HealthCheck>());

        await ForwardAsync(push, ct);
        return Ok(new { ack = true });
    }

    // -------------------------------------------------------------------------
    // POST /lan/v1/agents/{agentId}/hardware
    // -------------------------------------------------------------------------

    [HttpPost("{agentId}/hardware")]
    public async Task<IActionResult> HardwareAsync(
        string agentId,
        [FromBody] AgentHardwareRequest req,
        CancellationToken ct)
    {
        if (!AuthenticateAgent(agentId, out var problem))
            return problem!;

        if (req is null) return BadRequest(new { error = "Request body is required." });

        _logger.LogInformation("Hardware from agentId={AgentId}: host={Host} cpu={Cpu} mem={Mem}GB",
            agentId, req.HostId, req.ProcessorCount, req.TotalMemoryBytes / (1024L * 1024 * 1024));

        // Forward hardware snapshot to PaaS over WebSocket.
        var push = new HardwarePush(req.HostId, req.ProcessorCount, req.LogicalCoreCount,
            req.ProcessorName, req.TotalMemoryBytes, DateTimeOffset.UtcNow);
        await ForwardAsync(push, ct);

        return Ok(new { ack = true });
    }

    // -------------------------------------------------------------------------
    // GET /lan/v1/agents/{agentId}/jobs
    // -------------------------------------------------------------------------

    [HttpGet("{agentId}/jobs")]
    public IActionResult GetJobs(string agentId)
    {
        if (!AuthenticateAgent(agentId, out var problem))
            return problem!;

        // On the first poll seen by this relay process, resume any unfinished
        // delivered jobs immediately. After a relay crash/restart there is no
        // in-process execution to preserve, so waiting for redelivery grace only
        // delays recovery of work that can be safely retried (at-least-once).
        var isFirstPollSinceStart = _resumeServedAgents.TryAdd(agentId, 0);
        var jobs = isFirstPollSinceStart
            ? _jobQueue.DequeueForResume(agentId)
            : _jobQueue.Dequeue(agentId);

        // Relay-restart recovery: if the first authenticated poll from the only
        // enrolled agent finds nothing, reclaim undelivered work that may still
        // be keyed to a stale agent id and serve it immediately.
        if (isFirstPollSinceStart && jobs.Count == 0)
        {
            var agents = _registry.ListAgents().ToList();
            if (agents.Count == 1 &&
                string.Equals(agents[0].AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            {
                var moved = _jobQueue.ReassignUndeliveredJobs(agentId);
                if (moved > 0)
                {
                    _logger.LogInformation(
                        "Recovered {Count} stranded queued job(s) for agent {AgentId} after relay restart",
                        moved,
                        agentId);
                    jobs = _jobQueue.DequeueForResume(agentId);
                }
            }
        }

        _logger.LogDebug(
            "Job poll: agentId={AgentId} count={Count} mode={Mode}",
            agentId,
            jobs.Count,
            isFirstPollSinceStart ? "resume" : "normal");
        // Canonical LAN dispatch item shape (contract AB#4839 §1.4) — same fields
        // as the job.dispatch frame; the Agent parses payloadJson itself.
        return Ok(jobs.Select(j => new
        {
            jobId          = j.JobId,
            jobType        = j.JobType,
            payloadJson    = j.PayloadJson,
            idempotencyKey = j.IdempotencyKey,
            traceparent    = j.Traceparent,
        }));
    }

    // -------------------------------------------------------------------------
    // POST /lan/v1/agents/{agentId}/jobs/{jobId}/result
    // -------------------------------------------------------------------------

    [HttpPost("{agentId}/jobs/{jobId}/result")]
    public async Task<IActionResult> JobResultAsync(
        string agentId,
        string jobId,
        [FromBody] AgentJobResultRequest req,
        CancellationToken ct)
    {
        if (!AuthenticateAgent(agentId, out var problem))
            return problem!;

        if (req is null) return BadRequest(new { error = "Request body is required." });

        if (!Guid.TryParse(jobId, out var jobGuid))
            return BadRequest(new { error = "jobId must be a GUID." });

        // Durably persist the result first (forwarded = 0) — it survives a relay
        // restart and a disconnected control WebSocket (AB#4841 / contract §6.2).
        var result = new JobResult(
            jobGuid,
            req.Succeeded,
            req.ExitCode,
            req.Output ?? string.Empty,
            req.Error,
            req.CompletedAt ?? DateTimeOffset.UtcNow);

        _jobQueue.CompleteJob(result);

        // Best-effort immediate forward; the background sweep replays queued
        // results after reconnect if the WebSocket is currently down.
        try
        {
            await _forwarder.TryForwardPendingAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Immediate job.result forward failed — background sweep will retry");
        }

        _logger.LogInformation("Job result: agentId={AgentId} jobId={JobId} succeeded={Ok}",
            agentId, jobId, req.Succeeded);

        return Ok(new { ack = true });
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Validate the per-agent JWT presented in <c>X-Agent-Token</c>.
    /// Also accepts the legacy <c>X-Agent-Secret</c> header for backward compatibility
    /// with agents that enrolled before AB#2491 and have not yet re-enrolled.
    ///
    /// Sets <paramref name="problem"/> to a 401 result if auth fails; returns true on success.
    /// </summary>
    private bool AuthenticateAgent(string agentId, out IActionResult? problem)
    {
        // Primary path: per-agent JWT (AB#2491).
        var token = Request.Headers["X-Agent-Token"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(token))
        {
            if (_registry.ValidateToken(agentId, token))
            {
                problem = null;
                return true;
            }
            _logger.LogWarning("Unauthorized agent request (invalid JWT): agentId={AgentId}", agentId);
            problem = Unauthorized(new { error = "Invalid or expired X-Agent-Token." });
            return false;
        }

        // Fallback: current agents still send the enrollment response value under
        // X-Agent-Secret. That value is now a JWT (property name kept for wire
        // compatibility), so validate it exactly like X-Agent-Token.
        var secret = Request.Headers["X-Agent-Secret"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(secret))
        {
            if (_registry.ValidateToken(agentId, secret))
            {
                _logger.LogDebug(
                    "Agent {AgentId} authenticated via legacy X-Agent-Secret header carrying JWT",
                    agentId);
                problem = null;
                return true;
            }

            _logger.LogWarning("Unauthorized agent request (invalid legacy JWT): agentId={AgentId}", agentId);
            problem = Unauthorized(new { error = "Invalid or expired X-Agent-Secret token." });
            return false;
        }

        _logger.LogWarning("Unauthorized agent request (no credential): agentId={AgentId}", agentId);
        problem = Unauthorized(new { error = "Missing X-Agent-Token header." });
        return false;
    }

    private async Task ForwardAsync(RelayMessage msg, CancellationToken ct)
    {
        if (!_connection.IsConnected)
        {
            _logger.LogWarning("Cannot forward {MsgType} — PaaS WebSocket not connected", msg.GetType().Name);
            return;
        }

        try
        {
            await _connection.SendAsync(msg, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to forward {MsgType} to PaaS", msg.GetType().Name);
        }
    }
}
