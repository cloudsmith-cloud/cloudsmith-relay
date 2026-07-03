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
/// job_results(
///   job_id       TEXT PRIMARY KEY,      -- idempotent on jobId (contract §4.3)
///   succeeded    INTEGER NOT NULL,
///   exit_code    INTEGER NOT NULL,
///   output       TEXT NOT NULL,
///   error        TEXT NULL,
///   completed_at TEXT NOT NULL,
///   forwarded    INTEGER NOT NULL,      -- 0 until the job.result frame reaches PaaS
///   received_at  TEXT NOT NULL
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
            CREATE TABLE IF NOT EXISTS job_results (
                job_id       TEXT    NOT NULL PRIMARY KEY,
                succeeded    INTEGER NOT NULL,
                exit_code    INTEGER NOT NULL,
                output       TEXT    NOT NULL,
                error        TEXT    NULL,
                completed_at TEXT    NOT NULL,
                forwarded    INTEGER NOT NULL DEFAULT 0,
                received_at  TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_job_results_forwarded ON job_results(forwarded);
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
                      ORDER BY enqueued_at, rowid;
                      """
                    : """
                      SELECT job_id, agent_id, job_type, payload_json, idempotency_key, traceparent
                      FROM jobs
                      WHERE agent_id = @agentId
                        AND (status = 'pending'
                             OR (status = 'delivered' AND delivered_at < @staleBefore))
                      ORDER BY enqueued_at, rowid;
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
    /// Record a job result and mark the job completed. The result is durably
    /// persisted (forwarded = 0) so it survives a relay restart and is replayed
    /// upstream on reconnect (AB#4841 / contract §6.2). Idempotent on jobId —
    /// a second result for the same job is a no-op and returns false.
    /// Returns true if the result was newly recorded.
    /// </summary>
    public bool CompleteJob(JobResult result)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");

            using (var insert = _db.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO job_results (job_id, succeeded, exit_code, output, error,
                                             completed_at, forwarded, received_at)
                    VALUES (@jobId, @succeeded, @exitCode, @output, @error, @completedAt, 0, @now)
                    ON CONFLICT(job_id) DO NOTHING;
                    """;
                insert.Parameters.AddWithValue("@jobId", result.JobId.ToString("D"));
                insert.Parameters.AddWithValue("@succeeded", result.Succeeded ? 1 : 0);
                insert.Parameters.AddWithValue("@exitCode", result.ExitCode);
                insert.Parameters.AddWithValue("@output", result.Output);
                insert.Parameters.AddWithValue("@error", (object?)result.Error ?? DBNull.Value);
                insert.Parameters.AddWithValue("@completedAt", result.CompletedAt.ToString("O"));
                insert.Parameters.AddWithValue("@now", now);

                if (insert.ExecuteNonQuery() == 0)
                {
                    _logger.LogInformation("Duplicate result for job {JobId} — already recorded", result.JobId);
                    return false;
                }
            }

            using (var update = _db.CreateCommand())
            {
                update.CommandText = """
                    UPDATE jobs SET status = 'completed', completed_at = @now
                    WHERE job_id = @jobId AND status <> 'completed';
                    """;
                update.Parameters.AddWithValue("@now", now);
                update.Parameters.AddWithValue("@jobId", result.JobId.ToString("D"));
                if (update.ExecuteNonQuery() == 0)
                    _logger.LogWarning("Result recorded for job {JobId} not present in the jobs table", result.JobId);
            }

            _logger.LogInformation("Job {JobId} completed succeeded={Succeeded}; result queued for upstream forward",
                result.JobId, result.Succeeded);
            return true;
        }
    }

    /// <summary>
    /// Results not yet confirmed sent to PaaS, oldest first. Replayed by the
    /// forwarder whenever the WebSocket is (re)connected.
    /// </summary>
    public IReadOnlyList<JobResult> GetUnforwardedResults()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT job_id, succeeded, exit_code, output, error, completed_at
                FROM job_results WHERE forwarded = 0 ORDER BY received_at, rowid;
                """;

            var results = new List<JobResult>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new JobResult(
                    JobId:       Guid.Parse(reader.GetString(0)),
                    Succeeded:   reader.GetInt32(1) != 0,
                    ExitCode:    reader.GetInt32(2),
                    Output:      reader.GetString(3),
                    Error:       reader.IsDBNull(4) ? null : reader.GetString(4),
                    CompletedAt: DateTimeOffset.Parse(reader.GetString(5))));
            }
            return results;
        }
    }

    /// <summary>Mark a result as successfully forwarded to PaaS.</summary>
    public void MarkResultForwarded(Guid jobId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE job_results SET forwarded = 1 WHERE job_id = @jobId";
            cmd.Parameters.AddWithValue("@jobId", jobId.ToString("D"));
            cmd.ExecuteNonQuery();
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
