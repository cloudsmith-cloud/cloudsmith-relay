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
/// AB#1680 — replaces <see cref="CloudSmith.Relay.Stubs.StubPSRemoteExecutor"/>.
/// </summary>
public sealed class PSRemoteExecutor : IPSRemoteExecutor
{
    private readonly IHostStateTracker _stateTracker;
    private readonly PSRemoteCredential _credential;
    private readonly ILogger<PSRemoteExecutor> _logger;

    public PSRemoteExecutor(
        IHostStateTracker stateTracker,
        PSRemoteCredential credential,
        ILogger<PSRemoteExecutor> logger)
    {
        _stateTracker = stateTracker;
        _credential   = credential;
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

            var results = ps.Invoke();
            var output  = results.Select(o => o?.BaseObject).ToList();
            var errors  = ps.Streams.Error.Select(e => e?.ToString() ?? string.Empty).ToList();

            if (ps.HadErrors)
            {
                _logger.LogWarning(
                    "PSRemote {Host}: script produced {ErrorCount} error(s): {FirstError}",
                    hostId, errors.Count, errors.FirstOrDefault());
            }

            return new PSResult(output, errors, !ps.HadErrors);
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
        var state = _stateTracker.GetState(hostId);
        WSManConnectionInfo connInfo;

        if (state == HostState.DomainJoined)
        {
            // Kerberos — no credential object; rely on the process's ambient identity
            // or a Kerberos credential cache injected at container startup.
            connInfo = new WSManConnectionInfo(
                useSsl:       false,
                computerName: hostId,
                port:         5985,
                appName:      "/wsman",
                shellUri:     "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
                credential:   null);
            connInfo.AuthenticationMechanism = AuthenticationMechanism.Kerberos;
        }
        else
        {
            // Workgroup / Joining / Unknown — use the local admin credential.
            var psCredential = string.IsNullOrWhiteSpace(_credential.Username)
                ? null
                : new PSCredential(
                    _credential.Username,
                    _credential.Password.Length > 0
                        ? CreateSecureString(_credential.Password)
                        : new System.Security.SecureString());

            connInfo = new WSManConnectionInfo(
                useSsl:       false,
                computerName: hostId,
                port:         5985,
                appName:      "/wsman",
                shellUri:     "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
                credential:   psCredential);
            connInfo.AuthenticationMechanism = AuthenticationMechanism.Basic;
        }

        connInfo.OperationTimeout    = 30_000; // 30 s
        connInfo.OpenTimeout         = 20_000; // 20 s
        connInfo.MaximumReceivedDataSizePerCommand = 50 * 1024 * 1024; // 50 MB

        var runspace = RunspaceFactory.CreateRunspace(connInfo);
        runspace.Open();
        return runspace;
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
