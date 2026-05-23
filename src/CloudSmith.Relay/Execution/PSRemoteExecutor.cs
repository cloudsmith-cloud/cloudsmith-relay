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
/// Credential selection per ADR-007 (2026-05-23 amendment):
///   <see cref="HostState.DomainJoined"/> → Kerberos (no credentials object — rely
///     on the Relay container's machine account or the configured Kerberos context).
///   All other states → use the configured local admin credential stored in
///     <see cref="PSRemoteCredential"/> supplied at construction time.
///
/// Transport selection for non-domain-joined hosts (AB#1686 amendment):
///   <c>RELAY_PSREMOTE_TRANSPORT=https-basic</c> (default) → HTTPS:5986 + Basic +
///     SkipCACheck + SkipCNCheck. This is the Ansible-on-Windows MVP pattern and
///     the only transport that works from a Linux container against Server 2025
///     after Microsoft's NTLM-over-HTTP hardening — PSWSMan 2.3.1's forked WSMan
///     natives return MI_RESULT_ACCESS_DENIED for Negotiate-over-HTTP against
///     Server 2025 even with valid local admin credentials.
///   <c>RELAY_PSREMOTE_TRANSPORT=http-negotiate</c> → legacy HTTP:5985 + Negotiate
///     (NTLM). Kept for older targets / debugging.
///
/// AB#1680 — replaces <see cref="CloudSmith.Relay.Stubs.StubPSRemoteExecutor"/>.
/// </summary>
public sealed class PSRemoteExecutor : IPSRemoteExecutor
{
    /// <summary>HTTPS:5986 + Basic + skip-cert (Server 2025 MVP default).</summary>
    public const string TransportHttpsBasic   = "https-basic";

    /// <summary>HTTP:5985 + Negotiate (legacy, broken on Server 2025).</summary>
    public const string TransportHttpNegotiate = "http-negotiate";

    private readonly IHostStateTracker _stateTracker;
    private readonly PSRemoteCredential _credential;
    private readonly string _transport;
    private readonly ILogger<PSRemoteExecutor> _logger;

    public PSRemoteExecutor(
        IHostStateTracker stateTracker,
        PSRemoteCredential credential,
        ILogger<PSRemoteExecutor> logger)
        : this(stateTracker, credential, ResolveTransportFromEnv(), logger)
    {
    }

    // Internal constructor for unit tests — bypasses env-var lookup so tests
    // can assert behaviour for both transports deterministically.
    internal PSRemoteExecutor(
        IHostStateTracker stateTracker,
        PSRemoteCredential credential,
        string transport,
        ILogger<PSRemoteExecutor> logger)
    {
        _stateTracker = stateTracker;
        _credential   = credential;
        _transport    = NormalizeTransport(transport);
        _logger       = logger;
    }

    /// <inheritdoc />
    public Task<PSResult> InvokeAsync(
        string hostId,
        string script,
        IDictionary<string, object>? args,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var runspace = OpenRunspace(hostId);
            using var ps       = System.Management.Automation.PowerShell.Create();
            ps.Runspace = runspace;

            var cmd = ps.AddScript(script);
            if (args is not null)
            {
                foreach (var kv in args)
                    cmd.AddParameter(kv.Key, kv.Value);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
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
        }, ct);
    }

    /// <summary>
    /// Run <c>Get-VM</c> / <c>Get-VMHost</c> on <paramref name="hostId"/> and
    /// return a <see cref="VmSnapshot"/> for every VM found.
    /// </summary>
    public Task<IReadOnlyList<VmSnapshot>> GetInventoryAsync(string hostId, CancellationToken ct)
    {
        return Task.Run<IReadOnlyList<VmSnapshot>>(() =>
        {
            using var runspace = OpenRunspace(hostId);
            using var ps       = System.Management.Automation.PowerShell.Create();
            ps.Runspace = runspace;

            _logger.LogInformation("PSRemote {Host}: running Get-VM", hostId);

            // Get-VM returns Microsoft.HyperV.PowerShell.VirtualMachine objects.
            ps.AddCommand("Get-VM");
            var vms = ps.Invoke();

            if (ps.HadErrors)
            {
                foreach (var err in ps.Streams.Error)
                    _logger.LogWarning("PSRemote {Host}: Get-VM error: {Err}", hostId, err?.ToString());
            }

            var snapshots = new List<VmSnapshot>(vms.Count);
            var now = DateTimeOffset.UtcNow;
            foreach (var vm in vms)
            {
                if (vm?.BaseObject is null) continue;

                // Access via dynamic or PSMemberInfo to avoid hard reference to
                // Microsoft.HyperV.PowerShell assembly (not available in the relay
                // container — only the PowerShell remoting pipeline is used).
                var name   = GetPropertyString(vm, "Name")   ?? GetPropertyString(vm, "VMName") ?? "unknown";
                var vmGuid = GetPropertyString(vm, "VMId")   ?? GetPropertyString(vm, "Id")     ?? Guid.NewGuid().ToString();
                var state  = GetPropertyString(vm, "State")  ?? "unknown";
                var cpuCount = GetPropertyInt(vm, "ProcessorCount") ?? 0;

                // MemoryAssigned is bytes; fall back to MemoryStartup.
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

            _logger.LogInformation("PSRemote {Host}: found {Count} VM(s)", hostId, snapshots.Count);
            return snapshots;
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Runspace factory
    // -------------------------------------------------------------------------

    private Runspace OpenRunspace(string hostId)
    {
        var connInfo = BuildConnectionInfo(hostId, _stateTracker.GetState(hostId), _credential, _transport);
        var runspace = RunspaceFactory.CreateRunspace(connInfo);
        runspace.Open();
        return runspace;
    }

    /// <summary>
    /// Build a <see cref="WSManConnectionInfo"/> for the given host. Exposed as
    /// <c>internal</c> so unit tests can assert the resulting scheme/port/auth/skip
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
            // skip-cert-validation (Ansible-on-Windows MVP pattern). Operator is
            // responsible for provisioning the WinRM HTTPS listener (the iter
            // playbook does this for the cluster-sim VM). Phase V will tighten
            // with proper PKI + cert validation.
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

        connInfo.OperationTimeout    = 60_000; // 60 s
        connInfo.OpenTimeout         = 30_000; // 30 s
        connInfo.MaximumReceivedDataSizePerCommand = 50 * 1024 * 1024; // 50 MB

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
            return TransportHttpsBasic; // MVP default (AB#1686)

        var v = raw.Trim().ToLowerInvariant();
        return v switch
        {
            TransportHttpsBasic   => TransportHttpsBasic,
            TransportHttpNegotiate => TransportHttpNegotiate,
            _ => TransportHttpsBasic, // unknown → safe default
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
            int i    => i,
            long l   => (int)l,
            _        => int.TryParse(member.Value.ToString(), out var v) ? v : null,
        };
    }

    private static long? GetPropertyLong(PSObject obj, string name)
    {
        var member = obj.Properties[name];
        if (member?.Value is null) return null;
        return member.Value switch
        {
            long l  => l,
            int i   => (long)i,
            _       => long.TryParse(member.Value.ToString(), out var v) ? v : null,
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
