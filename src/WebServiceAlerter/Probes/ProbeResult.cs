namespace WebServiceAlerter.Probes;

/// <summary>
/// Why a check succeeded or failed. The classification is not decoration — it is the answer to
/// the question the whole program exists to answer: "is it my system or is it theirs?".
/// </summary>
public enum ProbeOutcome
{
    /// <summary>Answered correctly, within the latency budget.</summary>
    Ok,

    /// <summary>Answered correctly but slower than LatencyWarnMs. Degraded, not down.</summary>
    Slow,

    /// <summary>The name does not resolve.</summary>
    DnsFailure,

    /// <summary>TCP connection actively refused.</summary>
    ConnectionRefused,

    /// <summary>TLS handshake or certificate problem.</summary>
    TlsFailure,

    /// <summary>No answer within the timeout.</summary>
    Timeout,

    /// <summary>Answered with 4xx/5xx.</summary>
    HttpError,

    /// <summary>Answered 200 but the body is not what this endpoint is supposed to return —
    /// typically a captive portal or a proxy error page wearing a success status code.</summary>
    ContentMismatch,

    /// <summary>The service itself reports a subsystem as down (e.g. a SOAP dummy returning
    /// DbServer=ERROR). Unambiguously their side.</summary>
    ServiceReportedDown,

    /// <summary>Not checked, because there was no internet. Recorded so the chart shows the gap,
    /// but excluded from uptime: a local outage must not cost the monitored service any
    /// availability.</summary>
    NotVerifiable,
}

public static class ProbeOutcomeExtensions
{
    public static bool IsUp(this ProbeOutcome outcome) => outcome is ProbeOutcome.Ok or ProbeOutcome.Slow;

    public static bool CountsTowardUptime(this ProbeOutcome outcome) => outcome != ProbeOutcome.NotVerifiable;

    public static string ToSpanish(this ProbeOutcome outcome) => outcome switch
    {
        ProbeOutcome.Ok => "responde normalmente",
        ProbeOutcome.Slow => "responde lento",
        ProbeOutcome.DnsFailure => "el nombre del servidor no resuelve (DNS)",
        ProbeOutcome.ConnectionRefused => "el servidor rechaza la conexión",
        ProbeOutcome.TlsFailure => "falla el certificado o el cifrado (TLS)",
        ProbeOutcome.Timeout => "no responde dentro del tiempo de espera",
        ProbeOutcome.HttpError => "el servidor devuelve un código de error",
        ProbeOutcome.ContentMismatch => "responde, pero el contenido no es el esperado",
        ProbeOutcome.ServiceReportedDown => "el propio servicio informa que está caído",
        ProbeOutcome.NotVerifiable => "no verificable (sin conexión a internet)",
        _ => outcome.ToString(),
    };
}

public sealed record ProbeResult
{
    public required string EndpointId { get; init; }
    public required ProbeOutcome Outcome { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }

    public double? LatencyMs { get; init; }
    public int? HttpStatus { get; init; }

    /// <summary>Short human-readable detail, shown in the alert and in the UI.</summary>
    public string? Detail { get; init; }

    public int? CertificateDaysToExpiry { get; init; }

    public bool IsUp => Outcome.IsUp();

    public string DisplayValue => LatencyMs is { } ms ? $"{ms:F0} ms" : "sin respuesta";
}
