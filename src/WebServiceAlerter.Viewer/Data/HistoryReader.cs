using Microsoft.Data.Sqlite;

namespace WebServiceAlerter.Viewer.Data;

public sealed record LatencyPoint(DateTime At, double? LatencyMs, int Outcome);

public sealed record IncidentRow(string EndpointId, DateTime StartedAt, DateTime? ResolvedAt, int Outcome, string? Detail)
{
    public TimeSpan Duration => (ResolvedAt ?? DateTime.Now) - StartedAt;
    public bool IsOpen => ResolvedAt is null;
}

public sealed record EndpointStats(int Total, int Up, int Failures, int NotVerifiable)
{
    public int Verifiable => Total - NotVerifiable;
    public double? UptimePercent => Verifiable == 0 ? null : (double)Up / Verifiable;
}

/// <summary>
/// Lectura de la misma base SQLite que escribe el servicio. Sólo lectura, y en modo compartido:
/// el servicio sigue escribiendo mientras esta ventana consulta.
/// </summary>
public sealed class HistoryReader
{
    // Deben coincidir con ProbeOutcome del servicio.
    public const int OutcomeOk = 0;
    public const int OutcomeSlow = 1;
    public const int OutcomeNotVerifiable = 9;

    private readonly string _connectionString;

    public HistoryReader(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public string DatabasePath { get; }

    public bool Exists => File.Exists(DatabasePath);

    public IReadOnlyList<LatencyPoint> GetLatency(string endpointId, TimeSpan window)
    {
        var result = new List<LatencyPoint>();
        var from = DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds();

        Query("""
            SELECT Timestamp, LatencyMs, Outcome FROM Samples
            WHERE EndpointId = $id AND Timestamp >= $from
            ORDER BY Timestamp
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", endpointId);
                cmd.Parameters.AddWithValue("$from", from);
            },
            reader => result.Add(new LatencyPoint(
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).LocalDateTime,
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.GetInt32(2))));

        return result;
    }

    public EndpointStats GetStats(string endpointId, TimeSpan window)
    {
        var from = DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds();
        var total = 0;
        var up = 0;
        var notVerifiable = 0;

        Query($"""
            SELECT
                COUNT(*),
                SUM(CASE WHEN Outcome IN ({OutcomeOk}, {OutcomeSlow}) THEN 1 ELSE 0 END),
                SUM(CASE WHEN Outcome = {OutcomeNotVerifiable} THEN 1 ELSE 0 END)
            FROM Samples WHERE EndpointId = $id AND Timestamp >= $from
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", endpointId);
                cmd.Parameters.AddWithValue("$from", from);
            },
            reader =>
            {
                total = reader.GetInt32(0);
                up = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                notVerifiable = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            });

        return new EndpointStats(total, up, total - up - notVerifiable, notVerifiable);
    }

    /// <summary>
    /// Fallos sueltos recientes que nunca llegaron a ser incidente. De acá sale el estado
    /// amarillo "intermitente": un servicio que falla una de cada cinco veces no es un incidente,
    /// pero tampoco está bien, y sin esto la pantalla lo muestra en verde mientras el usuario no
    /// puede trabajar.
    /// </summary>
    public int CountRecentFailures(string endpointId, TimeSpan window)
    {
        var from = DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds();
        var count = 0;

        Query($"""
            SELECT COUNT(*) FROM Samples
            WHERE EndpointId = $id AND Timestamp >= $from
              AND Outcome NOT IN ({OutcomeOk}, {OutcomeSlow}, {OutcomeNotVerifiable})
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", endpointId);
                cmd.Parameters.AddWithValue("$from", from);
            },
            reader => count = reader.GetInt32(0));

        return count;
    }

    public IReadOnlyList<IncidentRow> GetIncidents(TimeSpan window)
    {
        var result = new List<IncidentRow>();
        var from = DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeSeconds();

        Query("""
            SELECT EndpointId, StartedAt, ResolvedAt, Outcome, Detail FROM Incidents
            WHERE StartedAt >= $from
            ORDER BY StartedAt DESC
            """,
            cmd => cmd.Parameters.AddWithValue("$from", from),
            reader => result.Add(new IncidentRow(
                reader.GetString(0),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).LocalDateTime,
                reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)).LocalDateTime,
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4))));

        return result;
    }

    private void Query(string sql, Action<SqliteCommand> bind, Action<SqliteDataReader> read)
    {
        if (!Exists)
        {
            return;
        }

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                read(reader);
            }
        }
        catch (SqliteException)
        {
            // La base puede no existir todavía, o estar bloqueada un instante. La ventana se
            // refresca sola: no vale la pena molestar al usuario con un cartel por eso.
        }
    }
}
