using Microsoft.Extensions.DependencyInjection;
using WebServiceAlerter.Alerting;
using WebServiceAlerter.Configuration;
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

        Console.WriteLine();
        Console.WriteLine("Pegá esto en Smtp:ProtectedPassword de appsettings.json:");
        Console.WriteLine();
        Console.WriteLine(PasswordProtector.Protect(password));
        Console.WriteLine();
        Console.WriteLine("Recordá que el blob sólo sirve en ESTA máquina: hay que regenerarlo en cada equipo.");
        return 0;

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
