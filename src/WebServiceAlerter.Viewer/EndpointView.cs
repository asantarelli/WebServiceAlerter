using WebServiceAlerter.Viewer.Data;

namespace WebServiceAlerter.Viewer;

/// <summary>
/// Un endpoint tal como se muestra: estado, la frase que lo explica y sus números. Acá se combina
/// lo que publica el servicio (status.json) con lo que surge del historial (fallos recientes),
/// porque ninguna de las dos fuentes sola alcanza para decir la verdad.
/// </summary>
public sealed record EndpointView
{
    /// <summary>Ventana para considerar que algo viene fallando de a ratos.</summary>
    public static readonly TimeSpan FlappingWindow = TimeSpan.FromMinutes(15);

    /// <summary>Si status.json es más viejo que esto, el servicio no está corriendo.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DisplayState State { get; init; }
    public required string Headline { get; init; }
    public string? Detail { get; init; }
    public double? LatencyMs { get; init; }
    public double? UptimePercent { get; init; }
    public int RecentFailures { get; init; }
    public int? CertificateDaysToExpiry { get; init; }
    public DateTimeOffset? Since { get; init; }

    public static EndpointView Build(StatusEndpoint endpoint, bool hasInternet, bool stale, HistoryReader history)
    {
        var recentFailures = history.CountRecentFailures(endpoint.Id, FlappingWindow);
        var stats = history.GetStats(endpoint.Id, TimeSpan.FromHours(24));

        var (state, headline) = Classify(endpoint, hasInternet, stale, recentFailures);

        return new EndpointView
        {
            Id = endpoint.Id,
            Name = endpoint.Name,
            State = state,
            Headline = headline,
            Detail = endpoint.Detail,
            LatencyMs = endpoint.LatencyMs,
            UptimePercent = stats.UptimePercent,
            RecentFailures = recentFailures,
            CertificateDaysToExpiry = endpoint.CertificateDaysToExpiry,
            Since = endpoint.Since,
        };
    }

    private static (DisplayState State, string Headline) Classify(
        StatusEndpoint endpoint, bool hasInternet, bool stale, int recentFailures)
    {
        // Orden deliberado: primero las razones por las que NO sabemos nada, después el estado
        // real. Mostrar un verde viejo es la peor respuesta posible.
        if (stale)
        {
            return (DisplayState.Unknown, "el monitor no está corriendo");
        }

        if (!hasInternet)
        {
            return (DisplayState.Unknown, "sin conexión a internet");
        }

        switch (endpoint.State?.ToLowerInvariant())
        {
            case "down":
                return (DisplayState.Down, "caído");

            case "degraded":
                return (DisplayState.Warning, "responde, pero lento");

            case "maintenance":
                return (DisplayState.Unknown, "en ventana de mantenimiento");

            case "offline":
                return (DisplayState.Unknown, "sin conexión a internet");

            case "ok":
                // Verde sólo si además no viene fallando de a ratos. Un servicio que falla una de
                // cada cinco veces no es un incidente, pero decirle al usuario que está todo bien
                // mientras no puede facturar es exactamente el problema que hay que evitar.
                return recentFailures > 0
                    ? (DisplayState.Warning, $"fallas intermitentes: {recentFailures} en los últimos {FlappingWindow.TotalMinutes:F0} min")
                    : (DisplayState.Ok, "funcionando normalmente");

            default:
                return (DisplayState.Unknown, "sin datos todavía");
        }
    }
}
