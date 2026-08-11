using WebServiceAlerter.Configuration;

namespace WebServiceAlerter.Probes;

/// <summary>
/// Shared tail end of every HTTP-flavoured probe: transport failure, then status code, then
/// body inspection, then latency. Order matters — a 500 is a better explanation than "slow",
/// and "the service says its database is down" is a better explanation than either.
/// </summary>
internal static class ProbeResultBuilder
{
    public static ProbeResult FromHttp(
        ResolvedEndpoint endpoint,
        HttpProbeOutcome outcome,
        Func<string, string?> contentCheck,
        Func<string, (ProbeOutcome Outcome, string Detail)?>? inspector = null)
    {
        var now = DateTimeOffset.UtcNow;

        if (outcome.Failed)
        {
            return new ProbeResult
            {
                EndpointId = endpoint.Id,
                Outcome = outcome.Failure!.Value,
                Detail = outcome.FailureDetail,
                LatencyMs = outcome.LatencyMs,
                CertificateDaysToExpiry = outcome.CertificateDaysToExpiry,
                TimestampUtc = now,
            };
        }

        var status = outcome.StatusCode ?? 0;
        var body = outcome.Body ?? "";

        if (status is < 200 or >= 400)
        {
            return new ProbeResult
            {
                EndpointId = endpoint.Id,
                Outcome = ProbeOutcome.HttpError,
                Detail = $"HTTP {status}",
                HttpStatus = status,
                LatencyMs = outcome.LatencyMs,
                CertificateDaysToExpiry = outcome.CertificateDaysToExpiry,
                TimestampUtc = now,
            };
        }

        string? detail = null;

        if (inspector is not null)
        {
            var inspection = inspector(body);
            if (inspection is { } found)
            {
                if (found.Outcome != ProbeOutcome.Ok)
                {
                    return new ProbeResult
                    {
                        EndpointId = endpoint.Id,
                        Outcome = found.Outcome,
                        Detail = found.Detail,
                        HttpStatus = status,
                        LatencyMs = outcome.LatencyMs,
                        CertificateDaysToExpiry = outcome.CertificateDaysToExpiry,
                        TimestampUtc = now,
                    };
                }

                detail = found.Detail;
            }
        }

        if (contentCheck(body) is { } mismatch)
        {
            return new ProbeResult
            {
                EndpointId = endpoint.Id,
                Outcome = ProbeOutcome.ContentMismatch,
                Detail = mismatch,
                HttpStatus = status,
                LatencyMs = outcome.LatencyMs,
                CertificateDaysToExpiry = outcome.CertificateDaysToExpiry,
                TimestampUtc = now,
            };
        }

        var slow = outcome.LatencyMs is { } ms && ms > endpoint.LatencyWarnMs;

        return new ProbeResult
        {
            EndpointId = endpoint.Id,
            Outcome = slow ? ProbeOutcome.Slow : ProbeOutcome.Ok,
            Detail = detail,
            HttpStatus = status,
            LatencyMs = outcome.LatencyMs,
            CertificateDaysToExpiry = outcome.CertificateDaysToExpiry,
            TimestampUtc = now,
        };
    }
}
