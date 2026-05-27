// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Management.Automation;
using System.Management.Automation.Runspaces;
using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Messages;
using CloudSmith.Relay.Models;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Relay.Execution;

/// <summary>
/// Real <see cref="IPSRemoteExecutor"/> implementation backed by
/// <see cref="System.Management.Automation"/> WSMan remoting.
///
/// Auth / transport is fully delegated to <see cref="PSRemoteTransport"/> (AB#1666):
///   • Domain context detected via <c>USERDNSDOMAIN</c> env var → Kerberos over HTTPS:5986.
///   • No domain context + <see cref="PSRemoteCredential.ClientCertificate"/> set → cert auth over HTTPS:5986.
///   • No domain context + no cert → falls back to Basic+skip-cert over HTTPS:5986
///     (legacy <see cref="TransportHttpsBasic"/> path, kept for Workgroup hosts without a cert).
///   • HTTP:5985 is never used by <see cref="PSRemoteTransport"/>; the legacy Negotiate path
///     is kept only in <see cref="BuildConnectionInfo"/> for backwards compatibility with tests.
///
/// Credential selection (per ADR-007, 2026-05-23 amendment):
///   <see cref="HostState.DomainJoined"/> → <see cref="PSRemoteTransport"/> Kerberos path.
///   All other states → <see cref="PSRemoteTransport"/> Auto path (cert → Basic fallback).
///
/// AB#1680 — replaces <see cref="CloudSmith.Relay.Stubs.StubPSRemoteExecutor"/>.
/// AB#1666 — auth/transport logic extracted to <see cref="PSRemoteTransport"/>.
/// </summary>
public sealed class PSRemoteExecutor : IPSRemoteExecutor
{
    /// <summary>HTTPS:5986 + Basic + skip-cert (Server 2025 MVP default for non-cert Workgroup hosts).</summary>
    public const string TransportHttpsBasic   = "https-basic";

    /// <summary>HTTP:5985 + Negotiate (legacy, broken on Server 2025; retained for test compat only).</summary>
    public const string TransportHttpNegotiate = "http-negotiate";

    private readonly IHostStateTracker _stateTracker;
    private readonly PSRemoteCredential _credential;
    private readonly string _transport;
    private readonly PSRemoteTransport _psRemoteTransport;
    private readonly ILogger<PSRemoteExecutor> _logger;

    public PSRemoteExecutor(
        IHostStateTracker stateTracker,
        PSRemoteCredential credential,
        PSRemoteTransport psRemoteTransport,
        ILogger<PSRemoteExecutor> logger)
        : this(stateTracker, credential, psRemoteTransport, ResolveTransportFromEnv(), logger)
    {
    }

    // Internal constructor for unit tests — bypasses env-var lookup so tests
    // can assert behaviour for both transports deterministically.
    internal PSRemoteExecutor(
        IHostStateTracker stateTracker,
        PSRemoteCredential credential,
        PSRemoteTransport psRemoteTransport,
        string transport,
        ILogger<PSRemoteExecutor> logger)
    {
        _stateTracker      = stateTracker;
        _credential        = credential;
        _psRemoteTransport = psRemoteTransport;
        _transport         = NormalizeTransport(transport);
        _logger            = logger;
    }

