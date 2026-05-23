// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Connection;
using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudSmith.Relay.Workers;

/// <summary>
/// Periodic inventory scan worker (AB#1680 / AB#1665).
///
/// Scan path (Agent-first per ADR-007 2026-05-23 update):
///   1. Read <c>RELAY_HYPER_V_HOSTS</c> (comma-separated hostnames).
///   2. For each host:
///      a. Check <see cref="IAgentRegistry"/> — if an Agent is enrolled for this
///         host, SKIP the PSRemote scan (the Agent will push inventory directly
///         to the LAN listener).
///      b. If no Agent enrolled, fall back to <see cref="IPSRemoteExecutor.GetInventoryAsync"/>
///         via WinRM (the pre-Agent fallback path per ADR-007).
///   3. Aggregate all PSRemote-scanned VM snapshots into a single
///      <see cref="InventoryPush"/> and send over the relay WebSocket to PaaS.
///
/// When <c>RELAY_HYPER_V_HOSTS</c> is empty, the worker emits an empty push
/// (liveness behaviour) so the Relay appears alive to PaaS.
/// </summary>
public sealed class InventoryScanWorker : BackgroundService
{
    private readonly RelayOptions _opts;
    private readonly IRelayConnection _connection;
    private readonly IPSRemoteExecutor _psRemote;
    private readonly IAgentRegistry _agentRegistry;
    private readonly ILogger<InventoryScanWorker> _logger;

    public InventoryScanWorker(
        IOptions<RelayOptions> opts,
        IRelayConnection connection,
        IPSRemoteExecutor psRemote,
        IAgentRegistry agentRegistry,
        ILogger<InventoryScanWorker> logger)
    {
        _opts          = opts.Value;
        _connection    = connection;
        _psRemote      = psRemote;
        _agentRegistry = agentRegistry;
        _logger        = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "InventoryScanWorker every {Interval} for cluster {ClusterId}; hosts={Hosts}",
            _opts.InventoryScanInterval, _opts.ClusterId,
            _opts.HyperVHosts.Count > 0 ? string.Join(",", _opts.HyperVHosts) : "(none configured)");

        // Stagger first scan so the WebSocket has a chance to open.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_opts.InventoryScanInterval);
        try
        {
            do
            {
                await ScanOnceAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }

    internal async Task ScanOnceAsync(CancellationToken ct)
    {
        if (!_connection.IsConnected)
        {
            _logger.LogDebug("Skipping inventory push — WebSocket not connected yet");
            return;
        }

        var allVms = new List<VmSnapshot>();

        if (_opts.HyperVHosts.Count == 0)
        {
            _logger.LogInformation(
                "Inventory scan: no RELAY_HYPER_V_HOSTS configured — pushing empty snapshot (liveness)");
        }
        else
        {
            foreach (var host in _opts.HyperVHosts)
            {
                // Agent-first: if an Agent is already enrolled for this host, it pushes
                // inventory directly to the LAN listener — no PSRemote scan needed here.
                var agent = await _agentRegistry.GetAgentForHostAsync(host, ct).ConfigureAwait(false);
                if (agent is not null)
                {
                    _logger.LogDebug(
                        "Host {Host} has enrolled Agent {AgentId} — skipping PSRemote scan (Agent handles push)",
                        host, agent.AgentId);
                    continue;
                }

                // Fallback: PSRemote scan for hosts without an Agent.
                try
                {
                    var vms = await _psRemote.GetInventoryAsync(host, ct).ConfigureAwait(false);
                    allVms.AddRange(vms);
                    _logger.LogInformation("PSRemote scan {Host}: {Count} VM(s)", host, vms.Count);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PSRemote inventory scan failed for host {Host} — skipping", host);
                }
            }
        }

        // Only push if we have PSRemote-sourced VMs (Agent-sourced ones are pushed live).
        // Still push empty to maintain liveness heartbeat behaviour when all hosts have agents.
        try
        {
            var push = new InventoryPush(_opts.ClusterId, allVms);
            await _connection.SendAsync(push, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Inventory push sent (PSRemote): cluster={ClusterId} vms={Count}",
                _opts.ClusterId, allVms.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inventory push failed");
        }
    }
}
