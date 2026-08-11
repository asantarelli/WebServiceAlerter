using System.Diagnostics;
using System.Net.Sockets;
using WebServiceAlerter.Configuration;

namespace WebServiceAlerter.Probes;

/// <summary>Bare TCP connect, for services that do not speak HTTP.</summary>
public sealed class TcpProbe : IProbe
{
    public ProbeType Type => ProbeType.Tcp;

    public async Task<ProbeResult> CheckAsync(ResolvedEndpoint endpoint, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!TryParseTarget(endpoint.Url, out var host, out var port))
        {
            return new ProbeResult
            {
                EndpointId = endpoint.Id,
                Outcome = ProbeOutcome.ContentMismatch,
                Detail = $"no puedo interpretar «{endpoint.Url}» como host:puerto",
                TimestampUtc = now,
            };
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(endpoint.TimeoutMs);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeoutCts.Token);
            stopwatch.Stop();

            var slow = stopwatch.Elapsed.TotalMilliseconds > endpoint.LatencyWarnMs;
            return new ProbeResult
            {
                EndpointId = endpoint.Id,
                Outcome = slow ? ProbeOutcome.Slow : ProbeOutcome.Ok,
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                TimestampUtc = now,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(endpoint, ProbeOutcome.Timeout, $"sin respuesta en {endpoint.TimeoutMs} ms", now);
        }
        catch (SocketException ex)
        {
            var outcome = ex.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => ProbeOutcome.DnsFailure,
                SocketError.ConnectionRefused => ProbeOutcome.ConnectionRefused,
                SocketError.TimedOut => ProbeOutcome.Timeout,
                _ => ProbeOutcome.ConnectionRefused,
            };

            return Failure(endpoint, outcome, ex.SocketErrorCode.ToString(), now);
        }
    }

    private static ProbeResult Failure(ResolvedEndpoint endpoint, ProbeOutcome outcome, string detail, DateTimeOffset now) =>
        new()
        {
            EndpointId = endpoint.Id,
            Outcome = outcome,
            Detail = detail,
            TimestampUtc = now,
        };

    /// <summary>Accepts both "host:port" and a full URI, so a Tcp endpoint can be written either
    /// way without the user having to know which one we expect.</summary>
    private static bool TryParseTarget(string value, out string host, out int port)
    {
        host = "";
        port = 0;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            host = uri.Host;
            port = uri.Port > 0 ? uri.Port : 443;
            return true;
        }

        var separator = value.LastIndexOf(':');
        if (separator > 0 && int.TryParse(value[(separator + 1)..], out port))
        {
            host = value[..separator];
            return port is > 0 and <= 65535;
        }

        return false;
    }
}
