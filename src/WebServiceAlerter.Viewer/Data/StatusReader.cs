using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebServiceAlerter.Viewer.Data;

/// <summary>Estado actual publicado por el servicio en status.json.</summary>
public sealed record StatusSnapshot
{
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("internet")]
    public string Internet { get; init; } = "ok";

    [JsonPropertyName("endpoints")]
    public List<StatusEndpoint> Endpoints { get; init; } = new();

    public bool HasInternet => Internet.Equals("ok", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cuánto hace que el archivo no se actualiza. Si crece, el servicio dejó de correr — y una
    /// pantalla en verde alimentada por datos viejos es peor que una pantalla vacía.
    /// </summary>
    public TimeSpan Age => DateTimeOffset.Now - UpdatedAt;
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

public sealed class StatusReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    public StatusReader(string path) => _path = path;

    public string Path => _path;

    /// <summary>
    /// Devuelve null cuando el servicio nunca corrió o el archivo está a medio escribir. El
    /// servicio escribe a un temporal y renombra, así que la ventana de lectura parcial es
    /// mínima, pero leer justo durante el reemplazo es posible y no debe romper la ventana.
    /// </summary>
    public StatusSnapshot? TryRead()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<StatusSnapshot>(stream, Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
