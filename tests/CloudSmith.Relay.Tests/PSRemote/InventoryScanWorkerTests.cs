// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Connection;
using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Messages;
using CloudSmith.Relay.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CloudSmith.Relay.Tests.PSRemote;

/// <summary>
/// Smoke tests for <see cref="InventoryScanWorker"/> PSRemote path (AB#1680).
/// Verifies that when RELAY_HYPER_V_HOSTS is configured, the worker calls
/// IPSRemoteExecutor.GetInventoryAsync and sends the resulting VMs over the
/// relay connection.
/// </summary>
public sealed class InventoryScanWorkerTests
{
    /// <summary>
    /// When two dummy VMs are returned by the mock PSRemote executor, the
    /// InventoryScanWorker sends a single InventoryPush with both VMs.
    /// </summary>
    [Fact]
    public async Task ScanOnce_WithTwoVms_SendsInventoryPushWithBothVms()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var vm1 = new VmSnapshot("vm-guid-1", "VM-One", "hv-host-01", "Running", 4, 8L * 1024 * 1024 * 1024, now);
        var vm2 = new VmSnapshot("vm-guid-2", "VM-Two", "hv-host-01", "Off",     2, 4L * 1024 * 1024 * 1024, now);
        IReadOnlyList<VmSnapshot> fakeVms = new[] { vm1, vm2 };

        var mockPsRemote = new Mock<IPSRemoteExecutor>();
        mockPsRemote
            .Setup(e => e.GetInventoryAsync("hv-host-01", It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeVms);

        var mockConnection = new Mock<IRelayConnection>();
        mockConnection.SetupGet(c => c.IsConnected).Returns(true);

        InventoryPush? capturedPush = null;
        mockConnection
            .Setup(c => c.SendAsync(It.IsAny<RelayMessage>(), It.IsAny<CancellationToken>()))
            .Callback<RelayMessage, CancellationToken>((msg, _) => capturedPush = msg as InventoryPush)
            .Returns(Task.CompletedTask);

        var opts = Options.Create(new RelayOptions
        {
            PaasUrl     = "https://api.test.local",
            DisplayName = "test-relay",
            ClusterId   = "cluster-99",
            HyperVHosts = new[] { "hv-host-01" },
        });

        var worker = new InventoryScanWorker(
            opts,
            mockConnection.Object,
            mockPsRemote.Object,
            NullLogger<InventoryScanWorker>.Instance);

        // Act
        await worker.ScanOnceAsync(CancellationToken.None);

        // Assert
        mockPsRemote.Verify(e => e.GetInventoryAsync("hv-host-01", It.IsAny<CancellationToken>()), Times.Once);
        mockConnection.Verify(c => c.SendAsync(It.IsAny<InventoryPush>(), It.IsAny<CancellationToken>()), Times.Once);

        Assert.NotNull(capturedPush);
        Assert.Equal("cluster-99", capturedPush!.ClusterId);
        Assert.Equal(2, capturedPush.Vms.Count);
        Assert.Contains(capturedPush.Vms, v => v.Name == "VM-One");
        Assert.Contains(capturedPush.Vms, v => v.Name == "VM-Two");
    }

    /// <summary>
    /// When no hosts are configured, the worker sends an empty InventoryPush
    /// (liveness mode) without calling IPSRemoteExecutor.
    /// </summary>
    [Fact]
    public async Task ScanOnce_NoHostsConfigured_SendsEmptyPushWithoutCallingPsRemote()
    {
        // Arrange
        var mockPsRemote = new Mock<IPSRemoteExecutor>();
        var mockConnection = new Mock<IRelayConnection>();
        mockConnection.SetupGet(c => c.IsConnected).Returns(true);

        InventoryPush? capturedPush = null;
        mockConnection
            .Setup(c => c.SendAsync(It.IsAny<RelayMessage>(), It.IsAny<CancellationToken>()))
            .Callback<RelayMessage, CancellationToken>((msg, _) => capturedPush = msg as InventoryPush)
            .Returns(Task.CompletedTask);

        var opts = Options.Create(new RelayOptions
        {
            PaasUrl     = "https://api.test.local",
            DisplayName = "test-relay",
            ClusterId   = "cluster-99",
            HyperVHosts = Array.Empty<string>(),
        });

        var worker = new InventoryScanWorker(
            opts,
            mockConnection.Object,
            mockPsRemote.Object,
            NullLogger<InventoryScanWorker>.Instance);

        // Act
        await worker.ScanOnceAsync(CancellationToken.None);

        // Assert
        mockPsRemote.Verify(e => e.GetInventoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotNull(capturedPush);
        Assert.Empty(capturedPush!.Vms);
    }
}
