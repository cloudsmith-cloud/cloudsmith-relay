// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Connection;
using CloudSmith.Relay.Enrollment;
using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.State;
using CloudSmith.Relay.Stubs;
using CloudSmith.Relay.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

// ---------------------------------------------------------------------------
// Configuration — environment-driven (see RelayOptions for full doc).
// ---------------------------------------------------------------------------
var paasUrl = Environment.GetEnvironmentVariable("RELAY_PAAS_URL")
    ?? throw new InvalidOperationException("RELAY_PAAS_URL is required.");
var enrollmentToken = Environment.GetEnvironmentVariable("RELAY_ENROLLMENT_TOKEN");
var displayName = Environment.GetEnvironmentVariable("RELAY_DISPLAY_NAME")
    ?? $"relay-{Environment.MachineName}";
var listenPort = int.TryParse(
    Environment.GetEnvironmentVariable("RELAY_LISTEN_PORT"),
    out var port) ? port : 8443;
var identityDir = Environment.GetEnvironmentVariable("RELAY_IDENTITY_DIR")
    ?? RelayEnrollmentClient.DefaultIdentityDirectory;
var clusterId = Environment.GetEnvironmentVariable("RELAY_CLUSTER_ID") ?? "demo";

var relayOptions = new RelayOptions
{
    PaasUrl = paasUrl,
    EnrollmentToken = enrollmentToken,
    DisplayName = displayName,
    ListenPort = listenPort,
    IdentityDirectory = identityDir,
    ClusterId = clusterId,
};

// ---------------------------------------------------------------------------
// Logging — Serilog -> stdout.
// ---------------------------------------------------------------------------
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(Log.Logger, dispose: true);

    builder.Services.AddSingleton(Options.Create(relayOptions));

    // HttpClient for enrollment.
    builder.Services.AddSingleton(_ => new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30),
    });

    // Enrollment client.
    builder.Services.AddSingleton<IRelayEnrollmentClient>(sp =>
        new RelayEnrollmentClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<ILogger<RelayEnrollmentClient>>(),
            relayOptions.PaasUrl,
            relayOptions.IdentityDirectory));

    // Resolve RelayId from persisted identity (or "pending" — the hosted
    // service runs enrollment first and the WebSocket loop attempts its first
    // connect after that). For first-run we re-build options after enrollment;
    // for steady-state the identity is on disk before DI runs.
    builder.Services.AddSingleton(sp =>
    {
        var enroller = sp.GetRequiredService<IRelayEnrollmentClient>();
        string relayId = "pending";
        if (enroller is RelayEnrollmentClient impl)
        {
            var id = impl.TryLoadIdentity();
            if (id is not null) relayId = id.RelayId;
        }
        return Options.Create(new RelayConnectionOptions
        {
            PaasUrl = relayOptions.PaasUrl,
            RelayId = relayId,
            PrivateKeyPath = Path.Combine(relayOptions.IdentityDirectory, "relay.key"),
        });
    });
    builder.Services.AddSingleton<IRelayConnection, WebSocketRelayConnection>();

    // Host state tracking.
    builder.Services.AddSingleton<IHostStateTracker, HostStateTracker>();

    // Agent registry + PSRemote executor still stubbed — AB#1666-followup.
    builder.Services.AddSingleton<IAgentRegistry, StubAgentRegistry>();
    builder.Services.AddSingleton<IPSRemoteExecutor, StubPSRemoteExecutor>();

    // Background services.
    builder.Services.AddHostedService<RelayHostedService>();
    builder.Services.AddHostedService<InventoryScanWorker>();

    var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Log.Fatal(ex, "cloudsmith-relay terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// ---------------------------------------------------------------------------
// TODO: Add Agent enrollment HTTP listener on port RELAY_LISTEN_PORT — AB#1666-followup.
//
// The Relay must accept inbound mTLS connections from local-LAN Agents on
// the configured ListenPort, validate their one-time enrollment tokens,
// issue per-Agent certificates, and persist Agent records via IAgentRegistry.
// That listener will likely run as a Kestrel-hosted ASP.NET Core endpoint
// alongside the BackgroundServices above (WebApplication can co-exist with
// the Worker SDK via shared IHostBuilder configuration).
//
// Out of scope for tonight — tracked as AB#1666-followup.
// ---------------------------------------------------------------------------
