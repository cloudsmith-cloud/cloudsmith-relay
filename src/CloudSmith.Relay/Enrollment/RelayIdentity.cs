// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Enrollment;

/// <summary>
/// Persisted Relay identity — what we know after a successful enrollment.
/// Serialized to <c>identity.json</c> in the identity directory.
/// </summary>
/// <param name="RelayId">PaaS-issued Relay identifier.</param>
/// <param name="PaasUrl">Base PaaS URL (https scheme).</param>
/// <param name="DisplayName">Display name registered with PaaS.</param>
/// <param name="EnrolledAtUtc">When enrollment completed.</param>
public sealed record RelayIdentity(
    string RelayId,
    string PaasUrl,
    string DisplayName,
    DateTimeOffset EnrolledAtUtc);
