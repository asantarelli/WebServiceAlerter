namespace WebServiceAlerter.Configuration;

/// <summary>
/// Everything needed to probe one kind of webservice, expressed as data.
///
/// This is the file colleagues are expected to contribute to: adding support for a new bank,
/// payment gateway or government service means adding an entry to profiles.json, not writing C#
/// and recompiling. Keeping the SOAP envelope out of the code is the whole point.
/// </summary>
public sealed class ServiceProfile
{
    public const string SectionName = "Profiles";

    public string? Description { get; set; }

    /// <summary>Value of the SOAPAction header. Required for SoapDummy probes.</summary>
    public string? SoapAction { get; set; }

    /// <summary>The full SOAP envelope to POST.</summary>
    public string? Envelope { get; set; }

    public string ContentType { get; set; } = "text/xml; charset=utf-8";

    /// <summary>
    /// Local names of the response elements that report subsystem health, e.g. AppServer,
    /// DbServer, AuthServer. Every one of them must equal <see cref="OkValue"/> for the service
    /// to count as up — that is what catches "the endpoint answers but its database is down",
    /// the failure a plain GET reports as perfectly healthy.
    /// </summary>
    public List<string> StatusNodes { get; set; } = new();

    public string OkValue { get; set; } = "OK";
}
