// Copyright 2026 CloudSmith Contributors
// SPDX-License-Identifier: Apache-2.0

using CloudSmith.Relay.Interfaces;
using CloudSmith.Relay.Models;
using CloudSmith.Relay.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CloudSmith.Relay.Lan;

/// <summary>
/// SQLite-backed <see cref="IAgentRegistry"/> that survives relay restarts.
///
/// Schema (single table):
/// <code>
/// agents(
///   agent_id       TEXT PRIMARY KEY,
///   host_id        TEXT NOT NULL,
///   hostname       TEXT NOT NULL,
///   enrolled_at    TEXT NOT NULL,   -- ISO-8601
///   last_seen_at   TEXT NOT NULL    -- ISO-8601
/// )
/// </code>
///
/// Secrets are NOT stored — each enroll issues a per-agent JWT (signed by the
/// relay's RSA key).  The JWT is validated on every request; only the relay's
/// public key is needed to verify, and that key is already in the relay identity
/// directory.
///
/// F-3 fix: per-agent JWTs replace the single shared RELAY_AGENT_ENROLLMENT_TOKEN
/// secret.  The enrollment token is still checked on enroll (site-scoped gate),
/// but subsequent requests present a JWT whose signature is cryptographically
/// bound to the specific agentId.
/// </summary>
public sealed class SqliteAgentRegistry : IAgentRegistry, IDisposable
{
    private readonly string _enrollmentToken;
    private readonly RelayJwtService _jwt;
    private readonly ILogger<SqliteAgentRegistry> _logger;
    private readonly SqliteConnection _db;

    // Default DB path inside the relay identity directory.
    public const string DefaultDbFileName = "agents.db";

    public SqliteAgentRegistry(
        string enrollmentToken,
        RelayJwtService jwt,
        string dbPath,
        ILogger<SqliteAgentRegistry> logger)
    {
        _enrollmentToken = enrollmentToken;
        _jwt = jwt;
        _logger = logger;

        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        EnsureSchema();
    }

    // ------------------------------------------------------------------
    // Schema
    // ------------------------------------------------------------------

    private void EnsureSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS agents (
                agent_id     TEXT    NOT NULL PRIMARY KEY,
                host_id      TEXT    NOT NULL,
                hostname     TEXT    NOT NULL,
                enrolled_at  TEXT    NOT NULL,
                last_seen_at TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // IAgentRegistry
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task RegisterAgentAsync(AgentEnrollmentRequest req, CancellationToken ct)
        => throw new NotSupportedException(
            "Use EnrollAsync on SqliteAgentRegistry — RegisterAgentAsync is the legacy stub path.");

    /// <inheritdoc />
    public async Task<Agent?> GetAgentForHostAsync(string hostId, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            "SELECT agent_id, host_id, hostname, enrolled_at, last_seen_at " +
            "FROM agents WHERE host_id = @hostId LIMIT 1";
        cmd.Parameters.AddWithValue("@hostId", hostId);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return ReadAgent(reader);
    }

    /// <inheritdoc />
    public IEnumerable<Agent> ListAgents()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            "SELECT agent_id, host_id, hostname, enrolled_at, last_seen_at FROM agents";
        using var reader = cmd.ExecuteReader();
        var list = new List<Agent>();
        while (reader.Read()) list.Add(ReadAgent(reader));
        return list;
    }

    // ------------------------------------------------------------------
    // LAN controller surface
    // ------------------------------------------------------------------

    /// <summary>
    /// Validate the enrollment token, upsert the agent record, issue a per-agent JWT.
    /// Returns (agentId, jwt) — the JWT is what the agent presents on every subsequent request.
    /// </summary>
    public Task<(string AgentId, string AgentJwt)> EnrollAsync(AgentEnrollRequest req, CancellationToken ct)
    {
        if (req.EnrollmentToken != _enrollmentToken)
        {
            _logger.LogWarning("Agent enroll rejected — bad enrollment token from {Host}",
                req.HostInfo.ComputerName);
            throw new UnauthorizedAccessException("Invalid enrollment token.");
        }

        var host = req.HostInfo.ComputerName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("hostInfo.computerName is required.", nameof(req));

        var now = DateTimeOffset.UtcNow;
        var nowIso = now.ToString("O");

        string? agentId = null;

        // Re-enrollment must keep the existing agent_id for the same host so
        // queued jobs already targeted to that agent are still deliverable.
        using (var find = _db.CreateCommand())
        {
            find.CommandText = """
                SELECT agent_id
                FROM agents
                WHERE lower(host_id) = lower(@hostId)
                ORDER BY enrolled_at
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("@hostId", host);
            agentId = find.ExecuteScalar() as string;
        }

        if (!string.IsNullOrWhiteSpace(agentId))
        {
            using var update = _db.CreateCommand();
            update.CommandText = """
                UPDATE agents
                SET host_id = @hostId,
                    hostname = @hostname,
                    last_seen_at = @now
                WHERE agent_id = @agentId;
                """;
            update.Parameters.AddWithValue("@hostId", host);
            update.Parameters.AddWithValue("@hostname", host);
            update.Parameters.AddWithValue("@now", nowIso);
            update.Parameters.AddWithValue("@agentId", agentId);
            update.ExecuteNonQuery();
        }
        else
        {
            agentId = Guid.NewGuid().ToString("N");

            using var insert = _db.CreateCommand();
            insert.CommandText = """
                INSERT INTO agents (agent_id, host_id, hostname, enrolled_at, last_seen_at)
                VALUES (@agentId, @hostId, @hostname, @now, @now);
                """;
            insert.Parameters.AddWithValue("@agentId", agentId);
            insert.Parameters.AddWithValue("@hostId", host);
            insert.Parameters.AddWithValue("@hostname", host);
            insert.Parameters.AddWithValue("@now", nowIso);
            insert.ExecuteNonQuery();
        }

        var token = _jwt.IssueToken(agentId, siteId: null);
        _logger.LogInformation("Agent enrolled: agentId={AgentId} host={Host}", agentId, host);
        return Task.FromResult((agentId, token));
    }

    /// <summary>
    /// Update the last-seen timestamp. Returns false if the agentId is unknown.
    /// </summary>
    public bool Heartbeat(string agentId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            "UPDATE agents SET last_seen_at = @now WHERE agent_id = @agentId";
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@agentId", agentId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Validate a per-agent JWT. Returns false if the token is invalid, expired,
    /// or its <c>sub</c> claim does not match <paramref name="agentId"/>.
    /// </summary>
    public bool ValidateToken(string agentId, string token)
    {
        var sub = _jwt.ValidateToken(token);
        return sub is not null &&
               string.Equals(sub, agentId, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Agent ReadAgent(SqliteDataReader r) => new(
        AgentId:       r.GetString(0),
        HostId:        r.GetString(1),
        Hostname:      r.GetString(2),
        EnrolledAtUtc: DateTimeOffset.Parse(r.GetString(3)),
        LastSeenUtc:   DateTimeOffset.Parse(r.GetString(4)));

    public void Dispose() => _db.Dispose();
}
