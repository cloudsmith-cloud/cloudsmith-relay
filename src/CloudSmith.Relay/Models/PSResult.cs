// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

namespace CloudSmith.Relay.Models;

/// <summary>
/// Result of a PSRemote script invocation.
/// </summary>
/// <param name="Success">True iff the pipeline completed without terminating errors.</param>
/// <param name="Output">Pipeline output objects, in order.</param>
/// <param name="ErrorRecord">Serialized terminating error (if any), else null.</param>
/// <param name="Elapsed">Wall-clock time the invocation took.</param>
public sealed record PSResult(
    bool Success,
    IReadOnlyList<object> Output,
    string? ErrorRecord,
    TimeSpan Elapsed);
