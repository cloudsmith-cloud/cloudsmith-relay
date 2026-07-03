// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Connection;
using CloudSmith.Relay.Enrollment;
using CloudSmith.Relay.Execution;
using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Lan;
using CloudSmith.Relay.Security;
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
var siteId = Environment.GetEnvironmentVariable("RELAY_SITE_ID");

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
    SiteId            = siteId,
    HyperVHosts       = hyperVHosts,
};

// ---------------------------------------------------------------------------
// Logging — Serilog -> stdout. AB#2357 — standardised enricher set.
// ---------------------------------------------------------------------------
const string LogTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] [{service}] {CorrelationId}{Message:lj}{NewLine}{Exception}";
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "cloudsmith-relay")
    .Enrich.WithProperty("machine", Environment.MachineName)
    .WriteTo.Console(outputTemplate: LogTemplate)
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
    // RelayJwtService — signs per-agent JWTs and challenge-response nonces.
    // Loaded from the relay's RSA private key.  Falls back to a key-less stub
    // before first enrollment so DI resolves without throwing.
    // ---------------------------------------------------------------------------
    builder.Services.AddSingleton(sp =>
    {
        var keyPath = Path.Combine(identityDir, "relay.key");
        if (File.Exists(keyPath))
        {
            return RelayJwtService.FromPrivateKeyFile(keyPath);
        }
        // Pre-enrollment: no key yet — return a no-op instance backed by a
        // freshly generated ephemeral key.  The real key will be on disk before
        // any agent tries to enroll (enrollment completes before the LAN listener
        // accepts connections).
        Log.Warning("RelayJwtService: identity key not found at {Path} — using ephemeral key until enrollment completes", keyPath);
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return RelayJwtService.FromPrivateKeyPem(rsa.ExportPkcs8PrivateKeyPem());
    });

    // ---------------------------------------------------------------------------
    // Agent registry — SQLite-backed implementation (AB#2491 F-3/F-5 fix).
    // Registered as both the concrete type (for LanController injection) and the
    // IAgentRegistry interface (for InventoryScanWorker lookup).
    // ---------------------------------------------------------------------------
    builder.Services.AddSingleton(sp =>
        new SqliteAgentRegistry(
            agentEnrollmentToken,
            sp.GetRequiredService<RelayJwtService>(),
            Path.Combine(identityDir, SqliteAgentRegistry.DefaultDbFileName),
            sp.GetRequiredService<ILogger<SqliteAgentRegistry>>()));

    builder.Services.AddSingleton<IAgentRegistry>(sp =>
        sp.GetRequiredService<SqliteAgentRegistry>());

    // Job queue — SQLite-persisted (AB#4840); routes jobs from PaaS (WebSocket)
    // to Agents (LAN poll) and survives relay restarts. Shares the SQLite database
    // file with the agent registry.
    builder.Services.AddSingleton(sp => new AgentJobQueue(
        Path.Combine(identityDir, SqliteAgentRegistry.DefaultDbFileName),
        sp.GetRequiredService<ILogger<AgentJobQueue>>()));

    // Job dispatch handler — routes job.dispatch frames to the Agent queue or
    // the PSRemote execution path, and produces the contract job.ack (AB#2961).
    builder.Services.AddSingleton<JobDispatchHandler>();

    // Result forwarder — drains the durable job_results queue upstream as
    // job.result frames, replaying after reconnect (AB#4841).
    builder.Services.AddSingleton<JobResultForwarder>();

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
    builder.Services.AddHostedService<ResultForwardingWorker>();

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
