using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebServiceAlerter.Viewer.Data;

/// <summary>Los valores que el cliente puede tocar desde la pantalla.</summary>
public sealed record UserSettings
{
    public string SiteName { get; init; } = "";
    public string Recipients { get; init; } = "";
    public int IntervalSeconds { get; init; } = 30;
    public int FailuresToAlert { get; init; } = 2;
    public int SuccessesToRecover { get; init; } = 2;
    public int ReminderIntervalMinutes { get; init; } = 20;
}

/// <summary>
/// Lee y escribe usersettings.json bajo ProgramData.
///
/// Escribe con JsonNode y no serializando un objeto entero a propósito: el archivo también puede
/// contener overrides de endpoints que el cliente o el soporte cargaron a mano, y guardar desde
/// esta pantalla no puede borrarlos. Sólo se tocan las claves que la pantalla edita.
/// </summary>
public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _path;

    public UserSettingsStore(string path) => _path = path;

    public string Path => _path;

    public UserSettings Read()
    {
        var root = LoadRoot();

        return new UserSettings
        {
            SiteName = GetString(root, "General", "SiteName") ?? "",
            Recipients = GetString(root, "Alerting", "Recipients") ?? "",
            IntervalSeconds = GetInt(root, "Monitoring", "DefaultIntervalSeconds") ?? 30,
            FailuresToAlert = GetInt(root, "Monitoring", "FailuresToAlert") ?? 2,
            SuccessesToRecover = GetInt(root, "Monitoring", "SuccessesToRecover") ?? 2,
            ReminderIntervalMinutes = GetInt(root, "Monitoring", "ReminderIntervalMinutes") ?? 20,
        };
    }

    public void Write(UserSettings settings)
    {
        var root = LoadRoot();

        Set(root, "General", "SiteName", JsonValue.Create(settings.SiteName));
        Set(root, "Alerting", "Recipients", JsonValue.Create(settings.Recipients));
        Set(root, "Monitoring", "DefaultIntervalSeconds", JsonValue.Create(settings.IntervalSeconds));
        Set(root, "Monitoring", "FailuresToAlert", JsonValue.Create(settings.FailuresToAlert));
        Set(root, "Monitoring", "SuccessesToRecover", JsonValue.Create(settings.SuccessesToRecover));
        Set(root, "Monitoring", "ReminderIntervalMinutes", JsonValue.Create(settings.ReminderIntervalMinutes));

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

        // Escritura a temporal y reemplazo: el servicio está mirando este archivo con un watcher,
        // y no puede llegar a leer una versión a medio escribir.
        var temp = _path + ".tmp";
        System.IO.File.WriteAllText(temp, root.ToJsonString(WriteOptions));
        System.IO.File.Move(temp, _path, overwrite: true);
    }

    private JsonObject LoadRoot()
    {
        try
        {
            if (System.IO.File.Exists(_path))
            {
                var texto = System.IO.File.ReadAllText(_path);
                if (!string.IsNullOrWhiteSpace(texto) &&
                    JsonNode.Parse(texto, documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    }) is JsonObject existente)
                {
                    return existente;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Un archivo corrupto no debe impedir guardar: se parte de uno nuevo.
        }

        return new JsonObject();
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject existente)
        {
            return existente;
        }

        var creada = new JsonObject();
        root[name] = creada;
        return creada;
    }

    private static void Set(JsonObject root, string section, string key, JsonNode? value) =>
        Section(root, section)[key] = value;

    private static string? GetString(JsonObject root, string section, string key) =>
        root[section] is JsonObject s && s[key] is JsonValue v && v.TryGetValue<string>(out var result) ? result : null;

    private static int? GetInt(JsonObject root, string section, string key) =>
        root[section] is JsonObject s && s[key] is JsonValue v && v.TryGetValue<int>(out var result) ? result : null;
}
