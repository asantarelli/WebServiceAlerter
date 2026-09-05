using WebServiceAlerter.Monitoring;

namespace WebServiceAlerter.Alerting;

public enum AlertKind
{
    Down,
    Recovered,
    StillDown,
    ServiceStarted,
    Test,
}

/// <summary>
/// Un aviso, sin redactar.
///
/// Viaja estructurado y no como texto ya armado porque cada canal se identifica distinto: el mail
/// va al cliente y lleva el nombre de su instalación, mientras que Discord va a un canal
/// compartido entre desarrolladores donde el cliente NO debe poder identificarse. Si el texto se
/// armara una sola vez, mantener esa diferencia dependería de acordarse de borrar campos en el
/// lugar correcto; así, cada sender sólo puede escribir lo que su propia configuración le da.
/// </summary>
public sealed record AlertEvent
{
    public required AlertKind Kind { get; init; }
    public required DateTimeOffset At { get; init; }

    /// <summary>Transiciones que originaron el aviso. Vacío en avisos que no vienen de una.</summary>
    public IReadOnlyList<Transition> Transitions { get; init; } = [];

    /// <summary>Título y texto para avisos sueltos (prueba, arranque), sin transiciones detrás.</summary>
    public string? Title { get; init; }

    public string? Note { get; init; }

    /// <summary>Resumen corto: el nombre del servicio si es uno solo, o cuántos son.</summary>
    public string Headline =>
        Transitions.Count switch
        {
            0 => Title ?? "WebServiceAlerter",
            1 => Transitions[0].Endpoint.Name,
            var n => $"{n} servicios",
        };

    public string KindLabel => Kind switch
    {
        AlertKind.Down => "CAÍDO",
        AlertKind.Recovered => "RECUPERADO",
        AlertKind.StillDown => "SIGUE CAÍDO",
        AlertKind.ServiceStarted => "MONITOR INICIADO",
        _ => "PRUEBA",
    };

    public static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" :
        span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" :
        $"{span.Seconds}s";
}

public interface IAlertSender
{
    /// <summary>Nombre del canal, para los mensajes de log.</summary>
    string Channel { get; }

    Task<bool> SendAsync(AlertEvent alert, CancellationToken cancellationToken);
}
