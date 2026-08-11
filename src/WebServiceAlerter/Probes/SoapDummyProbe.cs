using System.Text;
using System.Xml.Linq;
using WebServiceAlerter.Configuration;

namespace WebServiceAlerter.Probes;

/// <summary>
/// Calls the unauthenticated "dummy" operation that ARCA (and several other SOAP services)
/// expose, and reads the per-subsystem health it returns.
///
/// This is the probe that justifies the project. When ARCA's database is down, the endpoint keeps
/// answering HTTP 200 and its WSDL keeps downloading perfectly — a plain availability check
/// reports everything green while nobody can issue an invoice. The dummy response is the only
/// cheap, credential-free way to see the difference.
/// </summary>
public sealed class SoapDummyProbe : IProbe
{
    private readonly HttpTransport _transport;

    public SoapDummyProbe(HttpTransport transport) => _transport = transport;

    public ProbeType Type => ProbeType.SoapDummy;

    public async Task<ProbeResult> CheckAsync(ResolvedEndpoint endpoint, CancellationToken cancellationToken)
    {
        var profile = endpoint.Profile!;   // guaranteed by EndpointResolver

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
        {
            Content = new StringContent(profile.Envelope!, Encoding.UTF8),
        };

        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(profile.ContentType);

        if (!string.IsNullOrWhiteSpace(profile.SoapAction))
        {
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{profile.SoapAction}\"");
        }

        var outcome = await _transport.SendAsync(request, endpoint.TimeoutMs, cancellationToken);

        return ProbeResultBuilder.FromHttp(endpoint, outcome, body => null, body => Inspect(profile, body));
    }

    /// <summary>
    /// Returns null when every subsystem reports OK; otherwise the outcome and the detail to put
    /// in the alert. A missing node is treated as a content mismatch rather than an outage: it
    /// means the profile no longer matches the service's contract, which is our problem, not
    /// theirs, and alerting "ARCA caído" over it would be a lie.
    /// </summary>
    private static (ProbeOutcome Outcome, string Detail)? Inspect(ServiceProfile profile, string body)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException)
        {
            return (ProbeOutcome.ContentMismatch, "la respuesta del dummy no es XML válido");
        }

        if (profile.StatusNodes.Count == 0)
        {
            return null;
        }

        var readings = new List<string>();
        var failures = new List<string>();
        var missing = new List<string>();

        foreach (var nodeName in profile.StatusNodes)
        {
            var element = document.Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, nodeName, StringComparison.OrdinalIgnoreCase));

            if (element is null)
            {
                missing.Add(nodeName);
                continue;
            }

            var value = element.Value.Trim();
            readings.Add($"{nodeName}={value}");

            if (!string.Equals(value, profile.OkValue, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{nodeName}={value}");
            }
        }

        if (missing.Count > 0 && readings.Count == 0)
        {
            return (ProbeOutcome.ContentMismatch,
                $"el dummy no devolvió ninguno de los nodos esperados ({string.Join(", ", missing)})");
        }

        // On success the readings are still worth carrying: seeing "AppServer=OK, DbServer=OK,
        // AuthServer=OK" on screen is what convinces the user the check is real.
        return failures.Count > 0
            ? (ProbeOutcome.ServiceReportedDown, string.Join(", ", failures))
            : (ProbeOutcome.Ok, string.Join(", ", readings));
    }
}
