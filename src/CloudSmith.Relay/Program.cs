// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Connection;
using CloudSmith.Relay.Enrollment;
using CloudSmith.Relay.Execution;
using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Lan;
using CloudSmith.Relay.State;
using CloudSmith.Relay.Stubs;
using CloudSmith.Relay.Workers;
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
var lanListenPort = int.TryParse(
    Environment.GetEnvironmentVariable("RELAY_LISTEN_PORT"),
    out var port) ? port : 8080;
var identityDir = Environment.GetEnvironmentVariable("RELAY_IDENTITY_DIR")
    ?? RelayEnrollmentClient.DefaultIdentityDirectory;
var clusterId = Environment.GetEnvironmentVariable("RELAY_CLUSTER_ID") ?? "demo";

// RELAY_AGENT_ENROLLMENT_TOKEN — shared secret Agents must present during enroll.
// Defaults to a random value so the Relay starts safely even if not set; Agents
// must match whatever is configured here.
var agentEnrollmentToken = Environment.GetEnvironmentVariable("RELAY_AGENT_ENROLLMENT_TOKEN")
    ?? Guid.NewGuid().ToString("N");

// RELAY_HYPER_V_HOSTS — comma-separated Hyper-V hostnames/IPs to scan directly
// via WinRM when no Agent is enrolled for that host.
var hyperVHostsRaw = Environment.GetEnvironmentVariable("RELAY_HYPER_V_HOSTS") ?? string.Empty;
var hyperVHosts = hyperVHostsRaw
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToArray();

// RELAY_PSREMOTE_USERNAME / RELAY_PSREMOTE_PASSWORD — local admin WinRM credential.
var psRemoteUsername = Environment.GetEnvironmentVariable("RELAY_PSREMOTE_USERNAME") ?? string.Empty;
var psRemotePassword = Environment.GetEnvironmentVariable("RELAY_PSREMOTE_PASSWORD") ?? string.Empty;

var relayOptions = new RelayOptions
{
    PaasUrl           = paasUrl,
    EnrollmentToken   = enrollmentToken,
    DisplayName       = displayName,
    ListenPort        = lanListenPort,
    IdentityDirectory = identityDir,
    ClusterId         = clusterId,
    HyperVHosts       = hyperVHosts,
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
    // ---------------------------------------------------------------------------
    // WebApplication builder — replaces Host.CreateApplicationBuilder so that
    // the LAN listener (ASP.NET Core controllers) co-exists with the background
    // services (outbound PaaS WebSocket, PSRemote scanner).
    // ---------------------------------------------------------------------------
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(Log.Logger, dispose: true);

    // Kestrel LAN listener — HTTP on port RELAY_LISTEN_PORT (default 8080).
    // HTTPS (Phase V) will require cert provisioning; for MVP intra-LAN HTTP is acceptable.
    builder.WebHost.UseKestrel(k =>
    {
        k.ListenAnyIP(lanListenPort);
    });

    builder.Services.AddControllers();

    // ---------------------------------------------------------------------------
    // Services shared with background workers.
    // ---------------------------------------------------------------------------
    builder.Services.AddSingleton(Options.Create(relayOptions));

    builder.Services.AddSingleton(_ => new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30),
    });

    builder.Services.AddSingleton<IRelayEnrollmentClient>(sp =>
        new RelayEnrollmentClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<ILogger<RelayEnrollmentClient>>(),
            relayOptions.PaasUrl,
            relayOptions.IdentityDirectory));

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
            PaasUrl        = relayOptions.PaasUrl,
            RelayId        = relayId,
            PrivateKeyPath = Path.Combine(relayOptions.IdentityDirectory, "relay.key"),
        });
    });
    builder.Services.AddSingleton<IRelayConnection, WebSocketRelayConnection>();
    builder.Services.AddSingleton<IHostStateTracker, HostStateTracker>();

    // ---------------------------------------------------------------------------
    // Agent registry — real in-memory implementation backed by enrollment token.
    // Registered as both the concrete type (for LanController injection) and the
    // IAgentRegistry interface (for InventoryScanWorker lookup).
    // ---------------------------------------------------------------------------
    builder.Services.AddSingleton(sp =>
        new InMemoryAgentRegistry(
            agentEnrollmentToken,
            sp.GetRequiredService<ILogger<InMemoryAgentRegistry>>()));

    builder.Services.AddSingleton<IAgentRegistry>(sp =>
        sp.GetRequiredService<InMemoryAgentRegistry>());

    // Job queue — routes jobs from PaaS (WebSocket) to Agents (LAN poll).
    builder.Services.AddSingleton<AgentJobQueue>();

    // ---------------------------------------------------------------------------
    // PSRemote executor — real when RELAY_HYPER_V_HOSTS is set, stub otherwise.
    // PSRemoteTransport (AB#1666) is always registered — it is the auth/transport
    // abstraction that PSRemoteExecutor delegates to for Kerberos and cert paths.
    // ---------------------------------------------------------------------------
    builder.Services.AddSingleton<PSRemoteTransport>();

    if (hyperVHosts.Length > 0)
    {
        builder.Services.AddSingleton(new PSRemoteCredential
        {
            Username = psRemoteUsername,
            Password = psRemotePassword,
        });
        builder.Services.AddSingleton<IPSRemoteExecutor, PSRemoteExecutor>();
    }
    else
    {
        builder.Services.AddSingleton<IPSRemoteExecutor, StubPSRemoteExecutor>();
    }

    // ---------------------------------------------------------------------------
    // Background services.
    // ---------------------------------------------------------------------------
    builder.Services.AddHostedService<RelayHostedService>();
    builder.Services.AddHostedService<InventoryScanWorker>();

    // ---------------------------------------------------------------------------
    // Build + run.
    // ---------------------------------------------------------------------------
    var app = builder.Build();

    app.MapControllers();

    await app.RunAsync().ConfigureAwait(false);
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
