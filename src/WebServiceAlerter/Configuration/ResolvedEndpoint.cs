using Microsoft.Extensions.Logging;

namespace WebServiceAlerter.Configuration;

public enum ProbeType
{
    Http,
    Wsdl,
    SoapDummy,
    Tcp,
}

/// <summary>An endpoint with every default applied and its profile attached — what the probes
/// actually consume.</summary>
public sealed record ResolvedEndpoint
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Group { get; init; }
    public required ProbeType Type { get; init; }
    public required string Url { get; init; }
    public required int IntervalSeconds { get; init; }
    public required int TimeoutMs { get; init; }
    public required int LatencyWarnMs { get; init; }
    public string? MustContain { get; init; }
    public ServiceProfile? Profile { get; init; }
    public IReadOnlyList<MaintenanceWindow> MaintenanceWindows { get; init; } = [];

    public bool IsInMaintenance(DateTime localNow) => MaintenanceWindows.Any(w => w.Contains(localNow));
}

/// <summary>
/// Turns the configured endpoint dictionary into a validated, defaults-applied list. Entries
/// that cannot possibly work (no URL, unknown type, SoapDummy without a profile) are dropped with
/// a loud log line rather than blowing up the service: one bad entry in usersettings.json must
/// never stop the other endpoints from being monitored.
/// </summary>
public static class EndpointResolver
{
    public static IReadOnlyList<ResolvedEndpoint> Resolve(
        MonitoringOptions monitoring,
        IReadOnlyDictionary<string, ServiceProfile> profiles,
        ILogger logger)
    {
        var resolved = new List<ResolvedEndpoint>();

        foreach (var (id, options) in monitoring.Endpoints)
        {
            if (options.Enabled == false)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(options.Url))
            {
                logger.LogWarning("Endpoint '{Id}' ignorado: no tiene URL configurada.", id);
                continue;
            }

            if (!Enum.TryParse<ProbeType>(options.Type ?? nameof(ProbeType.Http), ignoreCase: true, out var type))
            {
                logger.LogWarning("Endpoint '{Id}' ignorado: tipo de chequeo desconocido '{Type}'.", id, options.Type);
                continue;
            }

            ServiceProfile? profile = null;
            var profileKey = options.Profile ?? id;
            if (profiles.TryGetValue(profileKey, out var found))
            {
                profile = found;
            }

            if (type == ProbeType.SoapDummy && (profile is null || string.IsNullOrWhiteSpace(profile.Envelope)))
            {
                logger.LogWarning(
                    "Endpoint '{Id}' ignorado: es SoapDummy pero no encontré el perfil '{Profile}' en profiles.json.",
                    id, profileKey);
                continue;
            }

            resolved.Add(new ResolvedEndpoint
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(options.Name) ? id : options.Name,
                Group = options.Group,
                Type = type,
                Url = options.Url.Trim(),
                IntervalSeconds = Math.Max(5, options.IntervalSeconds ?? monitoring.DefaultIntervalSeconds),
                TimeoutMs = Math.Max(500, options.TimeoutMs ?? monitoring.DefaultTimeoutMs),
                LatencyWarnMs = options.LatencyWarnMs ?? monitoring.DefaultLatencyWarnMs,
                MustContain = options.MustContain,
                Profile = profile,
                MaintenanceWindows = options.MaintenanceWindows ?? [],
            });
        }

        return resolved;
    }
}
