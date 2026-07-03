// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Messages;
using CloudSmith.Relay.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Relay.Lan;

/// <summary>Outcome of persisting a dispatched job to the local queue.</summary>
public enum EnqueueOutcome
{
    /// <summary>The job was newly persisted to the queue.</summary>
    Accepted,

    /// <summary>The jobId is already known to the queue — safe re-dispatch (contract §4.2).</summary>
    Duplicate,
}

/// <summary>
/// SQLite-persisted per-agent job queue (AB#4840 / contract §6.2). PaaS dispatches
/// jobs via the control WebSocket (<c>job.dispatch</c>); Agents poll via
/// GET /lan/v1/agents/{agentId}/jobs; results flow back via
/// POST /lan/v1/agents/{agentId}/jobs/{jobId}/result.
///
/// Durability guarantees:
///   - <c>job.ack (accepted)</c> is sent only after <see cref="Enqueue"/> commits —
///     an acked job survives a Relay crash.
///   - Pending (undelivered) jobs are reloaded from SQLite on restart and served
///     to agents on their next poll — nothing lost.
///   - Delivered-but-unresulted jobs are retained until a result arrives; after
///     a redelivery grace they are served again on the next poll (at-least-once,
///     contract §4.4 makes re-execution safe).
///   - The queue never fabricates results — timeout adjudication belongs to the API.
///
/// Schema (shares the SQLite database file with <see cref="SqliteAgentRegistry"/>):
/// <code>
/// jobs(
///   job_id          TEXT PRIMARY KEY,   -- Guid, "D" format
///   agent_id        TEXT NOT NULL,      -- target agent or the psremote pseudo-agent
///   job_type        TEXT NOT NULL,
///   payload_json    TEXT NOT NULL,      -- opaque; parsed only by the executing target
///   idempotency_key TEXT NULL,
///   traceparent     TEXT NULL,
///   status          TEXT NOT NULL,      -- pending | delivered | completed
///   enqueued_at     TEXT NOT NULL,      -- ISO-8601
///   delivered_at    TEXT NULL,
///   completed_at    TEXT NULL
/// )
/// </code>
/// </summary>
public sealed class AgentJobQueue : IDisposable
{
    /// <summary>
    /// How long a delivered-but-unresulted job is left alone before it becomes
    /// eligible for redelivery on the next agent poll (agent crash/reconnect path).
    /// </summary>
    public static readonly TimeSpan DefaultRedeliveryGrace = TimeSpan.FromMinutes(5);

    private readonly ILogger<AgentJobQueue> _logger;
    private readonly TimeSpan _redeliveryGrace;
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public AgentJobQueue(string dbPath, ILogger<AgentJobQueue> logger, TimeSpan? redeliveryGrace = null)
    {
        _logger = logger;
        _redeliveryGrace = redeliveryGrace ?? DefaultRedeliveryGrace;

        // Pooling=False: single long-lived connection owned by this instance;
        // guarantees the file handle is released on Dispose.
        _db = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _db.Open();

        using (var pragma = _db.CreateCommand())
        {
            // The agent registry shares this database file — wait out its locks.
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
        }

        EnsureSchema();
    }

    // ------------------------------------------------------------------
    // Schema
    // ------------------------------------------------------------------

