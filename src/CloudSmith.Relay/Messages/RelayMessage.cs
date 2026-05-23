// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace CloudSmith.Relay.Messages;

/// <summary>
/// Discriminated union of every message flowing across the Relay -> PaaS
/// WebSocket. Polymorphism is wire-encoded via the <c>$type</c> JSON discriminator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(JobDispatch), typeDiscriminator: "job.dispatch")]
[JsonDerivedType(typeof(JobAck), typeDiscriminator: "job.ack")]
[JsonDerivedType(typeof(InventoryPush), typeDiscriminator: "inventory.push")]
[JsonDerivedType(typeof(HealthProbePush), typeDiscriminator: "health.push")]
[JsonDerivedType(typeof(Heartbeat), typeDiscriminator: "heartbeat")]
public abstract record RelayMessage;

/// <summary>PaaS -> Relay: dispatch a job for execution against an Agent or via PSRemote.</summary>
public sealed record JobDispatch(
    string JobId,
    string Action,
    Dictionary<string, object> Args) : RelayMessage;

/// <summary>Relay -> PaaS: acknowledge receipt of a JobDispatch.</summary>
public sealed record JobAck(
    string JobId,
    string Status,
    string? Detail = null) : RelayMessage;

/// <summary>Relay -> PaaS: bulk inventory push for a managed cluster.</summary>
public sealed record InventoryPush(
    string ClusterId,
    IReadOnlyList<VmSnapshot> Vms) : RelayMessage;

/// <summary>Relay -> PaaS: cluster-level health probe results.</summary>
public sealed record HealthProbePush(
    string ClusterId,
    string Status,
    IReadOnlyList<HealthCheck> Checks) : RelayMessage;

/// <summary>Relay -> PaaS: liveness heartbeat.</summary>
public sealed record Heartbeat(DateTimeOffset At) : RelayMessage;

/// <summary>
/// Async event-handler signature used by <see cref="Connection.IRelayConnection.OnMessageReceived"/>.
/// </summary>
public delegate Task AsyncEventHandler<T>(object sender, T args);
