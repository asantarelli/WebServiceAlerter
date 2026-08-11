using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebServiceAlerter.Configuration;
using WebServiceAlerter.Probes;

namespace WebServiceAlerter.Data;

public sealed record UptimeSummary(string EndpointId, int Samples, int UpSamples)
{
    /// <summary>Null when nothing verifiable was recorded — better than reporting a confident 0%
    /// or 100% built on no data.</summary>
    public double? Percent => Samples == 0 ? null : (double)UpSamples / Samples;
}

/// <summary>
/// Owns the SQLite file: one row per check in Samples, one row per confirmed outage in Incidents.
/// Every write is best-effort — a database problem is logged and must never take monitoring down.
/// </summary>
public sealed class DataRecorder : IDisposable
{
    private readonly DatabaseOptions _options;
    private readonly ILogger<DataRecorder> _logger;
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private DateOnly _lastPurgeDay;

    public DataRecorder(IOptions<DatabaseOptions> options, ILogger<DataRecorder> logger)
    {
        _options = options.Value;
        _logger = logger;

        try
        {
            Initialize();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No pude abrir la base de datos; el registro histórico queda deshabilitado.");
            InitializationError = ex.Message;
            _connection = null;
        }
    }

    public bool IsAvailable => _connection is not null;

    public string? InitializationError { get; private set; }

    public string DatabasePath => _options.GetExpandedPath();

    private void Initialize()
    {
        var path = _options.GetExpandedPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS Samples (
                Id         INTEGER PRIMARY KEY,
                Timestamp  INTEGER NOT NULL,   -- unix seconds, UTC
                EndpointId TEXT    NOT NULL,
                LatencyMs  REAL,               -- NULL when it did not answer
                Outcome    INTEGER NOT NULL,
                HttpStatus INTEGER
            );
            CREATE INDEX IF NOT EXISTS IX_Samples_Endpoint_Time ON Samples(EndpointId, Timestamp);

            CREATE TABLE IF NOT EXISTS Incidents (
                Id         INTEGER PRIMARY KEY,
                EndpointId TEXT    NOT NULL,
                StartedAt  INTEGER NOT NULL,
                ResolvedAt INTEGER,
                Outcome    INTEGER NOT NULL,
                Detail     TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Incidents_Time ON Incidents(StartedAt);
            """;
        cmd.ExecuteNonQuery();

        _logger.LogInformation("Base de datos lista en {Path} (retención {Days} días).", path, _options.RetentionDays);
    }

    public void RecordSample(ProbeResult result)
    {
        if (_connection is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO Samples (Timestamp, EndpointId, LatencyMs, Outcome, HttpStatus)
                    VALUES ($ts, $id, $latency, $outcome, $status)
                    """;
                cmd.Parameters.AddWithValue("$ts", result.TimestampUtc.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$id", result.EndpointId);
                cmd.Parameters.AddWithValue("$latency", (object?)result.LatencyMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$outcome", (int)result.Outcome);
                cmd.Parameters.AddWithValue("$status", (object?)result.HttpStatus ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No pude registrar la muestra de {Endpoint}.", result.EndpointId);
            }

            PurgeIfDue();
        }
    }

    public void RecordIncidentStart(string endpointId, DateTimeOffset startedAt, ProbeOutcome outcome, string? detail)
    {
        Execute("""
            INSERT INTO Incidents (EndpointId, StartedAt, Outcome, Detail)
            VALUES ($id, $ts, $outcome, $detail)
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", endpointId);
                cmd.Parameters.AddWithValue("$ts", startedAt.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$outcome", (int)outcome);
                cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            },
            $"No pude registrar el inicio del incidente de {endpointId}.");
    }

    public void RecordIncidentResolved(string endpointId, DateTimeOffset resolvedAt)
    {
        Execute("""
            UPDATE Incidents SET ResolvedAt = $ts
            WHERE Id = (
                SELECT Id FROM Incidents
                WHERE EndpointId = $id AND ResolvedAt IS NULL
                ORDER BY StartedAt DESC LIMIT 1
            )
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$ts", resolvedAt.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$id", endpointId);
            },
            $"No pude cerrar el incidente de {endpointId}.");
    }

    /// <summary>
    /// Uptime over a window, excluding NotVerifiable samples: a power cut at the client's office
    /// must not show up as the monitored service having been unavailable.
    /// </summary>
    public UptimeSummary GetUptime(string endpointId, DateTimeOffset from)
    {
        if (_connection is null)
        {
            return new UptimeSummary(endpointId, 0, 0);
        }

        lock (_gate)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    SELECT
                        COUNT(*),
                        SUM(CASE WHEN Outcome IN ($ok, $slow) THEN 1 ELSE 0 END)
                    FROM Samples
                    WHERE EndpointId = $id AND Timestamp >= $from AND Outcome <> $notVerifiable
                    """;
                cmd.Parameters.AddWithValue("$id", endpointId);
                cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$ok", (int)ProbeOutcome.Ok);
                cmd.Parameters.AddWithValue("$slow", (int)ProbeOutcome.Slow);
                cmd.Parameters.AddWithValue("$notVerifiable", (int)ProbeOutcome.NotVerifiable);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var total = reader.GetInt32(0);
                    var up = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    return new UptimeSummary(endpointId, total, up);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No pude calcular el uptime de {Endpoint}.", endpointId);
            }

            return new UptimeSummary(endpointId, 0, 0);
        }
    }

    private void Execute(string sql, Action<SqliteCommand> bind, string errorMessage)
    {
        if (_connection is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                bind(cmd);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Message}", errorMessage);
            }
        }
    }

    private void PurgeIfDue()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _lastPurgeDay || _connection is null)
        {
            return;
        }

        _lastPurgeDay = today;

        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays).ToUnixTimeSeconds();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Samples WHERE Timestamp < $cutoff; DELETE FROM Incidents WHERE StartedAt < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            var removed = cmd.ExecuteNonQuery();

            if (removed > 0)
            {
                _logger.LogInformation("Purgadas {Rows} filas con más de {Days} días.", removed, _options.RetentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falló la purga por retención.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