    private void EnsureSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS jobs (
                job_id          TEXT    NOT NULL PRIMARY KEY,
                agent_id        TEXT    NOT NULL,
                job_type        TEXT    NOT NULL,
                payload_json    TEXT    NOT NULL,
                idempotency_key TEXT    NULL,
                traceparent     TEXT    NULL,
                status          TEXT    NOT NULL DEFAULT 'pending',
                enqueued_at     TEXT    NOT NULL,
                delivered_at    TEXT    NULL,
                completed_at    TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS ix_jobs_agent_status ON jobs(agent_id, status);
            """;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // Enqueue / Dequeue / Complete
    // ------------------------------------------------------------------

    /// <summary>
    /// Durably persist a dispatched job for the target agent. The insert commits
    /// before this method returns — callers may ack <c>accepted</c> afterwards.
    /// Idempotent on jobId: a re-dispatch of a known job (any status) returns
    /// <see cref="EnqueueOutcome.Duplicate"/> and changes nothing.
    /// </summary>
    public EnqueueOutcome Enqueue(string agentId, JobDispatch dispatch)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO jobs (job_id, agent_id, job_type, payload_json,
                                  idempotency_key, traceparent, status, enqueued_at)
                VALUES (@jobId, @agentId, @jobType, @payload, @idem, @trace, 'pending', @now)
                ON CONFLICT(job_id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("@jobId", dispatch.JobId.ToString("D"));
            cmd.Parameters.AddWithValue("@agentId", agentId);
            cmd.Parameters.AddWithValue("@jobType", dispatch.JobType);
            cmd.Parameters.AddWithValue("@payload", dispatch.PayloadJson);
            cmd.Parameters.AddWithValue("@idem", (object?)dispatch.IdempotencyKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@trace", (object?)dispatch.Traceparent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

            if (cmd.ExecuteNonQuery() == 0)
            {
                _logger.LogInformation("Duplicate dispatch for job {JobId} — already persisted", dispatch.JobId);
                return EnqueueOutcome.Duplicate;
            }

            _logger.LogInformation("Job {JobId} ({JobType}) persisted for agent {AgentId}",
                dispatch.JobId, dispatch.JobType, agentId);
            return EnqueueOutcome.Accepted;
        }
    }

    /// <summary>
    /// Dequeue jobs for an agent and mark them delivered. Called by Agent poll.
    /// Serves pending jobs plus delivered-but-unresulted jobs older than the
    /// redelivery grace (agent crash/reconnect — at-least-once redelivery).
    /// </summary>
    public IReadOnlyList<QueuedJob> Dequeue(string agentId)
        => DequeueCore(agentId, ignoreRedeliveryGrace: false);

    /// <summary>
    /// Dequeue every undelivered AND unresulted job for an agent, ignoring the
    /// redelivery grace. Used on relay startup to resume work that cannot be
    /// in flight anymore (e.g. psremote jobs interrupted by the restart).
    /// </summary>
    public IReadOnlyList<QueuedJob> DequeueForResume(string agentId)
        => DequeueCore(agentId, ignoreRedeliveryGrace: true);

    private IReadOnlyList<QueuedJob> DequeueCore(string agentId, bool ignoreRedeliveryGrace)
    {
        lock (_gate)
        {
            var staleBefore = DateTimeOffset.UtcNow.Subtract(_redeliveryGrace).ToString("O");
            var jobs = new List<QueuedJob>();

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = ignoreRedeliveryGrace
                    ? """
                      SELECT job_id, agent_id, job_type, payload_json, idempotency_key, traceparent
                      FROM jobs
                      WHERE agent_id = @agentId AND status IN ('pending', 'delivered')
                      ORDER BY enqueued_at;
                      """
                    : """
                      SELECT job_id, agent_id, job_type, payload_json, idempotency_key, traceparent
                      FROM jobs
                      WHERE agent_id = @agentId
                        AND (status = 'pending'
                             OR (status = 'delivered' AND delivered_at < @staleBefore))
                      ORDER BY enqueued_at;
                      """;
                cmd.Parameters.AddWithValue("@agentId", agentId);
                if (!ignoreRedeliveryGrace)
                    cmd.Parameters.AddWithValue("@staleBefore", staleBefore);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    jobs.Add(new QueuedJob(
                        JobId:          Guid.Parse(reader.GetString(0)),
                        AgentId:        reader.GetString(1),
                        JobType:        reader.GetString(2),
                        PayloadJson:    reader.GetString(3),
                        IdempotencyKey: reader.IsDBNull(4) ? null : reader.GetString(4),
                        Traceparent:    reader.IsDBNull(5) ? null : reader.GetString(5)));
                }
            }

            if (jobs.Count > 0)
            {
                var now = DateTimeOffset.UtcNow.ToString("O");
                foreach (var job in jobs)
                {
                    using var update = _db.CreateCommand();
                    update.CommandText =
                        "UPDATE jobs SET status = 'delivered', delivered_at = @now WHERE job_id = @jobId";
                    update.Parameters.AddWithValue("@now", now);
                    update.Parameters.AddWithValue("@jobId", job.JobId.ToString("D"));
                    update.ExecuteNonQuery();
                }
            }

            return jobs;
        }
    }

    /// <summary>
    /// Mark a job completed when the agent (or PSRemote path) reports a result.
    /// Returns false if the jobId is unknown or already completed.
    /// </summary>
    public bool CompleteJob(Guid jobId, bool succeeded)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE jobs SET status = 'completed', completed_at = @now
                WHERE job_id = @jobId AND status <> 'completed';
                """;
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@jobId", jobId.ToString("D"));

            if (cmd.ExecuteNonQuery() == 0)
            {
                _logger.LogWarning("Job result received for unknown/already-completed jobId={JobId}", jobId);
                return false;
            }

            _logger.LogInformation("Job {JobId} completed succeeded={Succeeded}", jobId, succeeded);
            return true;
        }
    }

    /// <summary>Number of pending (undelivered) jobs for an agent — diagnostics/tests.</summary>
    public int PendingCount(string agentId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM jobs WHERE agent_id = @agentId AND status = 'pending'";
            cmd.Parameters.AddWithValue("@agentId", agentId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void Dispose() => _db.Dispose();
}
