// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Connection;
using CloudSmith.Relay.Enrollment;
using CloudSmith.Relay.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudSmith.Relay.Workers;

/// <summary>
/// Top-level Relay lifecycle worker:
///   1. Ensure the Relay has an identity (enroll if first run).
///   2. Open the persistent WebSocket to PaaS.
///   3. Handle inbound JobDispatch by ACK'ing — actual dispatch wiring is AB#1666-followup.
///   4. Heartbeat every <see cref="RelayOptions.HeartbeatInterval"/>.
/// </summary>
public sealed class RelayHostedService : BackgroundService
{
    private readonly RelayOptions _opts;
    private readonly IRelayEnrollmentClient _enroller;
    private readonly IRelayConnection _connection;
    private readonly RelayConnectionOptions _connOpts;
    private readonly ILogger<RelayHostedService> _logger;

    public RelayHostedService(
        IOptions<RelayOptions> opts,
        IRelayEnrollmentClient enroller,
        IRelayConnection connection,
        IOptions<RelayConnectionOptions> connOpts,
        ILogger<RelayHostedService> logger)
    {
        _opts = opts.Value;
        _enroller = enroller;
        _connection = connection;
        _connOpts = connOpts.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var relayId = await EnsureEnrolledAsync(ct).ConfigureAwait(false);
        // Patch identity into the shared connection options so the WebSocket
        // loop builds the correct {relayId} URI (matters on first-run when DI
        // built the options before enrollment ran).
        _connOpts.RelayId = relayId;

        _connection.OnMessageReceived += OnMessageAsync;
        await _connection.OpenAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Relay {RelayId} online; heartbeat every {Interval}",
            relayId, _opts.HeartbeatInterval);

        using var hb = new PeriodicTimer(_opts.HeartbeatInterval);
        try
        {
            while (await hb.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_connection.IsConnected)
                {
                    _logger.LogDebug("Heartbeat skipped — WebSocket not yet connected");
                    continue;
                }
                try
                {
                    await _connection.SendAsync(new Heartbeat(DateTimeOffset.UtcNow), ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Heartbeat send failed");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
        finally
        {
            _connection.OnMessageReceived -= OnMessageAsync;
        }
    }

    private async Task<string> EnsureEnrolledAsync(CancellationToken ct)
    {
        // RelayEnrollmentClient exposes identity helpers — cast when available.
        if (_enroller is RelayEnrollmentClient impl && impl.HasPersistedIdentity())
        {
            var existing = impl.TryLoadIdentity();
            if (existing is not null)
            {
                _logger.LogInformation("Loaded persisted Relay identity: {RelayId}", existing.RelayId);
                return existing.RelayId;
            }
        }

        if (string.IsNullOrWhiteSpace(_opts.EnrollmentToken))
        {
            throw new InvalidOperationException(
                "No persisted Relay identity and RELAY_ENROLLMENT_TOKEN not set — cannot enroll.");
        }

        var result = await _enroller.EnrollAsync(_opts.EnrollmentToken, _opts.DisplayName, ct)
            .ConfigureAwait(false);
        return result.RelayId;
    }

    private async Task OnMessageAsync(object sender, RelayMessage msg)
    {
        switch (msg)
        {
            case JobDispatch job:
                _logger.LogInformation(
                    "Received job {JobId} action={Action} args={ArgCount}; " +
                    "would dispatch to Agent or PSRemote — AB#1666-followup",
                    job.JobId, job.Action, job.Args.Count);
                try
                {
                    await _connection.SendAsync(
                        new JobAck(job.JobId, "Accepted", "MVP stub — execution wiring pending"),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to ACK job {JobId}", job.JobId);
                }
                break;

            case Heartbeat hb:
                _logger.LogDebug("Inbound heartbeat {At}", hb.At);
                break;

            default:
                _logger.LogDebug("Inbound message: {Type}", msg.GetType().Name);
                break;
        }
    }
}
