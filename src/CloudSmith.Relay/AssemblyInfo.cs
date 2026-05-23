// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

// Allow the test project to access internal members (e.g. InventoryScanWorker.ScanOnceAsync).
[assembly: InternalsVisibleTo("CloudSmith.Relay.Tests")]
