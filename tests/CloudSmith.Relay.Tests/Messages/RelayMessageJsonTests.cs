// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using CloudSmith.Relay.Messages;
using Xunit;

namespace CloudSmith.Relay.Tests.Messages;

public sealed class RelayMessageJsonTests
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Heartbeat_RoundTrip()
    {
        var msg = new Heartbeat(new DateTimeOffset(2026, 5, 23, 10, 0, 0, TimeSpan.Zero));
        var json = JsonSerializer.Serialize<RelayMessage>(msg, Opts);

        Assert.Contains("\"$type\":\"heartbeat\"", json);

        var back = JsonSerializer.Deserialize<RelayMessage>(json, Opts);
        var hb = Assert.IsType<Heartbeat>(back);
        Assert.Equal(msg.At, hb.At);
    }

    [Fact]
    public void JobDispatch_RoundTrip()
    {
        var msg = new JobDispatch(
            JobId: "job-1",
            Action: "agent.runModule",
            Args: new Dictionary<string, object>
            {
                ["module"] = "inventory.scan",
                ["timeoutSec"] = 60,
            });
        var json = JsonSerializer.Serialize<RelayMessage>(msg, Opts);

        Assert.Contains("\"$type\":\"job.dispatch\"", json);
        Assert.Contains("\"jobId\":\"job-1\"", json);

        var back = JsonSerializer.Deserialize<RelayMessage>(json, Opts);
        var jd = Assert.IsType<JobDispatch>(back);
        Assert.Equal("job-1", jd.JobId);
        Assert.Equal("agent.runModule", jd.Action);
        Assert.Equal(2, jd.Args.Count);
    }

    [Fact]
    public void InventoryPush_RoundTrip()
    {
        var msg = new InventoryPush(
            ClusterId: "cluster-a",
            Vms: new[]
            {
                new VmSnapshot(
                    VmId: "vm-1",
                    Name: "web01",
                    HostId: "host-a",
                    State: "Running",
                    CpuCount: 4,
                    MemoryBytes: 8L * 1024 * 1024 * 1024,
                    ObservedAtUtc: DateTimeOffset.UnixEpoch),
            });
        var json = JsonSerializer.Serialize<RelayMessage>(msg, Opts);

        var back = JsonSerializer.Deserialize<RelayMessage>(json, Opts);
        var ip = Assert.IsType<InventoryPush>(back);
        Assert.Equal("cluster-a", ip.ClusterId);
        Assert.Single(ip.Vms);
        Assert.Equal("web01", ip.Vms[0].Name);
        Assert.Equal(4, ip.Vms[0].CpuCount);
    }

    [Fact]
    public void HealthProbePush_RoundTrip()
    {
        var msg = new HealthProbePush(
            ClusterId: "cluster-a",
            Status: "Degraded",
            Checks: new[]
            {
                new HealthCheck("cluster.heartbeat", "Healthy", null),
                new HealthCheck("csv.online", "Unhealthy", "CSV03 offline"),
            });
        var json = JsonSerializer.Serialize<RelayMessage>(msg, Opts);

        var back = JsonSerializer.Deserialize<RelayMessage>(json, Opts);
        var hp = Assert.IsType<HealthProbePush>(back);
        Assert.Equal("Degraded", hp.Status);
        Assert.Equal(2, hp.Checks.Count);
        Assert.Equal("CSV03 offline", hp.Checks[1].Detail);
    }

    [Fact]
    public void JobAck_RoundTrip()
    {
        var msg = new JobAck("job-9", "Accepted", "ok");
        var json = JsonSerializer.Serialize<RelayMessage>(msg, Opts);

        Assert.Contains("\"$type\":\"job.ack\"", json);
        var back = JsonSerializer.Deserialize<RelayMessage>(json, Opts);
        var ack = Assert.IsType<JobAck>(back);
        Assert.Equal("job-9", ack.JobId);
        Assert.Equal("Accepted", ack.Status);
    }
}
