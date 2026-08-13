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

        var blob = PasswordProtector.Protect(password);
        var destino = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WebServiceAlerter",
            "smtp.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            File.WriteAllText(destino,
                "{\r\n  \"Smtp\": {\r\n    \"ProtectedPassword\": \"" + blob + "\"\r\n  }\r\n}\r\n");

            Console.WriteLine();
            Console.WriteLine($"Contraseña cifrada y guardada en:  {destino}");
            Console.WriteLine();
            Console.WriteLine("Ese archivo no lo toca el instalador, así que sobrevive a las actualizaciones.");
            Console.WriteLine("El blob sólo sirve en ESTA máquina: hay que generarlo una vez por equipo.");
            return 0;
        }
        catch (Exception ex)
        {
            // Si no se pudo escribir (permisos, disco), al menos se muestra para pegarlo a mano.
            Console.WriteLine();
            Console.WriteLine($"No pude escribir {destino}: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("Pegá esto a mano en Smtp:ProtectedPassword:");
            Console.WriteLine();
            Console.WriteLine(blob);
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
            return await TestMailAsync(services);
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
            Console.WriteLine($"      duró {AlertDispatcher.FormatDuration(duration)}{closing}");
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

    private static async Task<int> TestMailAsync(IServiceProvider services)
    {
        var sender = services.GetRequiredService<IAlertSender>();

        var sent = await sender.SendAsync(new AlertMessage
        {
            Kind = AlertKind.Test,
            Subject = "WebServiceAlerter — mail de prueba",
            Body =
                $"Este es un mail de prueba enviado desde {Environment.MachineName}.\r\n" +
                $"Fecha: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\r\n\r\n" +
                "Si lo estás leyendo, la configuración de destinatarios funciona.\r\n",
        }, CancellationToken.None);

        Console.WriteLine(sent
            ? "Mail de prueba enviado."
            : "No se pudo enviar. Revisá el log: casi siempre es SMTP sin configurar o la lista de destinatarios vacía.");

        return sent ? 0 : 1;
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
