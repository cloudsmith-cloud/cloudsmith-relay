// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Messages;

/// <summary>
/// Inventory snapshot of a single VM, shaped to match the cloudsmith-api
/// <c>POST /api/v1/inventory/ingest</c> request body.
/// </summary>
/// <param name="VmId">Stable VM identifier (host's GUID for the VM).</param>
/// <param name="Name">Human-readable VM name as reported by the hypervisor.</param>
/// <param name="HostId">Identifier of the host the VM is running on.</param>
/// <param name="State">Power state (e.g. "Running", "Off", "Paused").</param>
/// <param name="CpuCount">vCPU count.</param>
/// <param name="MemoryBytes">Configured RAM in bytes.</param>
/// <param name="ObservedAtUtc">When this snapshot was taken on the Relay/Agent.</param>
public sealed record VmSnapshot(
    string VmId,
    string Name,
    string HostId,
    string State,
    int CpuCount,
    long MemoryBytes,
    DateTimeOffset ObservedAtUtc);
