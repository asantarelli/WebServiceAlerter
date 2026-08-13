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

    /// <summary>
    /// Everything recorded for one endpoint since <paramref name="from"/>, grouped by outcome.
    /// This is what makes the stored history usable: when the user says "no pude facturar a las
    /// diez y cuarto", this is how you find out what the monitor actually saw at that moment
    /// instead of arguing from memory.
    /// </summary>
    public IReadOnlyList<(string EndpointId, ProbeOutcome Outcome, int Count)> GetOutcomeCounts(DateTimeOffset from)
    {
        var result = new List<(string, ProbeOutcome, int)>();

        Query("""
            SELECT EndpointId, Outcome, COUNT(*)
            FROM Samples WHERE Timestamp >= $from
            GROUP BY EndpointId, Outcome
            ORDER BY EndpointId, Outcome
            """,
            cmd => cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds()),
            reader => result.Add((reader.GetString(0), (ProbeOutcome)reader.GetInt32(1), reader.GetInt32(2))),
            "No pude leer el resumen de muestras.");

        return result;
    }

    /// <summary>Individual failed checks, newest first — the timeline to line up against the
    /// moment the user reports.</summary>
    public IReadOnlyList<(DateTimeOffset At, string EndpointId, ProbeOutcome Outcome, double? LatencyMs)> GetFailures(
        DateTimeOffset from, int limit)
    {
        var result = new List<(DateTimeOffset, string, ProbeOutcome, double?)>();

        Query($"""
            SELECT Timestamp, EndpointId, Outcome, LatencyMs
            FROM Samples
            WHERE Timestamp >= $from AND Outcome NOT IN ({(int)ProbeOutcome.Ok}, {(int)ProbeOutcome.Slow})
            ORDER BY Timestamp DESC LIMIT $limit
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$limit", limit);
            },
            reader => result.Add((
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                reader.GetString(1),
                (ProbeOutcome)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3))),
            "No pude leer las muestras fallidas.");

        return result;
    }

    public IReadOnlyList<(string EndpointId, DateTimeOffset StartedAt, DateTimeOffset? ResolvedAt, ProbeOutcome Outcome, string? Detail)> GetIncidents(
        DateTimeOffset from)
    {
        var result = new List<(string, DateTimeOffset, DateTimeOffset?, ProbeOutcome, string?)>();

        Query("""
            SELECT EndpointId, StartedAt, ResolvedAt, Outcome, Detail
            FROM Incidents WHERE StartedAt >= $from
            ORDER BY StartedAt DESC
            """,
            cmd => cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds()),
            reader => result.Add((
                reader.GetString(0),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                (ProbeOutcome)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4))),
            "No pude leer los incidentes.");

        return result;
    }

    /// <summary>
    /// Latencias de las respuestas correctas, ordenadas. Sirve para responder "¿cuán lento es
    /// lento?" con números en vez de con impresiones: un puñado de respuestas de varios segundos
    /// cambia por completo cómo hay que escalar un gráfico.
    /// </summary>
    public IReadOnlyList<double> GetLatencies(string endpointId, DateTimeOffset from)
    {
        var result = new List<double>();

        Query($"""
            SELECT LatencyMs FROM Samples
            WHERE EndpointId = $id AND Timestamp >= $from AND LatencyMs IS NOT NULL
              AND Outcome IN ({(int)ProbeOutcome.Ok}, {(int)ProbeOutcome.Slow})
            ORDER BY LatencyMs
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", endpointId);
                cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            },
            reader => result.Add(reader.GetDouble(0)),
            "No pude leer las latencias.");

        return result;
    }

    private void Query(string sql, Action<SqliteCommand> bind, Action<SqliteDataReader> read, string errorMessage)
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
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    read(reader);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Message}", errorMessage);
            }
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
