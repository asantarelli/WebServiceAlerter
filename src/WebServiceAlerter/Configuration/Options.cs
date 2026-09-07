namespace WebServiceAlerter.Configuration;

public sealed class GeneralOptions
{
    public const string SectionName = "General";

    /// <summary>Shown in alert subjects so the recipient knows which installation is talking.</summary>
    public string SiteName { get; set; } = "";
}

public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    public int DefaultIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Random spread applied to each endpoint's interval. Without it, every installation out
    /// there ends up hitting the same service on the same tick — which is both rude and a good
    /// way to get an IP range throttled.
    /// </summary>
    public int JitterPercent { get; set; } = 20;

    public int FailuresToAlert { get; set; } = 3;
    public int SuccessesToRecover { get; set; } = 2;
    public int ReminderIntervalMinutes { get; set; } = 20;
    public int DefaultTimeoutMs { get; set; } = 10_000;
    public int DefaultLatencyWarnMs { get; set; } = 3_000;

    public CanaryOptions Canary { get; set; } = new();

    /// <summary>
    /// Keyed by endpoint id rather than an array on purpose: IConfiguration merges arrays by
    /// index (so usersettings.json would silently overwrite the wrong entry) but merges objects
    /// by key. Merge-by-id therefore comes for free, which is exactly the semantics we want when
    /// the client overrides one endpoint of a shipped profile.
    /// </summary>
    public Dictionary<string, EndpointOptions> Endpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CanaryOptions
{
    public bool Enabled { get; set; } = true;
    public List<string> Targets { get; set; } = new();
    public int TimeoutMs { get; set; } = 3_000;

    /// <summary>How long a canary verdict stays good for, so N endpoints in one cycle don't
    /// trigger N connectivity checks.</summary>
    public int CacheSeconds { get; set; } = 5;
}

/// <summary>
/// Raw configured endpoint. Everything except the id is nullable so that an entry in
/// usersettings.json can override a single field of a shipped endpoint without having to repeat
/// the whole definition.
/// </summary>
public sealed class EndpointOptions
{
    public string? Name { get; set; }
    public string? Group { get; set; }
    public bool? Enabled { get; set; }
    public string? Type { get; set; }
    public string? Url { get; set; }
    public string? Profile { get; set; }
    public int? IntervalSeconds { get; set; }
    public int? TimeoutMs { get; set; }
    public int? LatencyWarnMs { get; set; }
    public string? MustContain { get; set; }
    public List<MaintenanceWindow>? MaintenanceWindows { get; set; }
}

public sealed class MaintenanceWindow
{
    /// <summary>"Daily", or a day name ("Monday", "Lunes"). Empty means daily.</summary>
    public string Days { get; set; } = "Daily";

    public string From { get; set; } = "00:00";
    public string To { get; set; } = "00:00";

    public bool Contains(DateTime localNow)
    {
        if (!TimeOnly.TryParse(From, out var from) || !TimeOnly.TryParse(To, out var to))
        {
            return false;
        }

        if (!IsDayMatch(localNow.DayOfWeek))
        {
            return false;
        }

        var now = TimeOnly.FromDateTime(localNow);

        // A window like 22:00-04:00 wraps past midnight.
        return from <= to
            ? now >= from && now < to
            : now >= from || now < to;
    }

    private bool IsDayMatch(DayOfWeek day)
    {
        if (string.IsNullOrWhiteSpace(Days) || Days.Equals("Daily", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Days.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(token => Enum.TryParse<DayOfWeek>(token, ignoreCase: true, out var parsed) && parsed == day);
    }
}

public sealed class AlertingOptions
{
    public const string SectionName = "Alerting";

    /// <summary>Comma-separated. Lives in usersettings.json — it is the one alerting knob the
    /// client owns.</summary>
    public string Recipients { get; set; } = "";

    /// <summary>
    /// Permite instalar el programa en varias máquinas de un mismo cliente donde sólo una avisa.
    /// En el servidor queda encendido; en las terminales, apagado, para que puedan ver el semáforo
    /// y el gráfico sin que cada una mande su propio mail por la misma caída.
    /// </summary>
    public bool MailEnabled { get; set; } = true;

    public bool GroupByIncident { get; set; } = true;

    /// <summary>Nothing is alerted while the canary says there is no internet. A local outage is
    /// the user's own problem and they already know about it.</summary>
    public bool SuppressWhenOffline { get; set; } = true;

    public int MaxMailsPerHour { get; set; } = 12;

    public IReadOnlyList<string> ParsedRecipients() =>
        Recipients.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = "";

    /// <summary>
    /// DPAPI (LocalMachine scope) blob, Base64. Deliberately not called "Password" so nobody
    /// drops a plaintext one in by reflex. Generate it with --protect-password.
    /// </summary>
    public string ProtectedPassword { get; set; } = "";

    /// <summary>Escape hatch for local development only; never populated in a shipped build.</summary>
    public string Password { get; set; } = "";

    public string FromAddress { get; set; } = "";
    public string FromDisplayName { get; set; } = "WebServiceAlerter";
    public int RetryCount { get; set; } = 3;
    public int RetryBackoffSeconds { get; set; } = 5;
    public int TimeoutMilliseconds { get; set; } = 15_000;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

/// <summary>
/// Canal opcional hacia un Discord compartido entre desarrolladores, donde se ve el estado de los
/// servicios en todas las instalaciones a la vez.
///
/// Se identifica por <see cref="Identity"/> —localidad e ISP— y NO por el nombre del cliente: el
/// canal es común a varios desarrolladores y nadie tiene por qué saber de quién es cada servidor.
/// Vive en discord.json bajo ProgramData y no en appsettings.json porque el instalador se publica
/// abierto: una URL de webhook dentro del MSI sería una invitación a que cualquiera escriba en ese
/// canal.
/// </summary>
public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    public bool Enabled { get; set; }

    /// <summary>
    /// Blob DPAPI (LocalMachine) de la URL del webhook, en Base64. La URL es un secreto: quien la
    /// tenga puede escribir en el canal, y este archivo vive en equipos de clientes. Se genera con
    /// --configure-discord.
    /// </summary>
    public string ProtectedWebhookUrl { get; set; } = "";

    /// <summary>Alternativa en texto plano, sólo para desarrollo.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>Cómo se identifica esta instalación en el canal común: "Rosario — Telecom".</summary>
    public string Identity { get; set; } = "";

    public int MaxMessagesPerHour { get; set; } = 20;

    public bool HasWebhook =>
        !string.IsNullOrWhiteSpace(ProtectedWebhookUrl) || !string.IsNullOrWhiteSpace(WebhookUrl);

    public bool IsConfigured => Enabled && HasWebhook && !string.IsNullOrWhiteSpace(Identity);
}

public sealed class HttpOptions
{
    public const string SectionName = "Http";

    public bool IgnoreCertificateErrors { get; set; }
    public string UserAgent { get; set; } = "WebServiceAlerter/0.1";
    public int CertificateExpiryWarnDays { get; set; } = 15;
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Path { get; set; } = @"%ProgramData%\WebServiceAlerter\webservicealerter.db";
    public int RetentionDays { get; set; } = 90;

    public string GetExpandedPath() => Environment.ExpandEnvironmentVariables(Path);
}

public sealed class StatusFileOptions
{
    public const string SectionName = "StatusFile";

    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = @"%ProgramData%\WebServiceAlerter\status.json";

    public string GetExpandedPath() => Environment.ExpandEnvironmentVariables(Path);
}
