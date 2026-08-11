using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebServiceAlerter.Configuration;
using WebServiceAlerter.Monitoring;

namespace WebServiceAlerter.Status;

/// <summary>
/// Publishes the current state to a small JSON file that other programs can read.
///
/// The point is integration: the billing system can check this before attempting to issue an
/// invoice and tell the operator "ARCA has been down for 10 minutes, don't bother retrying"
/// instead of leaving them staring at a timeout. That turns the monitor from something that
/// reports the problem into something that prevents the support call.
/// </summary>
public sealed class StatusWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly StatusFileOptions _options;
    private readonly ILogger<StatusWriter> _logger;
    private bool _warned;

    public StatusWriter(IOptions<StatusFileOptions> options, ILogger<StatusWriter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void Write(IReadOnlyList<EndpointTracker> trackers, IReadOnlyDictionary<string, ResolvedEndpoint> endpoints, bool online)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var snapshot = new StatusSnapshot
        {
            UpdatedAt = DateTimeOffset.Now,
            Internet = online ? "ok" : "down",
            Endpoints = trackers.Select(tracker =>
            {
                var result = tracker.LastResult;
                var endpoint = result is not null && endpoints.TryGetValue(result.EndpointId, out var found) ? found : null;

                return new StatusEndpoint
                {
                    Id = result?.EndpointId ?? "",
                    Name = endpoint?.Name ?? result?.EndpointId ?? "",
                    State = tracker.State.ToString().ToLowerInvariant(),
                    Since = tracker.StateSince?.ToLocalTime(),
                    Outcome = result?.Outcome.ToString(),
                    Detail = result?.Detail,
                    LatencyMs = result?.LatencyMs is { } ms ? Math.Round(ms) : null,
                    CertificateDaysToExpiry = result?.CertificateDaysToExpiry,
                };
            }).ToList(),
        };

        var path = _options.GetExpandedPath();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write-then-move so a reader never catches a half-written file.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, SerializerOptions));
            File.Move(temp, path, overwrite: true);

            _warned = false;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _logger.LogWarning(ex, "No pude escribir el archivo de estado en {Path}.", path);
            }
        }
    }
}

public sealed record StatusSnapshot
{
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("internet")]
    public string Internet { get; init; } = "ok";

    [JsonPropertyName("endpoints")]
    public List<StatusEndpoint> Endpoints { get; init; } = new();
}

public sealed record StatusEndpoint
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    [JsonPropertyName("since")]
    public DateTimeOffset? Since { get; init; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("latencyMs")]
    public double? LatencyMs { get; init; }

    [JsonPropertyName("certificateDaysToExpiry")]
    public int? CertificateDaysToExpiry { get; init; }
}
