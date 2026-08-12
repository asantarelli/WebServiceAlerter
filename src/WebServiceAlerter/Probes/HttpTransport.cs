using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using WebServiceAlerter.Configuration;

namespace WebServiceAlerter.Probes;

/// <summary>
/// One HttpClient for every HTTP-flavoured probe, plus the exception-to-outcome classification
/// that makes the difference between "no anda" and an actionable answer.
/// </summary>
public sealed class HttpTransport : IDisposable
{
    private readonly HttpOptions _options;
    private readonly HttpClient _client;

    /// <summary>
    /// Expiry date of the certificate last seen for each host. Keyed by host rather than by
    /// request because that is what a certificate actually belongs to, and because the TLS
    /// validation callback runs on a different async context than the caller — an AsyncLocal set
    /// inside it would never flow back out.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _certificateExpiry = new(StringComparer.OrdinalIgnoreCase);

    public HttpTransport(IOptions<HttpOptions> options)
    {
        _options = options.Value;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,

            // Without this, connections are pooled forever and DNS is resolved once, at the very
            // first request. A service that runs for months would keep talking to whatever IP it
            // resolved on the day it started — so if the monitored service moves (or its DNS is
            // repointed) we would keep reporting on a stale address, which for a tool whose only
            // job is to be right about availability is a silent, permanent lie.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),

            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, _, errors) =>
                {
                    // Which object arrives as `sender` is an implementation detail that has
                    // changed between runtimes, so take the host from whichever one shows up.
                    var host = sender switch
                    {
                        HttpRequestMessage request => request.RequestUri?.Host,
                        SslStream stream => stream.TargetHostName,
                        _ => null,
                    };

                    // This callback hands over the base X509Certificate; the runtime always
                    // supplies an X509Certificate2 in practice, and if that ever stops being true
                    // we simply skip the expiry reading rather than guess at a date.
                    if (certificate is X509Certificate2 parsed && !string.IsNullOrEmpty(host))
                    {
                        _certificateExpiry[host] = parsed.NotAfter;
                    }

                    return errors == SslPolicyErrors.None || _options.IgnoreCertificateErrors;
                },
            },
        };

        _client = new HttpClient(handler);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);
        // Timeout is enforced per request with a linked token so that a slow endpoint cannot
        // stall the whole cycle, and so a cancelled shutdown is distinguishable from a timeout.
        _client.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<HttpProbeOutcome> SendAsync(
        HttpRequestMessage request,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            stopwatch.Stop();

            return new HttpProbeOutcome
            {
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                StatusCode = (int)response.StatusCode,
                Body = body,
                CertificateDaysToExpiry = DaysToExpiry(request),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new HttpProbeOutcome
            {
                Failure = ProbeOutcome.Timeout,
                FailureDetail = $"sin respuesta en {timeoutMs} ms",
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                CertificateDaysToExpiry = DaysToExpiry(request),
            };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var (outcome, detail) = Classify(ex);
            return new HttpProbeOutcome
            {
                Failure = outcome,
                FailureDetail = detail,
                CertificateDaysToExpiry = DaysToExpiry(request),
            };
        }
    }

    private int? DaysToExpiry(HttpRequestMessage request) =>
        request.RequestUri?.Host is { Length: > 0 } host &&
        _certificateExpiry.TryGetValue(host, out var notAfter)
            ? (int)Math.Floor((notAfter - DateTime.Now).TotalDays)
            : null;

    private static (ProbeOutcome Outcome, string Detail) Classify(HttpRequestException ex)
    {
        // .NET surfaces a structured reason on HttpRequestException; fall back to inspecting the
        // inner exception for the cases it does not cover.
        switch (ex.HttpRequestError)
        {
            case HttpRequestError.NameResolutionError:
                return (ProbeOutcome.DnsFailure, "el nombre del servidor no resuelve");
            case HttpRequestError.SecureConnectionError:
                return (ProbeOutcome.TlsFailure, ex.InnerException?.Message ?? "error de TLS");
            case HttpRequestError.ConnectionError:
                break; // needs the socket detail below to say anything useful
        }

        return ex.InnerException switch
        {
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } =>
                (ProbeOutcome.DnsFailure, "el nombre del servidor no resuelve"),
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
                (ProbeOutcome.ConnectionRefused, "conexión rechazada por el servidor"),
            SocketException { SocketErrorCode: SocketError.TimedOut } =>
                (ProbeOutcome.Timeout, "tiempo de espera agotado al conectar"),
            SocketException socket =>
                (ProbeOutcome.ConnectionRefused, $"error de red: {socket.SocketErrorCode}"),
            AuthenticationException auth =>
                (ProbeOutcome.TlsFailure, auth.Message),
            _ => (ProbeOutcome.ConnectionRefused, ex.Message),
        };
    }

    public void Dispose() => _client.Dispose();
}

public sealed record HttpProbeOutcome
{
    public ProbeOutcome? Failure { get; init; }
    public string? FailureDetail { get; init; }
    public double? LatencyMs { get; init; }
    public int? StatusCode { get; init; }
    public string? Body { get; init; }
    public int? CertificateDaysToExpiry { get; init; }

    public bool Failed => Failure is not null;
}