    /// <inheritdoc />
    public Task<PSResult> InvokeAsync(
        string hostId,
        string script,
        IDictionary<string, object>? args,
        CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            var state = _stateTracker.GetState(hostId);

            // Delegate to PSRemoteTransport (AB#1666) when the host is domain-joined
            // or when a client certificate is available. Legacy Basic path for Workgroup
            // hosts without a cert is handled below.
            if (state == HostState.DomainJoined || _credential.ClientCertificate is not null)
            {
                return await InvokeViaTransportAsync(hostId, state, script, args, ct)
                    .ConfigureAwait(false);
            }

            // Legacy Basic/Negotiate path (Workgroup without cert).
            return await Task.Run(() => InvokeLegacy(hostId, script, args), ct)
                .ConfigureAwait(false);
        }, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VmSnapshot>> GetInventoryAsync(string hostId, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            var state = _stateTracker.GetState(hostId);

            if (state == HostState.DomainJoined || _credential.ClientCertificate is not null)
            {
                return await GetInventoryViaTransportAsync(hostId, state, ct)
                    .ConfigureAwait(false);
            }

            return await Task.Run(() => GetInventoryLegacy(hostId), ct)
                .ConfigureAwait(false);
        }, ct);
    }

    // -------------------------------------------------------------------------
    // PSRemoteTransport-delegating paths (AB#1666)
    // -------------------------------------------------------------------------

    private async Task<PSResult> InvokeViaTransportAsync(
        string hostId,
        HostState state,
        string script,
        IDictionary<string, object>? args,
        CancellationToken ct)
    {
        var opts = BuildTransportOptions(hostId, state);

        using var session = await _psRemoteTransport.ConnectAsync(opts, ct).ConfigureAwait(false);
        using var ps      = System.Management.Automation.PowerShell.Create();
        ps.Runspace = session.Runspace;

        var cmd = ps.AddScript(script);
        if (args is not null)
        {
            foreach (var kv in args)
                cmd.AddParameter(kv.Key, kv.Value);
        }

        var sw      = System.Diagnostics.Stopwatch.StartNew();
        var results = ps.Invoke();
        sw.Stop();

        IReadOnlyList<object> output = results
            .Select(o => o?.BaseObject ?? (object)string.Empty)
            .ToList();
        var firstError = ps.Streams.Error.FirstOrDefault()?.ToString();

        if (ps.HadErrors)
        {
            _logger.LogWarning(
                "PSRemote {Host}: script produced error(s): {FirstError}",
                hostId, firstError);
        }

        return new PSResult(
            Success:     !ps.HadErrors,
            Output:      output,
            ErrorRecord: firstError,
            Elapsed:     sw.Elapsed);
    }

    private async Task<IReadOnlyList<VmSnapshot>> GetInventoryViaTransportAsync(
        string hostId,
        HostState state,
        CancellationToken ct)
    {
        var opts = BuildTransportOptions(hostId, state);

        using var session = await _psRemoteTransport.ConnectAsync(opts, ct).ConfigureAwait(false);
        using var ps      = System.Management.Automation.PowerShell.Create();
        ps.Runspace = session.Runspace;

        _logger.LogInformation("PSRemote {Host}: running Get-VM (transport={AuthMode})",
            hostId, session.AuthModeUsed);

        ps.AddCommand("Get-VM");
        var vms = ps.Invoke();

        if (ps.HadErrors)
        {
            foreach (var err in ps.Streams.Error)
                _logger.LogWarning("PSRemote {Host}: Get-VM error: {Err}", hostId, err?.ToString());
        }

        return BuildSnapshots(hostId, vms);
    }

    private PSRemoteConnectionOptions BuildTransportOptions(string hostId, HostState state)
    {
        if (state == HostState.DomainJoined)
        {
            return new PSRemoteConnectionOptions
            {
                Hostname   = hostId,
                AuthMode   = PSRemoteAuthMode.Kerberos,
            };
        }

        if (_credential.ClientCertificate is not null)
        {
            return new PSRemoteConnectionOptions
            {
                Hostname          = hostId,
                AuthMode          = PSRemoteAuthMode.Certificate,
                ClientCertificate = _credential.ClientCertificate,
            };
        }

        // Auto: will pick Kerberos if USERDNSDOMAIN is set, cert if cert available,
        // or throw if neither — PSRemoteTransport enforces no-HTTP.
        return new PSRemoteConnectionOptions
        {
            Hostname          = hostId,
            AuthMode          = PSRemoteAuthMode.Auto,
            ClientCertificate = _credential.ClientCertificate,
        };
    }

    // -------------------------------------------------------------------------
    // Legacy Basic/Negotiate paths (Workgroup hosts without a client cert)
    // Kept so that existing deployments continue to work.
    // -------------------------------------------------------------------------

    private PSResult InvokeLegacy(
        string hostId,
        string script,
        IDictionary<string, object>? args)
    {
        using var runspace = OpenLegacyRunspace(hostId);
        using var ps       = System.Management.Automation.PowerShell.Create();
        ps.Runspace = runspace;

        var cmd = ps.AddScript(script);
        if (args is not null)
        {
            foreach (var kv in args)
                cmd.AddParameter(kv.Key, kv.Value);
        }

        var sw      = System.Diagnostics.Stopwatch.StartNew();
        var results = ps.Invoke();
        sw.Stop();

        IReadOnlyList<object> output = results
            .Select(o => o?.BaseObject ?? (object)string.Empty)
            .ToList();
        var firstError = ps.Streams.Error.FirstOrDefault()?.ToString();

        if (ps.HadErrors)
        {
            _logger.LogWarning(
                "PSRemote {Host}: script produced error(s): {FirstError}",
                hostId, firstError);
        }

        return new PSResult(
            Success:     !ps.HadErrors,
            Output:      output,
            ErrorRecord: firstError,
            Elapsed:     sw.Elapsed);
    }

    private IReadOnlyList<VmSnapshot> GetInventoryLegacy(string hostId)
    {
        using var runspace = OpenLegacyRunspace(hostId);
        using var ps       = System.Management.Automation.PowerShell.Create();
        ps.Runspace = runspace;

        _logger.LogInformation("PSRemote {Host}: running Get-VM (legacy transport)", hostId);

        ps.AddCommand("Get-VM");
        var vms = ps.Invoke();

        if (ps.HadErrors)
        {
            foreach (var err in ps.Streams.Error)
                _logger.LogWarning("PSRemote {Host}: Get-VM error: {Err}", hostId, err?.ToString());
        }

        return BuildSnapshots(hostId, vms);
    }

    private Runspace OpenLegacyRunspace(string hostId)
    {
        var state    = _stateTracker.GetState(hostId);
        var connInfo = BuildConnectionInfo(hostId, state, _credential, _transport);

        // Emit a warning when the legacy HTTPS+Basic path is used: TLS validation is
        // unconditionally skipped there (see SECURITY NOTE in BuildConnectionInfo).
        if (state != HostState.DomainJoined &&
            string.Equals(_transport, TransportHttpsBasic, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "PSRemote TLS certificate validation is disabled for host {Host} (legacy https-basic path). " +
                "Set CaThumbprint in connection options for production use.",
                hostId);
        }

        var runspace = RunspaceFactory.CreateRunspace(connInfo);
        runspace.Open();
        return runspace;
    }

    // -------------------------------------------------------------------------
    // Snapshot builder (shared)
    // -------------------------------------------------------------------------

    private static IReadOnlyList<VmSnapshot> BuildSnapshots(
        string hostId,
        System.Collections.ObjectModel.Collection<PSObject> vms)
    {
        var snapshots = new List<VmSnapshot>(vms.Count);
        var now = DateTimeOffset.UtcNow;

        foreach (var vm in vms)
        {
            if (vm?.BaseObject is null) continue;

            var name      = GetPropertyString(vm, "Name")   ?? GetPropertyString(vm, "VMName") ?? "unknown";
            var vmGuid    = GetPropertyString(vm, "VMId")   ?? GetPropertyString(vm, "Id")     ?? Guid.NewGuid().ToString();
            var state     = GetPropertyString(vm, "State")  ?? "unknown";
            var cpuCount  = GetPropertyInt(vm, "ProcessorCount") ?? 0;
            var memBytes  = GetPropertyLong(vm, "MemoryAssigned") ?? GetPropertyLong(vm, "MemoryStartup") ?? 0L;

            snapshots.Add(new VmSnapshot(
                VmId:          vmGuid,
                Name:          name,
                HostId:        hostId,
                State:         state,
                CpuCount:      cpuCount,
                MemoryBytes:   memBytes,
                ObservedAtUtc: now));
        }

        return snapshots;
    }

    // -------------------------------------------------------------------------
    // Legacy WSManConnectionInfo builder — kept for backward compat and test coverage
    // -------------------------------------------------------------------------

    /// <summary>
    /// Build a <see cref="WSManConnectionInfo"/> for the given host. Exposed as
    /// <c>internal</c> so existing unit tests can assert the resulting scheme/port/auth/skip
    /// flags without standing up a runspace.
    /// </summary>
    internal static WSManConnectionInfo BuildConnectionInfo(
        string hostId,
        HostState state,
        PSRemoteCredential credential,
        string transport)
    {
        WSManConnectionInfo connInfo;

        if (state == HostState.DomainJoined)
        {
            // Kerberos — no credential object; rely on the process's ambient identity
            // or a Kerberos credential cache injected at container startup.
            // Domain-joined uses HTTP:5985 + Kerberos (Phase V will revisit with
            // proper PKI). Server 2025 NTLM hardening does NOT affect Kerberos.
            connInfo = new WSManConnectionInfo(
                useSsl:       false,
                computerName: hostId,
                port:         5985,
                appName:      "/wsman",
                shellUri:     "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
                credential:   null);
            connInfo.AuthenticationMechanism = AuthenticationMechanism.Kerberos;
        }
        else if (string.Equals(transport, TransportHttpsBasic, StringComparison.OrdinalIgnoreCase))
        {
            // AB#1686 — Server 2025 NTLM hardening + PSWSMan's forked WSMan natives
            // make Negotiate-over-HTTP infeasible. Switch to HTTPS:5986 + Basic +
            // skip-cert-validation (Ansible-on-Windows MVP pattern).
            //
            // SECURITY NOTE (H1 / ADR-057): TLS validation is unconditionally skipped
            // in this legacy path because PSRemoteCredential carries no CaThumbprint.
            // This path is only reached for Workgroup hosts without a client certificate;
            // it should be eliminated in Phase V when operator-provisioned PKI is in
            // place.  The warning below makes the insecure configuration visible in
            // operator logs until then.
            var psCredential = BuildPSCredential(credential);

            connInfo = new WSManConnectionInfo(
                new Uri($"https://{hostId}:5986/wsman"),
                "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
                psCredential);
            connInfo.AuthenticationMechanism = AuthenticationMechanism.Basic;
            connInfo.SkipCACheck = true;
            connInfo.SkipCNCheck = true;
        }
        else
        {
            // Legacy HTTP:5985 + Negotiate (NTLM) — kept for older targets and
            // for debugging. Broken on Server 2025 (returns MI_RESULT_ACCESS_DENIED).
            var psCredential = BuildPSCredential(credential);

            connInfo = new WSManConnectionInfo(
                useSsl:       false,
                computerName: hostId,
                port:         5985,
                appName:      "/wsman",
                shellUri:     "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
                credential:   psCredential);
            connInfo.AuthenticationMechanism = AuthenticationMechanism.Negotiate;
        }

        connInfo.OperationTimeout                    = 60_000;
        connInfo.OpenTimeout                         = 30_000;
        connInfo.MaximumReceivedDataSizePerCommand    = 50 * 1024 * 1024;

        return connInfo;
    }

    private static PSCredential? BuildPSCredential(PSRemoteCredential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.Username))
            return null;

        var secure = credential.Password.Length > 0
            ? CreateSecureString(credential.Password)
            : new System.Security.SecureString();
        return new PSCredential(credential.Username, secure);
    }

    private static string ResolveTransportFromEnv()
    {
        var raw = Environment.GetEnvironmentVariable("RELAY_PSREMOTE_TRANSPORT");
        return NormalizeTransport(raw);
    }

    private static string NormalizeTransport(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return TransportHttpsBasic;

        var v = raw.Trim().ToLowerInvariant();
        return v switch
        {
            TransportHttpsBasic    => TransportHttpsBasic,
            TransportHttpNegotiate => TransportHttpNegotiate,
            _                      => TransportHttpsBasic,
        };
    }

    // -------------------------------------------------------------------------
    // PSObject reflection helpers
    // -------------------------------------------------------------------------

    private static string? GetPropertyString(PSObject obj, string name)
    {
        var member = obj.Properties[name];
        return member?.Value?.ToString();
    }

    private static int? GetPropertyInt(PSObject obj, string name)
    {
        var member = obj.Properties[name];
        if (member?.Value is null) return null;
        return member.Value switch
        {
            int i  => i,
            long l => (int)l,
            _      => int.TryParse(member.Value.ToString(), out var v) ? v : null,
        };
    }

    private static long? GetPropertyLong(PSObject obj, string name)
    {
        var member = obj.Properties[name];
        if (member?.Value is null) return null;
        return member.Value switch
        {
            long l => l,
            int i  => (long)i,
            _      => long.TryParse(member.Value.ToString(), out var v) ? v : null,
        };
    }

    private static System.Security.SecureString CreateSecureString(string plain)
    {
        var secure = new System.Security.SecureString();
        foreach (var c in plain) secure.AppendChar(c);
        secure.MakeReadOnly();
        return secure;
    }
}
