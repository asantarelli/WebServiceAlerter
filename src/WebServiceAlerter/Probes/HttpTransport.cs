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
    /// Server certificates observed during the handshake, keyed by the request that triggered
    /// them. The validation callback runs on a different async context than the caller, so an
    /// AsyncLocal would not flow the value back — the request message is the one thing both
    /// sides reliably share.
    /// </summary>
    private readonly ConcurrentDictionary<HttpRequestMessage, DateTime> _certificateExpiry = new();

    public HttpTransport(IOptions<HttpOptions> options)
    {
        _options = options.Value;

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
            {
                if (certificate is not null)
                {
                    _certificateExpiry[request] = certificate.NotAfter;
                }

                return errors == SslPolicyErrors.None || _options.IgnoreCertificateErrors;
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
        finally
        {
            _certificateExpiry.TryRemove(request, out _);
        }
    }

    private int? DaysToExpiry(HttpRequestMessage request) =>
        _certificateExpiry.TryGetValue(request, out var notAfter)
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
