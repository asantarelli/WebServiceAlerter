using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebServiceAlerter.Configuration;
using WebServiceAlerter.Monitoring;
using WebServiceAlerter.Probes;
using WebServiceAlerter.Security;

namespace WebServiceAlerter.Alerting;

/// <summary>
/// Avisa por mail al dueño del equipo. A diferencia de Discord, acá SÍ corresponde identificar la
/// instalación por su nombre: el destinatario es quien la administra.
/// </summary>
public sealed class SmtpAlertSender : IAlertSender
{
    private readonly SmtpOptions _smtp;
    private readonly IOptionsMonitor<GeneralOptions> _general;
    private readonly IOptionsMonitor<AlertingOptions> _alerting;
    private readonly ILogger<SmtpAlertSender> _logger;

    /// <summary>Marcas de tiempo de los últimos envíos, para el tope por hora. Un bug o un
    /// endpoint inestable no pueden quemar la casilla compartida y hacer que el proveedor la
    /// bloquee.</summary>
    private readonly Queue<DateTimeOffset> _recentSends = new();
    private readonly object _gate = new();

    public SmtpAlertSender(
        IOptions<SmtpOptions> smtp,
        IOptionsMonitor<GeneralOptions> general,
        IOptionsMonitor<AlertingOptions> alerting,
        ILogger<SmtpAlertSender> logger)
    {
        _smtp = smtp.Value;
        _general = general;
        _alerting = alerting;
        _logger = logger;
    }

    public string Channel => "mail";

    public async Task<bool> SendAsync(AlertEvent alert, CancellationToken cancellationToken)
    {
        var recipients = _alerting.CurrentValue.ParsedRecipients();

        if (!_smtp.IsConfigured)
        {
            _logger.LogWarning("SMTP no configurado: «{Headline}» no se envió por mail.", alert.Headline);
            return false;
        }

        if (recipients.Count == 0)
        {
            _logger.LogWarning(
                "No hay destinatarios configurados: «{Headline}» no se envió. " +
                "Cargalos desde la pantalla de configuración.", alert.Headline);
            return false;
        }

        if (!TryReserveQuota())
        {
            _logger.LogError(
                "Límite de {Max} mails por hora alcanzado; se descarta «{Headline}». " +
                "Esto casi siempre indica un endpoint inestable o un error de configuración.",
                _alerting.CurrentValue.MaxMailsPerHour, alert.Headline);
            return false;
        }

        var (subject, body) = Render(alert);
        var password = ResolvePassword();

        for (var attempt = 1; attempt <= Math.Max(1, _smtp.RetryCount); attempt++)
        {
            try
            {
                using var client = new SmtpClient(_smtp.Host, _smtp.Port)
                {
                    EnableSsl = _smtp.UseSsl,
                    Timeout = _smtp.TimeoutMilliseconds,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Credentials = string.IsNullOrEmpty(_smtp.Username)
                        ? null
                        : new NetworkCredential(_smtp.Username, password),
                };

                using var mail = new MailMessage
                {
                    From = new MailAddress(_smtp.FromAddress, _smtp.FromDisplayName),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = false,
                };

                foreach (var recipient in recipients)
                {
                    mail.To.Add(recipient);
                }

                await client.SendMailAsync(mail, cancellationToken);
                _logger.LogInformation("Alerta enviada a {Count} destinatario(s): {Subject}", recipients.Count, subject);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Intento {Attempt} de envío falló.", attempt);

                if (attempt < _smtp.RetryCount)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_smtp.RetryBackoffSeconds), cancellationToken);
                }
            }
        }

        _logger.LogError("No se pudo enviar «{Subject}» tras {Count} intentos.", subject, _smtp.RetryCount);
        return false;
    }

    private (string Subject, string Body) Render(AlertEvent alert)
    {
        var site = _general.CurrentValue.SiteName;
        var prefijo = string.IsNullOrWhiteSpace(site) ? "" : $"[{site}] ";
        var subject = $"{prefijo}{alert.KindLabel}: {alert.Headline}";

        var body = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(site))
        {
            body.AppendLine($"Instalación: {site}");
        }

        body.AppendLine($"Equipo: {Environment.MachineName}");
        body.AppendLine($"Fecha: {alert.At.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        body.AppendLine();

        foreach (var transition in alert.Transitions)
        {
            body.AppendLine($"— {transition.Endpoint.Name}");
            body.AppendLine($"  URL: {transition.Endpoint.Url}");
            body.AppendLine($"  Estado: {alert.KindLabel}");
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

            if (transition.Duration is { } duration && alert.Kind != AlertKind.Down)
            {
                body.AppendLine($"  Duración: {AlertEvent.FormatDuration(duration)}");
            }

            body.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(alert.Note))
        {
            body.AppendLine(alert.Note);
            body.AppendLine();
        }

        if (alert.Transitions.Count > 0)
        {
            // El veredicto que el destinatario realmente busca. Sin decirlo explícitamente, cada
            // alerta termina igual en un llamado preguntando si es "nuestro o de ellos".
            body.AppendLine("La conexión a internet de este equipo estaba funcionando cuando se detectó el problema,");
            body.AppendLine("así que la falla es del servicio monitoreado y no de la red local.");
        }

        return (subject, body.ToString());
    }

    private string ResolvePassword()
    {
        if (!string.IsNullOrEmpty(_smtp.ProtectedPassword))
        {
            var unprotected = PasswordProtector.TryUnprotect(_smtp.ProtectedPassword);
            if (unprotected is not null)
            {
                return unprotected;
            }

            _logger.LogError(
                "No pude descifrar Smtp:ProtectedPassword. El blob DPAPI está atado a la máquina " +
                "que lo generó: si copiaste la configuración de otro equipo, regeneralo con " +
                "--protect-password.");
        }

        return _smtp.Password;
    }

    private bool TryReserveQuota()
    {
        lock (_gate)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
            while (_recentSends.Count > 0 && _recentSends.Peek() < cutoff)
            {
                _recentSends.Dequeue();
            }

            if (_recentSends.Count >= _alerting.CurrentValue.MaxMailsPerHour)
            {
                return false;
            }

            _recentSends.Enqueue(DateTimeOffset.UtcNow);
            return true;
        }
    }
}
