// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Models;

/// <summary>
/// Enrollment payload sent by an Agent when it first registers with the Relay.
/// </summary>
/// <param name="HostId">Stable host identifier (GUID string preferred).</param>
/// <param name="Hostname">DNS or NetBIOS name as observed by the host itself.</param>
/// <param name="AgentPublicKeyPem">Agent's enrollment public key, PEM-encoded.</param>
/// <param name="InitialState">Host's self-declared <see cref="HostState"/> at enrollment time.</param>
/// <param name="OneTimeToken">Single-use enrollment token issued by PaaS.</param>
public sealed record AgentEnrollmentRequest(
    string HostId,
    string Hostname,
    string AgentPublicKeyPem,
    HostState InitialState,
    string OneTimeToken);
