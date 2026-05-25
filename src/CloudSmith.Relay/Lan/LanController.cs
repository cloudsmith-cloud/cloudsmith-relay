// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Connection;
using CloudSmith.Relay.Messages;
using CloudSmith.Relay.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Relay.Lan;

/// <summary>
/// Inbound LAN HTTP API for Agents on the same network as this Relay.
///
/// Routes (all under <c>/lan/v1/</c>):
///   POST /lan/v1/agents/enroll                   — first-boot enrollment
///   POST /lan/v1/agents/{agentId}/heartbeat       — keep-alive + last-seen update
///   POST /lan/v1/agents/{agentId}/inventory       — inventory push, forwarded to PaaS via WS
///   POST /lan/v1/agents/{agentId}/health          — health probe, forwarded to PaaS via WS
///
/// Auth model (MVP):
///   - enroll: validates <c>enrollmentToken</c> in body against <c>RELAY_AGENT_ENROLLMENT_TOKEN</c>
///   - heartbeat / inventory / health: validates <c>X-Agent-Secret</c> header against the
///     per-agent secret issued during enrollment
/// </summary>
[ApiController]
[Route("lan/v1/agents")]
public sealed class LanController : ControllerBase
{
    private readonly InMemoryAgentRegistry _registry;
    private readonly AgentJobQueue _jobQueue;
    private readonly IRelayConnection _connection;
    private readonly ILogger<LanController> _logger;

    public LanController(
        InMemoryAgentRegistry registry,
        AgentJobQueue jobQueue,
        IRelayConnection connection,
        ILogger<LanController> logger)
    {
        _registry = registry;
        _jobQueue = jobQueue;
        _connection = connection;
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
            var (agentId, secret) = await _registry.EnrollAsync(req, ct);
            _logger.LogInformation("Enrollment accepted: agentId={AgentId} host={Host}",
                agentId, req.HostInfo.ComputerName);
            return Ok(new AgentEnrollResponse { AgentId = agentId, AgentSecret = secret });
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
    // GET /lan/v1/agents/{agentId}/jobs
    // -------------------------------------------------------------------------

    [HttpGet("{agentId}/jobs")]
    public IActionResult GetJobs(string agentId)
    {
        if (!AuthenticateAgent(agentId, out var problem))
            return problem!;

        var jobs = _jobQueue.Dequeue(agentId);
        _logger.LogDebug("Job poll: agentId={AgentId} count={Count}", agentId, jobs.Count);
        return Ok(jobs.Select(j => new
        {
            jobId   = j.JobId,
            jobType = j.Kind,
            payload = new { scriptName = j.Kind, arguments = (Dictionary<string, string>?)null },
            traceparent = (string?)null,
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

        var result = new JobResult(
            jobId,
            req.Succeeded,
            req.Output,
            req.Error,
            DateTimeOffset.UtcNow);

        _jobQueue.CompleteJob(result);

        // Forward result to PaaS over WebSocket.
        var ack = new JobAck(jobId, req.Succeeded ? "succeeded" : "failed", req.Error);
        await ForwardAsync(ack, ct);

        _logger.LogInformation("Job result: agentId={AgentId} jobId={JobId} succeeded={Ok}",
            agentId, jobId, req.Succeeded);

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
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Validate the <c>X-Agent-Secret</c> header. Sets <paramref name="problem"/>
    /// to a 401 result if auth fails; returns true on success.
    /// </summary>
    private bool AuthenticateAgent(string agentId, out IActionResult? problem)
    {
        var secret = Request.Headers["X-Agent-Secret"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(secret) || !_registry.ValidateSecret(agentId, secret))
        {
            _logger.LogWarning("Unauthorized agent request: agentId={AgentId}", agentId);
            problem = Unauthorized(new { error = "Missing or invalid X-Agent-Secret header." });
            return false;
        }

        problem = null;
        return true;
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
