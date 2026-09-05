using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebServiceAlerter.Configuration;
using WebServiceAlerter.Probes;
using WebServiceAlerter.Security;

namespace WebServiceAlerter.Alerting;

/// <summary>
/// Publica los eventos en un canal de Discord compartido entre desarrolladores.
///
/// El propósito es distinto al del mail: el mail avisa al dueño del equipo, mientras que esto
/// arma una vista común del estado de los servicios en muchas instalaciones a la vez. Por eso la
/// instalación se identifica por localidad e ISP y nunca por el cliente.
///
/// Esa anonimización es estructural y no una convención: esta clase no recibe
/// <see cref="GeneralOptions"/>, así que no tiene forma de acceder al nombre de la instalación
/// aunque alguien lo intente más adelante. Tampoco publica el nombre del equipo —suele ser el de
/// la empresa— ni las URLs monitoreadas, que en un endpoint propio del cliente delatarían su
/// dominio.
/// </summary>
public sealed class DiscordAlertSender : IAlertSender
{
    private const int ColorDown = 0xEF4444;
    private const int ColorRecovered = 0x22C55E;
    private const int ColorStillDown = 0xEA580C;
    private const int ColorNeutral = 0x9CA3AF;

    /// <summary>Discord admite 25 campos por embed; bastante antes de eso deja de leerse.</summary>
    private const int MaxFields = 10;

    private readonly IOptionsMonitor<DiscordOptions> _options;
    private readonly ILogger<DiscordAlertSender> _logger;
    private readonly HttpClient _client;

    private readonly Queue<DateTimeOffset> _recentSends = new();
    private readonly object _gate = new();

    public DiscordAlertSender(IOptionsMonitor<DiscordOptions> options, ILogger<DiscordAlertSender> logger)
    {
        _options = options;
        _logger = logger;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public string Channel => "Discord";

    public async Task<bool> SendAsync(AlertEvent alert, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        if (!options.Enabled)
        {
            return false;
        }

        // Falla cerrado y a propósito: sin identidad el mensaje no sirve para nada en un canal
        // común, y la tentación sería caer al nombre de la instalación, que es justo lo que no
        // puede publicarse. Antes que publicar mal, no publicar.
        if (string.IsNullOrWhiteSpace(options.Identity))
        {
            _logger.LogWarning(
                "Discord está habilitado pero sin identidad (localidad e ISP): no se publica nada. " +
                "Configurala con --configure-discord.");
            return false;
        }

        if (!TryParseWebhook(ResolveWebhookUrl(options), out var webhook))
        {
            _logger.LogError(
                "Discord:WebhookUrl no parece un webhook de Discord. No se envía nada a una " +
                "dirección desconocida.");
            return false;
        }

        if (!TryReserveQuota(options.MaxMessagesPerHour))
        {
            _logger.LogWarning(
                "Límite de {Max} mensajes por hora a Discord alcanzado; se descarta «{Headline}».",
                options.MaxMessagesPerHour, alert.Headline);
            return false;
        }

        var payload = BuildPayload(alert, options.Identity);

        return await PostAsync(webhook, payload, cancellationToken);
    }

    private async Task<bool> PostAsync(Uri webhook, string payload, CancellationToken cancellationToken)
    {
        for (var intento = 1; intento <= 2; intento++)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _client.PostAsync(webhook, content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                // Discord limita por webhook y este canal recibe mensajes de muchas instalaciones
                // a la vez, así que el 429 no es un caso raro: es de esperar cuando se cae un
                // servicio que todos monitorean. Se respeta el tiempo que pide y se reintenta.
                if (response.StatusCode == HttpStatusCode.TooManyRequests && intento == 1)
                {
                    var espera = await ReadRetryAfterAsync(response, cancellationToken);
                    _logger.LogInformation("Discord pide esperar {Seconds:F1} s; reintento una vez.", espera.TotalSeconds);
                    await Task.Delay(espera, cancellationToken);
                    continue;
                }

                var cuerpo = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Discord respondió {Status}: {Body}", (int)response.StatusCode, Truncate(cuerpo, 300));
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "No pude publicar en Discord.");
                return false;
            }
        }

