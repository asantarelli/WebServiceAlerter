using Microsoft.Extensions.Logging;

namespace WebServiceAlerter.Alerting;

/// <summary>
/// Manda el aviso por todos los canales configurados.
///
/// Cada canal falla por su cuenta: que Discord esté caído no puede impedir que salga el mail al
/// cliente, ni al revés. Se informa éxito si al menos uno llegó.
/// </summary>
public sealed class CompositeAlertSender : IAlertSender
{
    private readonly IReadOnlyList<IAlertSender> _senders;
    private readonly ILogger<CompositeAlertSender> _logger;

    public CompositeAlertSender(IEnumerable<IAlertSender> senders, ILogger<CompositeAlertSender> logger)
    {
        // Excluirse a sí mismo evita una recursión infinita si alguien lo registra como IAlertSender.
        _senders = senders.Where(s => s is not CompositeAlertSender).ToList();
        _logger = logger;
    }

    public string Channel => "todos";

    public async Task<bool> SendAsync(AlertEvent alert, CancellationToken cancellationToken)
    {
        var enviado = false;

        foreach (var sender in _senders)
        {
            try
            {
                enviado |= await sender.SendAsync(alert, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "El canal {Channel} falló al enviar «{Headline}».", sender.Channel, alert.Headline);
            }
        }

        return enviado;
    }
}
