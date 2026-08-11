using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebServiceAlerter.Configuration;

namespace WebServiceAlerter.Probes;

/// <summary>Resolves configuration into endpoints and routes each one to the probe for its type.</summary>
public sealed class ProbeRunner
{
    private readonly Dictionary<ProbeType, IProbe> _probes;
    private readonly IOptionsMonitor<MonitoringOptions> _monitoring;
    private readonly IReadOnlyDictionary<string, ServiceProfile> _profiles;
    private readonly ILogger<ProbeRunner> _logger;

    public ProbeRunner(
        IEnumerable<IProbe> probes,
        IOptionsMonitor<MonitoringOptions> monitoring,
        IReadOnlyDictionary<string, ServiceProfile> profiles,
        ILogger<ProbeRunner> logger)
    {
        _probes = probes.ToDictionary(p => p.Type);
        _monitoring = monitoring;
        _profiles = profiles;
        _logger = logger;
    }

    public IReadOnlyList<ResolvedEndpoint> ResolveEndpoints() =>
        EndpointResolver.Resolve(_monitoring.CurrentValue, _profiles, _logger);

    public async Task<ProbeResult> CheckAsync(ResolvedEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            return await _probes[endpoint.Type].CheckAsync(endpoint, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A probe that throws is a bug in the probe, not evidence that the service is down —
            // report it as unverifiable rather than crying wolf.
            _logger.LogError(ex, "El chequeo de {Endpoint} lanzó una excepción inesperada.", endpoint.Id);

            return new ProbeResult
            {
                EndpointId = endpoint.Id,
                Outcome = ProbeOutcome.NotVerifiable,
                Detail = $"error interno del chequeo: {ex.Message}",
                TimestampUtc = DateTimeOffset.UtcNow,
            };
        }
    }
}