        return false;
    }

    private static async Task<TimeSpan> ReadRetryAfterAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        try
        {
            var cuerpo = await response.Content.ReadAsStringAsync(cancellationToken);
            if (JsonNode.Parse(cuerpo) is JsonObject json &&
                json["retry_after"] is JsonValue valor &&
                valor.TryGetValue<double>(out var segundos))
            {
                return TimeSpan.FromSeconds(Math.Clamp(segundos, 0.5, 30));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Sin dato utilizable: se usa la espera por defecto de abajo.
        }

        return TimeSpan.FromSeconds(3);
    }

    private string ResolveWebhookUrl(DiscordOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ProtectedWebhookUrl))
        {
            var descifrada = PasswordProtector.TryUnprotect(options.ProtectedWebhookUrl);
            if (descifrada is not null)
            {
                return descifrada;
            }

            _logger.LogError(
                "No pude descifrar Discord:ProtectedWebhookUrl. El blob DPAPI está atado a la " +
                "máquina que lo generó: si copiaste discord.json de otro equipo, regeneralo con " +
                "--configure-discord.");
        }

        return options.WebhookUrl;
    }

    /// <summary>
    /// Acepta sólo webhooks de Discord. Un error de tipeo en la configuración no puede terminar
    /// mandando el estado de los servidores de los clientes a un host cualquiera.
    /// </summary>
    public static bool IsValidWebhook(string value) => TryParseWebhook(value, out _);

    private static bool TryParseWebhook(string value, out Uri webhook)
    {
        webhook = null!;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var hostValido = uri.Host is "discord.com" or "discordapp.com" ||
                         uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase);

        if (!hostValido || !uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        webhook = uri;
        return true;
    }

    private static string BuildPayload(AlertEvent alert, string identity)
    {
        var (color, prefijo) = alert.Kind switch
        {
            AlertKind.Down => (ColorDown, "🔴"),
            AlertKind.Recovered => (ColorRecovered, "🟢"),
            AlertKind.StillDown => (ColorStillDown, "🟠"),
            _ => (ColorNeutral, "⚪"),
        };

        var fields = new JsonArray();

        foreach (var transition in alert.Transitions.Take(MaxFields))
        {
            var valor = new StringBuilder();
            valor.AppendLine(transition.Result.Outcome.ToSpanish());

            if (!string.IsNullOrWhiteSpace(transition.Result.Detail))
            {
                valor.AppendLine($"`{Truncate(transition.Result.Detail, 200)}`");
            }

            if (transition.Result.LatencyMs is { } ms)
            {
                valor.AppendLine($"latencia {ms:F0} ms");
            }

            if (transition.DownSince is { } desde)
            {
                valor.AppendLine($"caído desde {desde.ToLocalTime():HH:mm:ss}");
            }

            if (transition.Duration is { } duracion && alert.Kind != AlertKind.Down)
            {
                valor.AppendLine($"duró {AlertEvent.FormatDuration(duracion)}");
            }

            fields.Add(new JsonObject
            {
                // El nombre del endpoint y su grupo, nunca la URL: un endpoint propio del cliente
                // delataría su dominio en un canal compartido.
                ["name"] = Truncate(transition.Endpoint.Name, 250),
                ["value"] = Truncate(valor.ToString().TrimEnd(), 1000),
                ["inline"] = false,
            });
        }

        if (alert.Transitions.Count > MaxFields)
        {
            fields.Add(new JsonObject
            {
                ["name"] = "…",
                ["value"] = $"y {alert.Transitions.Count - MaxFields} servicio(s) más",
                ["inline"] = false,
            });
        }

        if (!string.IsNullOrWhiteSpace(alert.Note))
        {
            fields.Add(new JsonObject
            {
                ["name"] = "Nota",
                ["value"] = Truncate(alert.Note!, 1000),
                ["inline"] = false,
            });
        }

        var embed = new JsonObject
        {
            ["title"] = Truncate($"{prefijo} {alert.KindLabel} — {alert.Headline}", 250),
            ["color"] = color,
            ["timestamp"] = alert.At.ToUniversalTime().ToString("o"),
            ["footer"] = new JsonObject { ["text"] = Truncate(identity, 2000) },
        };

        if (fields.Count > 0)
        {
            embed["fields"] = fields;
        }

        var payload = new JsonObject
        {
            ["username"] = "WebServiceAlerter",
            ["embeds"] = new JsonArray(embed),
        };

        return payload.ToJsonString();
    }

    private bool TryReserveQuota(int maxPerHour)
    {
        lock (_gate)
        {
            var corte = DateTimeOffset.UtcNow.AddHours(-1);
            while (_recentSends.Count > 0 && _recentSends.Peek() < corte)
            {
                _recentSends.Dequeue();
            }

            if (_recentSends.Count >= Math.Max(1, maxPerHour))
            {
                return false;
            }

            _recentSends.Enqueue(DateTimeOffset.UtcNow);
            return true;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
