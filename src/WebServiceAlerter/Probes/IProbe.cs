using WebServiceAlerter.Configuration;

namespace WebServiceAlerter.Probes;

public interface IProbe
{
    ProbeType Type { get; }

    Task<ProbeResult> CheckAsync(ResolvedEndpoint endpoint, CancellationToken cancellationToken);
}
