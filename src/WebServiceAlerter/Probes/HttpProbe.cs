using System.Xml.Linq;
using WebServiceAlerter.Configuration;

namespace WebServiceAlerter.Probes;

/// <summary>Plain availability check: status code plus optional content assertion.</summary>
public sealed class HttpProbe : IProbe
{
    private readonly HttpTransport _transport;

    public HttpProbe(HttpTransport transport) => _transport = transport;

    public ProbeType Type => ProbeType.Http;

    public async Task<ProbeResult> CheckAsync(ResolvedEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
        var outcome = await _transport.SendAsync(request, endpoint.TimeoutMs, cancellationToken);

        return ProbeResultBuilder.FromHttp(endpoint, outcome, body =>
            endpoint.MustContain is { Length: > 0 } needle &&
            !body.Contains(needle, StringComparison.OrdinalIgnoreCase)
                ? $"la respuesta no contiene «{needle}»"
                : null);
    }
}

/// <summary>
/// Fetches the WSDL and checks it actually parses as one. Catches the classic false positive
/// where a proxy or captive portal returns an HTML error page with a 200 status — a plain HTTP
/// probe calls that healthy.
/// </summary>
public sealed class WsdlProbe : IProbe
{
    private readonly HttpTransport _transport;

    public WsdlProbe(HttpTransport transport) => _transport = transport;

    public ProbeType Type => ProbeType.Wsdl;

    public async Task<ProbeResult> CheckAsync(ResolvedEndpoint endpoint, CancellationToken cancellationToken)
    {
        var url = endpoint.Url.Contains('?') ? endpoint.Url : endpoint.Url + "?WSDL";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var outcome = await _transport.SendAsync(request, endpoint.TimeoutMs, cancellationToken);

        return ProbeResultBuilder.FromHttp(endpoint, outcome, body =>
        {
            try
            {
                var root = XDocument.Parse(body).Root;
                return root?.Name.LocalName == "definitions"
                    ? null
                    : $"la respuesta no es un WSDL (raíz «{root?.Name.LocalName ?? "vacía"}»)";
            }
            catch (System.Xml.XmlException)
            {
                return "la respuesta no es XML válido";
            }
        });
    }
}
