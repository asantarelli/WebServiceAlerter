using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebServiceAlerter.Configuration;
using WebServiceAlerter.Monitoring;
using WebServiceAlerter.Probes;

namespace WebServiceAlerter.Alerting;

/// <summary>
/// Turns state transitions into at most one mail per cycle.
///
/// Grouping is not a nicety. The reason this project exists is that an ARCA outage currently
/// produces a flood of simultaneous reports; a monitor that answers an outage with one mail per
/// endpoint would just move the flood somewhere else.
/// </summary>
public sealed class AlertDispatcher
{
    private readonly IAlertSender _sender;
    private readonly IOptions<GeneralOptions> _general;
    private readonly IOptionsMonitor<AlertingOptions> _alerting;
    private readonly ILogger<AlertDispatcher> _logger;

    public AlertDispatcher(
        IAlertSender sender,
        IOptions<GeneralOptions> general,
        IOptionsMonitor<AlertingOptions> alerting,
        ILogger<AlertDispatcher> logger)
    {
        _sender = sender;
        _general = general;
        _alerting = alerting;
        _logger = logger;
    }

    public async Task DispatchAsync(IReadOnlyList<Transition> transitions, bool online, CancellationToken cancellationToken)
    {
        var actionable = transitions.Where(t => t.Kind != TransitionKind.None).ToList();
        if (actionable.Count == 0)
        {
            return;
        }

        if (!online && _alerting.CurrentValue.SuppressWhenOffline)
        {
            _logger.LogInformation("Sin conexión: se omiten {Count} alertas (es un problema local).", actionable.Count);
            return;
        }

        foreach (var group in actionable.GroupBy(t => t.Kind))
        {
            var message = Build(group.Key, group.ToList());
            await _sender.SendAsync(message, cancellationToken);
        }
    }

    private AlertMessage Build(TransitionKind kind, IReadOnlyList<Transition> transitions)
    {
        var site = _general.Value.SiteName;
        var sitePrefix = string.IsNullOrWhiteSpace(site) ? "" : $"[{site}] ";

        var names = string.Join(", ", transitions.Select(t => t.Endpoint.Name));
        var headline = transitions.Count == 1
            ? names
            : $"{transitions.Count} servicios";

        var (subject, alertKind) = kind switch
        {
            TransitionKind.WentDown => ($"{sitePrefix}CAÍDO: {headline}", AlertKind.Down),
            TransitionKind.Recovered => ($"{sitePrefix}RECUPERADO: {headline}", AlertKind.Recovered),
            _ => ($"{sitePrefix}SIGUE CAÍDO: {headline}", AlertKind.StillDown),
        };

        var body = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(site))
        {
            body.AppendLine($"Instalación: {site}");
        }

        body.AppendLine($"Equipo: {Environment.MachineName}");
        body.AppendLine($"Fecha: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        body.AppendLine();

        foreach (var transition in transitions)
        {
            body.AppendLine($"— {transition.Endpoint.Name}");
            body.AppendLine($"  URL: {transition.Endpoint.Url}");
            body.AppendLine($"  Estado: {Describe(transition)}");
            body.AppendLine($"  Motivo: {transition.Result.Outcome.ToSpanish()}");

            if (!string.IsNullOrWhiteSpace(transition.Result.Detail))
            {
                body.AppendLine($"  Detalle: {transition.Result.Detail}");
            }

            if (transition.Result.LatencyMs is { } ms)
            {
                body.AppendLine($"  Latencia: {ms:F0} ms");
            }

            if (transition.DownSince is { } since)
            {
                body.AppendLine($"  Caído desde: {since.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            }

            if (transition.Duration is { } duration && kind != TransitionKind.WentDown)
            {
                body.AppendLine($"  Duración: {FormatDuration(duration)}");
            }

            body.AppendLine();
        }

        // The verdict the recipient actually wants. Without stating it explicitly, every alert
        // still ends in a phone call asking whether it is "us or them".
        body.AppendLine("La conexión a internet de este equipo estaba funcionando cuando se detectó el problema,");
        body.AppendLine("así que la falla es del servicio monitoreado y no de la red local.");

        return new AlertMessage
        {
            Kind = alertKind,
            Subject = subject,
            Body = body.ToString(),
        };
    }

    private static string Describe(Transition transition) => transition.Kind switch
    {
        TransitionKind.WentDown => "CAÍDO",
        TransitionKind.Recovered => "RECUPERADO",
        TransitionKind.StillDown => "SIGUE CAÍDO",
        _ => "sin cambios",
    };

    public static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" :
        span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" :
        $"{span.Seconds}s";
}
