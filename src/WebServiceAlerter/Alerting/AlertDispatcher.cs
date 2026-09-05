using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebServiceAlerter.Configuration;
using WebServiceAlerter.Monitoring;

namespace WebServiceAlerter.Alerting;

/// <summary>
/// Convierte los cambios de estado en, como mucho, un aviso por ciclo y por tipo.
///
/// Agrupar no es un detalle. El proyecto existe porque una caída de ARCA produce hoy una avalancha
/// de reportes simultáneos; un monitor que respondiera a esa caída con un aviso por endpoint sólo
/// movería la avalancha de lugar.
///
/// El aviso sale sin redactar: cada canal lo escribe con su propia identidad (ver AlertEvent).
/// </summary>
public sealed class AlertDispatcher
{
    private readonly IAlertSender _sender;
    private readonly IOptionsMonitor<AlertingOptions> _alerting;
    private readonly ILogger<AlertDispatcher> _logger;

    public AlertDispatcher(
        IAlertSender sender,
        IOptionsMonitor<AlertingOptions> alerting,
        ILogger<AlertDispatcher> logger)
    {
        _sender = sender;
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
            var alert = new AlertEvent
            {
                Kind = group.Key switch
                {
                    TransitionKind.WentDown => AlertKind.Down,
                    TransitionKind.Recovered => AlertKind.Recovered,
                    _ => AlertKind.StillDown,
                },
                At = DateTimeOffset.Now,
                Transitions = group.ToList(),
            };

            await _sender.SendAsync(alert, cancellationToken);
        }
    }
}
