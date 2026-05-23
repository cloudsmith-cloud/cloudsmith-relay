// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Connection;
using CloudSmith.Relay.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudSmith.Relay.Workers;

/// <summary>
/// Periodic inventory scan worker. For MVP it produces an empty VM list — actual
/// host scanning lands once Agents are enrolling (AB#1666-followup).
/// </summary>
public sealed class InventoryScanWorker : BackgroundService
{
    private readonly RelayOptions _opts;
    private readonly IRelayConnection _connection;
    private readonly ILogger<InventoryScanWorker> _logger;

    public InventoryScanWorker(
        IOptions<RelayOptions> opts,
        IRelayConnection connection,
        ILogger<InventoryScanWorker> logger)
    {
        _opts = opts.Value;
        _connection = connection;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("InventoryScanWorker every {Interval} for cluster {ClusterId}",
            _opts.InventoryScanInterval, _opts.ClusterId);

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

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        // MVP: no Agent / PSRemote scanning yet — emit an empty inventory push so
        // PaaS sees the Relay is alive and reporting (AB#1666-followup will plug in
        // the actual scan code).
        _logger.LogInformation("Inventory scan: no agents enrolled yet — pushing empty snapshot");

        if (!_connection.IsConnected)
        {
            _logger.LogDebug("Skipping inventory push — WebSocket not connected yet");
            return;
        }

        try
        {
            var push = new InventoryPush(_opts.ClusterId, Array.Empty<VmSnapshot>());
            await _connection.SendAsync(push, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Inventory push failed");
        }
    }
}
