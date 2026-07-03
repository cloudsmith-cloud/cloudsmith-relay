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
        var jobId = Guid.Parse("d3b07384-d9a0-4c9e-8f6e-1a2b3c4d5e6f");
        var msg = new JobDispatch(
            JobId: jobId,
            JobType: "cluster.validate-network",
            PayloadJson: "{\"scriptName\":\"Validate-Network.ps1\"}",
            IdempotencyKey: "op-2026-07-03-cluster01-validate",
            Traceparent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        var json = JsonSerializer.Serialize<RelayMessage>(msg, Opts);

        Assert.Contains("\"$type\":\"job.dispatch\"", json);
        Assert.Contains("\"jobId\":\"d3b07384-d9a0-4c9e-8f6e-1a2b3c4d5e6f\"", json);
        Assert.Contains("\"jobType\":\"cluster.validate-network\"", json);
        Assert.Contains("\"payloadJson\":", json);
        Assert.Contains("\"idempotencyKey\":", json);
        Assert.Contains("\"traceparent\":", json);

        var back = JsonSerializer.Deserialize<RelayMessage>(json, Opts);
        var jd = Assert.IsType<JobDispatch>(back);
        Assert.Equal(jobId, jd.JobId);
        Assert.Equal("cluster.validate-network", jd.JobType);
        Assert.Equal("{\"scriptName\":\"Validate-Network.ps1\"}", jd.PayloadJson);
        Assert.Equal("op-2026-07-03-cluster01-validate", jd.IdempotencyKey);
        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", jd.Traceparent);
    }

    [Fact]
    public void JobDispatch_CanonicalContractFrame_Deserializes()
    {
        // Literal frame from the frozen contract doc (AB#4839 §1.1).
        const string json = """
            {
              "$type": "job.dispatch",
              "jobId": "d3b07384-d9a0-4c9e-8f6e-1a2b3c4d5e6f",
              "jobType": "cluster.validate-network",
              "payloadJson": "{\"scriptName\":\"Validate-Network.ps1\",\"arguments\":{\"ClusterName\":\"clu-01\"}}",
              "idempotencyKey": "op-2026-07-03-cluster01-validate",
              "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
            }
            """;

        var back = JsonSerializer.Deserialize<RelayMessage>(json, Opts);
        var jd = Assert.IsType<JobDispatch>(back);
        Assert.Equal(Guid.Parse("d3b07384-d9a0-4c9e-8f6e-1a2b3c4d5e6f"), jd.JobId);
        Assert.Equal("cluster.validate-network", jd.JobType);
        Assert.Contains("Validate-Network.ps1", jd.PayloadJson);
        Assert.Equal("op-2026-07-03-cluster01-validate", jd.IdempotencyKey);
        Assert.NotNull(jd.Traceparent);
    }

    [Fact]
    public void JobDispatch_OptionalFieldsAbsent_DeserializeAsNull()
    {
        const string json = """
            {
              "$type": "job.dispatch",
              "jobId": "d3b07384-d9a0-4c9e-8f6e-1a2b3c4d5e6f",
              "jobType": "cluster.validate-network",
              "payloadJson": "{}"
            }
            """;

        var jd = Assert.IsType<JobDispatch>(JsonSerializer.Deserialize<RelayMessage>(json, Opts));
        Assert.Null(jd.IdempotencyKey);
        Assert.Null(jd.Traceparent);
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
        var jobId = Guid.NewGuid();
        var msg = new JobAck(jobId, "accepted");
        var json = JsonSerializer.Serialize<RelayMessage>(msg, Opts);

        Assert.Contains("\"$type\":\"job.ack\"", json);
        Assert.Contains("\"ackStatus\":\"accepted\"", json);

        var back = JsonSerializer.Deserialize<RelayMessage>(json, Opts);
        var ack = Assert.IsType<JobAck>(back);
        Assert.Equal(jobId, ack.JobId);
        Assert.Equal("accepted", ack.AckStatus);
        Assert.Null(ack.Detail);
    }

    [Fact]
    public void JobAck_Rejected_CarriesDetail()
    {
        var msg = new JobAck(Guid.NewGuid(), "rejected", "no enrolled agent available");
        var json = JsonSerializer.Serialize<RelayMessage>(msg, Opts);

        var ack = Assert.IsType<JobAck>(JsonSerializer.Deserialize<RelayMessage>(json, Opts));
        Assert.Equal("rejected", ack.AckStatus);
        Assert.Equal("no enrolled agent available", ack.Detail);
    }
}
