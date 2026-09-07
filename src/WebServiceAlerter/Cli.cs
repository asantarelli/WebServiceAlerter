using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using WebServiceAlerter.Alerting;
using WebServiceAlerter.Configuration;
using WebServiceAlerter.Data;
using WebServiceAlerter.Monitoring;
using WebServiceAlerter.Probes;
using WebServiceAlerter.Security;

namespace WebServiceAlerter;

/// <summary>
/// The commands that make the prototype inspectable from a console. `--once` in particular is the
/// fastest way for somebody to see whether this thing does anything useful, which matters a lot
/// more than it sounds when you are asking colleagues to try it.
/// </summary>
internal static class Cli
{
    public static int ProtectPassword(string[] args)
    {
        var index = Array.IndexOf(args, "--protect-password");
        var password = index + 1 < args.Length && !args[index + 1].StartsWith("--")
            ? args[index + 1]
            : Prompt();

        if (string.IsNullOrEmpty(password))
        {
            Console.WriteLine("No ingresaste ninguna contraseña.");
            return 1;
        }

        try
        {
            // Sólo la contraseña: el resto de la sección se conserva. Antes se reescribía el
            // archivo entero, así que cambiar la contraseña borraba el servidor y el usuario.
            var destino = SaveSmtp(smtp => smtp["ProtectedPassword"] = PasswordProtector.Protect(password));

            Console.WriteLine();
            Console.WriteLine($"Contraseña cifrada y guardada en:  {destino}");
            Console.WriteLine();
            Console.WriteLine("Ese archivo no lo toca el instalador, así que sobrevive a las actualizaciones.");
            Console.WriteLine("El blob sólo sirve en ESTA máquina: hay que generarlo una vez por equipo.");
            Console.WriteLine();
            Console.WriteLine("Si además falta cargar servidor, usuario y remitente:  --configure-smtp");
            return 0;
        }
        catch (Exception ex)
        {
            ExplainWriteFailure(ex, SmtpPath);
            return 1;
        }

        static string Prompt()
        {
            Console.Write("Contraseña de la casilla de envío: ");
            return ReadHidden();
        }
    }

    private static string ReadHidden()
    {
        var buffer = new System.Text.StringBuilder();

        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }

                if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
                {
                    buffer.Length--;
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // No interactive console (redirected input): fall back to a plain read.
            return Console.ReadLine() ?? "";
        }

