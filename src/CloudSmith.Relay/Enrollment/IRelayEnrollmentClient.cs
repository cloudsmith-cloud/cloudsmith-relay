// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Enrollment;

/// <summary>
/// Result of enrolling this Relay with PaaS.
/// </summary>
/// <param name="RelayId">Stable Relay identifier assigned by PaaS.</param>
/// <param name="PaasUrl">Base URL of the PaaS control plane (https://...).</param>
public sealed record EnrollmentResult(string RelayId, string PaasUrl);

/// <summary>
/// Performs first-run enrollment of this Relay with CloudSmith PaaS.
/// </summary>
public interface IRelayEnrollmentClient
{
    /// <summary>
    /// Generate a key-pair, exchange the one-time <paramref name="token"/> for a
    /// long-lived Relay identity, and persist the result.
    /// </summary>
    /// <param name="token">One-time enrollment token from PaaS.</param>
    /// <param name="displayName">Human-readable name the Relay registers under.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<EnrollmentResult> EnrollAsync(string token, string displayName, CancellationToken ct);
}
