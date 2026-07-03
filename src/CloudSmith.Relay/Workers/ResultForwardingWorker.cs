// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Connection;
using CloudSmith.Relay.Lan;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Relay.Workers;

/// <summary>
/// Forwards durably-queued job results upstream as <c>job.result</c> frames
/// (AB#4841). Results are persisted by <see cref="AgentJobQueue.CompleteJob"/>
/// with <c>forwarded = 0</c> and marked forwarded only after the WebSocket send
/// succeeds — at-least-once delivery; the API side is idempotent on jobId, so
/// replays after a crash between send and mark are safe (contract §4.3).
/// </summary>
public sealed class JobResultForwarder
{
    private readonly AgentJobQueue _queue;
    private readonly IRelayConnection _connection;
    private readonly ILogger<JobResultForwarder> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JobResultForwarder(
        AgentJobQueue queue,
        IRelayConnection connection,
        ILogger<JobResultForwarder> logger)
    {
        _queue = queue;
        _connection = connection;
        _logger = logger;
    }

    /// <summary>
    /// Send every unforwarded result upstream, oldest first. No-op while the
    /// WebSocket is down — results stay queued in SQLite and are replayed on
    /// the next pass after reconnect. Returns the number forwarded.
    /// </summary>
    public async Task<int> TryForwardPendingAsync(CancellationToken ct)
    {
        if (!_connection.IsConnected) return 0;

        // Single-flight: a pass already in progress covers the newly queued result.
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return 0;
        try
        {
            var forwarded = 0;
            foreach (var result in _queue.GetUnforwardedResults())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _connection.SendAsync(result, ct).ConfigureAwait(false);
                    _queue.MarkResultForwarded(result.JobId);
                    forwarded++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Connection dropped mid-pass — remaining results are retried
                    // on the next pass. Nothing is lost; nothing is marked forwarded.
                    _logger.LogWarning(ex,
                        "job.result forward failed for job {JobId} — will retry on reconnect",
                        result.JobId);
                    break;
                }
            }

            if (forwarded > 0)
                _logger.LogInformation("Forwarded {Count} job.result frame(s) to PaaS", forwarded);
            return forwarded;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Background sweep that drains the durable result queue. Covers the
/// disconnected-WS case: results queued while the WebSocket was down are
/// forwarded within one period of reconnect (AB#4841 / contract §6.2).
/// </summary>
public sealed class ResultForwardingWorker : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    private readonly JobResultForwarder _forwarder;
    private readonly ILogger<ResultForwardingWorker> _logger;

    public ResultForwardingWorker(JobResultForwarder forwarder, ILogger<ResultForwardingWorker> logger)
    {
        _forwarder = forwarder;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _forwarder.TryForwardPendingAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Result forwarding sweep failed — retrying next period");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }
}