        return buffer.ToString();
    }

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var runner = services.GetRequiredService<ProbeRunner>();
        var endpoints = runner.ResolveEndpoints();

        if (args.Contains("--list"))
        {
            return List(endpoints);
        }

        if (args.Contains("--test-mail"))
        {
            return await TestAsync(services.GetRequiredService<SmtpAlertSender>());
        }

        if (args.Contains("--test-discord"))
        {
            return await TestAsync(services.GetRequiredService<DiscordAlertSender>());
        }

        if (args.Contains("--configure-discord"))
        {
            return ConfigureDiscord();
        }

        if (args.Contains("--configure-smtp"))
        {
            return ConfigureSmtp();
        }

        var historyIndex = Array.IndexOf(args, "--history");
        if (historyIndex >= 0)
        {
            var hours = historyIndex + 1 < args.Length && int.TryParse(args[historyIndex + 1], out var parsed)
                ? parsed
                : 24;

            return History(services, endpoints, hours);
        }

        var testIndex = Array.IndexOf(args, "--test");
        if (testIndex >= 0)
        {
            var id = testIndex + 1 < args.Length ? args[testIndex + 1] : null;
            endpoints = endpoints.Where(e => id is null || e.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();

            if (endpoints.Count == 0)
            {
                Console.WriteLine($"No encontré ningún endpoint con id «{id}». Probá --list.");
                return 1;
            }
        }

        return await RunOnceAsync(services, runner, endpoints);
    }

    private static int List(IReadOnlyList<ResolvedEndpoint> endpoints)
    {
        if (endpoints.Count == 0)
        {
            Console.WriteLine("No hay endpoints configurados.");
            return 1;
        }

        Console.WriteLine($"{"ID",-20} {"TIPO",-11} {"CADA",-7} URL");
        Console.WriteLine(new string('-', 100));

        foreach (var endpoint in endpoints)
        {
            Console.WriteLine($"{endpoint.Id,-20} {endpoint.Type,-11} {endpoint.IntervalSeconds + "s",-7} {endpoint.Url}");
        }

        return 0;
    }

    private static async Task<int> RunOnceAsync(
        IServiceProvider services,
        ProbeRunner runner,
        IReadOnlyList<ResolvedEndpoint> endpoints)
    {
        if (endpoints.Count == 0)
        {
            Console.WriteLine("No hay endpoints configurados. Revisá Monitoring:Endpoints en appsettings.json.");
            return 1;
        }

        var canary = services.GetRequiredService<CanaryChecker>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        Console.WriteLine();
        var online = await canary.IsOnlineAsync(cts.Token);
        WriteLabelled("Internet", online ? "OK" : "SIN CONEXIÓN", online ? ConsoleColor.Green : ConsoleColor.DarkYellow);

        if (!online)
        {
            Console.WriteLine();
            Console.WriteLine("Sin conexión no se chequea nada: un corte local no puede hacerse pasar por una caída del servicio.");
            return 2;
        }

        Console.WriteLine();

        var results = await Task.WhenAll(endpoints.Select(e => runner.CheckAsync(e, cts.Token)));
        var byId = endpoints.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var anyDown = false;

        foreach (var result in results)
        {
            var endpoint = byId[result.EndpointId];
            var (symbol, color) = result.Outcome switch
            {
                ProbeOutcome.Ok => ("●", ConsoleColor.Green),
                ProbeOutcome.Slow => ("●", ConsoleColor.Yellow),
                _ => ("●", ConsoleColor.Red),
            };

            anyDown |= !result.IsUp;

            Write(symbol + " ", color);
            Console.Write($"{endpoint.Name}");
            Console.WriteLine();

            Console.WriteLine($"    {result.Outcome.ToSpanish()}" +
                              (result.LatencyMs is { } ms ? $" — {ms:F0} ms" : ""));

            if (!string.IsNullOrWhiteSpace(result.Detail))
            {
                Console.WriteLine($"    {result.Detail}");
            }

            if (result.CertificateDaysToExpiry is { } days && days < 30)
            {
                Write($"    certificado TLS vence en {days} días\n", ConsoleColor.DarkYellow);
            }

            Console.WriteLine();
        }

        return anyDown ? 3 : 0;
    }

    /// <summary>
    /// Prints what the monitor recorded over a window. This is the command to reach for when
    /// somebody reports "no pude facturar a las diez y cuarto": it turns the stored history into
    /// an answer instead of leaving it to memory and impressions.
    /// </summary>
    private static int History(IServiceProvider services, IReadOnlyList<ResolvedEndpoint> endpoints, int hours)
    {
        var recorder = services.GetRequiredService<DataRecorder>();

        if (!recorder.IsAvailable)
        {
            Console.WriteLine($"No pude abrir la base de datos: {recorder.InitializationError}");
            return 1;
        }

        var from = DateTimeOffset.UtcNow.AddHours(-hours);
        var names = endpoints.ToDictionary(e => e.Id, e => e.Name, StringComparer.OrdinalIgnoreCase);
        var counts = recorder.GetOutcomeCounts(from);

        Console.WriteLine();
        Console.WriteLine($"Historial de las últimas {hours} h — {recorder.DatabasePath}");
        Console.WriteLine();

        if (counts.Count == 0)
        {
            Console.WriteLine("No hay nada registrado en ese período.");
            Console.WriteLine("El servicio graba mientras corre; si estuvo detenido, no hay datos.");
            return 0;
        }

        foreach (var group in counts.GroupBy(c => c.EndpointId))
        {
            var total = group.Sum(g => g.Count);
            var verifiable = group.Where(g => g.Outcome.CountsTowardUptime()).Sum(g => g.Count);
            var up = group.Where(g => g.Outcome.IsUp()).Sum(g => g.Count);

            Console.WriteLine(names.GetValueOrDefault(group.Key, group.Key));

            // Uptime over verifiable samples only: time without internet is not the monitored
            // service's fault and must not be charged to it.
            var uptime = verifiable == 0 ? "sin datos verificables" : $"{(double)up / verifiable:P2}";
            Console.WriteLine($"    {total} chequeos — disponibilidad {uptime}");

            var latencias = recorder.GetLatencies(group.Key, from);
            if (latencias.Count > 0)
            {
                double Percentil(double p) => latencias[Math.Min(latencias.Count - 1, (int)(latencias.Count * p))];

                Console.WriteLine(
                    $"    latencia — mediana {Percentil(0.50):F0} ms · p95 {Percentil(0.95):F0} ms · " +
                    $"p99 {Percentil(0.99):F0} ms · máxima {latencias[^1]:F0} ms");
            }

            foreach (var (_, outcome, count) in group.OrderByDescending(g => g.Count))
            {
                var color = outcome.IsUp() ? ConsoleColor.Green
                          : outcome == ProbeOutcome.NotVerifiable ? ConsoleColor.DarkGray
                          : ConsoleColor.Red;

                Write($"    {count,6}  ", color);
                Console.WriteLine(outcome.ToSpanish());
            }

            Console.WriteLine();
        }

        var incidents = recorder.GetIncidents(from);
        Console.WriteLine($"Incidentes confirmados: {incidents.Count}");

        foreach (var (endpointId, startedAt, resolvedAt, outcome, detail) in incidents)
        {
            var duration = (resolvedAt ?? DateTimeOffset.UtcNow) - startedAt;
            var closing = resolvedAt is null ? " (en curso)" : "";

            Write("  ! ", ConsoleColor.Red);
            Console.WriteLine($"{startedAt.ToLocalTime():dd/MM HH:mm:ss}  {names.GetValueOrDefault(endpointId, endpointId)}");
            Console.WriteLine($"      {outcome.ToSpanish()}{(string.IsNullOrWhiteSpace(detail) ? "" : $" — {detail}")}");
            Console.WriteLine($"      duró {AlertEvent.FormatDuration(duration)}{closing}");
        }

        // Individual failures matter even when they never became an incident: a blip that never
        // reached the confirmation threshold is invisible in the incident list, but it is exactly
        // what a user hits when a single invoice fails and the next one works.
        var failures = recorder.GetFailures(from, 20);
        Console.WriteLine();
        Console.WriteLine($"Chequeos fallidos sueltos (los {Math.Min(20, failures.Count)} más recientes):");

        if (failures.Count == 0)
        {
            Console.WriteLine("  ninguno.");
        }

        foreach (var (at, endpointId, outcome, latencyMs) in failures)
        {
            Console.WriteLine(
                $"  {at.ToLocalTime():dd/MM HH:mm:ss}  {names.GetValueOrDefault(endpointId, endpointId)}" +
                $"  —  {outcome.ToSpanish()}" +
                (latencyMs is { } ms ? $" ({ms:F0} ms)" : ""));
        }

        return 0;
    }

    /// <summary>
    /// Prueba un canal puntual y no el compuesto: si se probaran todos juntos, un canal caído
    /// quedaría tapado por el que sí anduvo.
    /// </summary>
    private static async Task<int> TestAsync(IAlertSender sender)
    {
        var enviado = await sender.SendAsync(new AlertEvent
        {
            Kind = AlertKind.Test,
            At = DateTimeOffset.Now,
            Title = "Mensaje de prueba",
            Note = "Si estás leyendo esto, el canal está bien configurado.",
        }, CancellationToken.None);

        Console.WriteLine(enviado
            ? $"Prueba enviada por {sender.Channel}."
            : $"No se pudo enviar por {sender.Channel}. El motivo está en las líneas de log de arriba.");

        return enviado ? 0 : 1;
    }

    /// <summary>
    /// Ruta de smtp.json, el archivo por equipo con los datos de la casilla de envío.
    /// </summary>
    private static string SmtpPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WebServiceAlerter",
        "smtp.json");

    /// <summary>
    /// Modifica la sección Smtp preservando lo que ya haya. Se lee y se reescribe con JsonNode en
    /// vez de serializar un objeto entero para que cambiar un dato no borre los otros.
    /// </summary>
    private static string SaveSmtp(Action<JsonObject> modificar)
    {
        JsonObject raiz;

        try
        {
            raiz = File.Exists(SmtpPath) && JsonNode.Parse(File.ReadAllText(SmtpPath)) is JsonObject existente
                ? existente
                : new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            raiz = new JsonObject();
        }

        if (raiz["Smtp"] is not JsonObject smtp)
        {
            smtp = new JsonObject();
            raiz["Smtp"] = smtp;
        }

        modificar(smtp);

        Directory.CreateDirectory(Path.GetDirectoryName(SmtpPath)!);
        File.WriteAllText(SmtpPath, raiz.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return SmtpPath;
    }

    /// <summary>
    /// Configura la casilla de envío completa en smtp.json.
    ///
    /// Existe porque el instalador se publica abierto y por lo tanto no puede llevar los datos de
    /// la casilla adentro: el appsettings.json que instala tiene la sección vacía. Sin este paso,
    /// una instalación queda muda —no puede enviar nada— y el fallo recién aparece cuando hace
    /// falta avisar de una caída, que es el peor momento para descubrirlo.
    /// </summary>
    private static int ConfigureSmtp()
    {
        Console.WriteLine("Configuración de la casilla desde la que se envían las alertas.");
        Console.WriteLine("Esto se carga una vez por equipo y sobrevive a las actualizaciones.");
        Console.WriteLine();

        var host = Ask("Servidor SMTP", "");
        if (string.IsNullOrWhiteSpace(host))
        {
            Console.WriteLine("Sin servidor no se configura nada.");
            return 1;
        }

        var puertoTexto = Ask("Puerto", "587");
        if (!int.TryParse(puertoTexto, out var puerto) || puerto is < 1 or > 65535)
        {
            Console.WriteLine("Ese puerto no es válido.");
            return 1;
        }

        var ssl = Ask("¿Usa SSL/TLS? (s/n)", "s").StartsWith("s", StringComparison.OrdinalIgnoreCase);
        var usuario = Ask("Usuario", "");
        var remitente = Ask("Dirección remitente", usuario);
        var nombre = Ask("Nombre visible del remitente", "WebServiceAlerter");

        Console.Write("Contraseña (no se muestra): ");
        var password = ReadHidden();

        try
        {
            var destino = SaveSmtp(smtp =>
            {
                smtp["Host"] = host;
                smtp["Port"] = puerto;
                smtp["UseSsl"] = ssl;
                smtp["Username"] = usuario;
                smtp["FromAddress"] = remitente;
                smtp["FromDisplayName"] = nombre;

                // Una contraseña vacía deja la que ya estuviera guardada: sirve para corregir el
                // servidor sin tener que volver a tipearla.
                if (!string.IsNullOrEmpty(password))
                {
                    smtp["ProtectedPassword"] = PasswordProtector.Protect(password);
                }
            });

            Console.WriteLine();
            Console.WriteLine($"Guardado en:  {destino}");
            Console.WriteLine();
            Console.WriteLine("Probalo con:  WebServiceAlerter.exe --test-mail");
            return 0;
        }
        catch (Exception ex)
        {
            ExplainWriteFailure(ex, SmtpPath);
            return 1;
        }
    }

    /// <summary>
    /// Los archivos de ProgramData los crea el servicio, que corre como LocalSystem, así que un
    /// usuario común sólo puede leerlos. Decirlo con todas las letras evita que quien configura
    /// una instalación se quede mirando un "acceso denegado" sin saber qué le falta.
    /// </summary>
    private static void ExplainWriteFailure(Exception ex, string destino)
    {
        Console.WriteLine();

        if (ex is UnauthorizedAccessException)
        {
            Console.WriteLine($"No tengo permiso para escribir {destino}.");
            Console.WriteLine();
            Console.WriteLine("Ese archivo pertenece al servicio, así que hace falta una consola");
            Console.WriteLine("de administrador. Abrí PowerShell o CMD con «Ejecutar como");
            Console.WriteLine("administrador» y volvé a correr este comando.");
        }
        else
        {
            Console.WriteLine($"No pude escribir {destino}: {ex.Message}");
        }
    }

    private static string Ask(string etiqueta, string porDefecto)
    {
        Console.Write(string.IsNullOrEmpty(porDefecto) ? $"{etiqueta}: " : $"{etiqueta} [{porDefecto}]: ");
        var valor = (Console.ReadLine() ?? "").Trim();
        return string.IsNullOrEmpty(valor) ? porDefecto : valor;
    }

    /// <summary>
    /// Deja discord.json listo en ProgramData. Se pide interactivamente porque los dos valores son
    /// distintos en cada instalación —la identidad siempre, y el webhook por estar cifrado contra
    /// esta máquina— así que ninguno puede venir dentro del instalador.
    /// </summary>
    private static int ConfigureDiscord()
    {
        Console.WriteLine("Configuración del canal de Discord");
        Console.WriteLine();
        Console.WriteLine("La identidad es cómo se va a ver ESTE equipo en el canal compartido.");
        Console.WriteLine("Poné localidad e ISP, nunca el nombre del cliente: el canal lo ven");
        Console.WriteLine("varios desarrolladores y no corresponde que sepan de quién es cada servidor.");
        Console.WriteLine("Ejemplo:  Rosario, Santa Fe — Telecom");
        Console.WriteLine();

        Console.Write("Identidad: ");
        var identidad = (Console.ReadLine() ?? "").Trim();

        if (string.IsNullOrWhiteSpace(identidad))
        {
            Console.WriteLine("Sin identidad no se configura nada.");
            return 1;
        }

        Console.WriteLine();
        Console.Write("URL del webhook (no se muestra): ");
        var webhook = ReadHidden().Trim();

        // Se valida con la misma regla que usa el sender. Con dos validaciones distintas, una
        // dirección podía guardarse como buena y después fallar callada en cada envío.
        if (!DiscordAlertSender.IsValidWebhook(webhook))
        {
            Console.WriteLine();
            Console.WriteLine("Eso no parece un webhook de Discord.");
            Console.WriteLine("Tiene que ser https y del dominio discord.com, con la forma");
            Console.WriteLine("  https://discord.com/api/webhooks/<id>/<token>");
            Console.WriteLine("Se obtiene en el canal: Editar canal → Integraciones → Webhooks.");
            return 1;
        }

        var destino = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WebServiceAlerter",
            "discord.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

            var contenido =
                "{\r\n" +
                "  \"Discord\": {\r\n" +
                "    \"Enabled\": true,\r\n" +
                $"    \"Identity\": {JsonSerializer.Serialize(identidad)},\r\n" +
                $"    \"ProtectedWebhookUrl\": \"{PasswordProtector.Protect(webhook)}\"\r\n" +
                "  }\r\n" +
                "}\r\n";

            File.WriteAllText(destino, contenido);

            Console.WriteLine();
            Console.WriteLine($"Guardado en:  {destino}");
            Console.WriteLine();
            Console.WriteLine("El instalador no toca ese archivo, así que sobrevive a las actualizaciones.");
            Console.WriteLine("La URL queda cifrada contra esta máquina: hay que configurarla en cada equipo.");
            Console.WriteLine();
            Console.WriteLine("Probalo con:  WebServiceAlerter.exe --test-discord");
            return 0;
        }
        catch (Exception ex)
        {
            ExplainWriteFailure(ex, destino);
            return 1;
        }
    }

    private static void WriteLabelled(string label, string value, ConsoleColor color)
    {
        Console.Write($"{label}: ");
        Write(value + "\n", color);
    }

    private static void Write(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}
