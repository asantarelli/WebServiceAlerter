using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebServiceAlerter.Configuration;
using WebServiceAlerter.Security;

namespace WebServiceAlerter.Alerting;

/// <summary>
/// Sends through the fixed SDigitales mailbox. The client owns the recipient list and nothing
/// else — they never see or set the sending account.
/// </summary>
public sealed class SmtpAlertSender : IAlertSender
{
    private readonly SmtpOptions _smtp;
    private readonly IOptionsMonitor<AlertingOptions> _alerting;
    private readonly ILogger<SmtpAlertSender> _logger;

    /// <summary>Timestamps of recent sends, for the hourly cap. A bug or a pathological flap must
    /// not be able to burn the shared mailbox and get it blocked by the provider.</summary>
    private readonly Queue<DateTimeOffset> _recentSends = new();
    private readonly object _gate = new();

    public SmtpAlertSender(
        IOptions<SmtpOptions> smtp,
        IOptionsMonitor<AlertingOptions> alerting,
        ILogger<SmtpAlertSender> logger)
    {
        _smtp = smtp.Value;
        _alerting = alerting;
        _logger = logger;
    }

    public async Task<bool> SendAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        var recipients = _alerting.CurrentValue.ParsedRecipients();

        if (!_smtp.IsConfigured)
        {
            _logger.LogWarning("SMTP no configurado: la alerta «{Subject}» no se envió por mail.", message.Subject);
            return false;
        }

        if (recipients.Count == 0)
        {
            _logger.LogWarning(
                "No hay destinatarios configurados: la alerta «{Subject}» no se envió. " +
                "Cargá Alerting:Recipients en usersettings.json.", message.Subject);
            return false;
        }

        if (!TryReserveQuota())
        {
            _logger.LogError(
                "Límite de {Max} mails por hora alcanzado; se descarta «{Subject}». " +
                "Esto casi siempre indica un endpoint inestable o un error de configuración.",
                _alerting.CurrentValue.MaxMailsPerHour, message.Subject);
            return false;
        }

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
                    Subject = message.Subject,
                    Body = message.Body,
                    IsBodyHtml = false,
                };

                foreach (var recipient in recipients)
                {
                    mail.To.Add(recipient);
                }

                await client.SendMailAsync(mail, cancellationToken);
                _logger.LogInformation("Alerta enviada a {Count} destinatario(s): {Subject}", recipients.Count, message.Subject);
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

        _logger.LogError("No se pudo enviar la alerta «{Subject}» tras {Count} intentos.", message.Subject, _smtp.RetryCount);
        return false;
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
                "que lo generó: si copiaste el appsettings.json de otro equipo, regeneralo con " +
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
