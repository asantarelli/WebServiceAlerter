using System.Net;
using System.Net.Sockets;
using System.Text;

// Simulador del FEDummy de ARCA, para probar el monitor sin esperar a que ARCA se caiga.
//
// Existe por un motivo concreto: la falla que justifica todo el proyecto —el servicio contesta
// HTTP 200 pero informa que su base de datos está caída— no se puede reproducir tocando el
// archivo hosts ni apagando nada. Un endpoint muerto prueba el camino fácil; éste prueba el
// difícil, que es el único que un chequeo de disponibilidad común no ve.
//
// Uso interactivo:   dotnet run --project tools/FakeArca
//   a / d / u   alternan AppServer / DbServer / AuthServer entre OK y ERROR
//   s           alterna respuesta lenta (para provocar el estado DEGRADADO)
//   x           alterna error HTTP 500
//   q           salir
//
// Uso scripteable: los parámetros de la query mandan sobre el estado interactivo, así que se
// puede forzar un escenario puntual sin tocar el teclado:
//   http://127.0.0.1:8099/?db=ERROR
//   http://127.0.0.1:8099/?delay=5000
//   http://127.0.0.1:8099/?status=500

var port = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 8099;

var state = new ServerState();
using var cts = new CancellationTokenSource();

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();

Console.WriteLine($"Simulador de ARCA escuchando en http://127.0.0.1:{port}/");
Console.WriteLine("Teclas:  a=AppServer  d=DbServer  u=AuthServer  s=lento  x=HTTP 500  q=salir");
Console.WriteLine();
state.Print();

var serving = ServeAsync(listener, state, cts.Token);

// Sin consola interactiva (lanzado desde un script, o con la entrada redirigida) no hay teclas
// que leer: se queda sirviendo y el escenario se elige por query string.
if (Console.IsInputRedirected)
{
    Console.WriteLine("Entrada redirigida: modo no interactivo, usá los parámetros de la query.");
    await serving;
    return 0;
}

while (!cts.IsCancellationRequested)
{
    var key = Console.ReadKey(intercept: true).KeyChar;

    switch (char.ToLowerInvariant(key))
    {
        case 'a': state.AppServer = Toggle(state.AppServer); state.Print(); break;
        case 'd': state.DbServer = Toggle(state.DbServer); state.Print(); break;
        case 'u': state.AuthServer = Toggle(state.AuthServer); state.Print(); break;
        case 's': state.Slow = !state.Slow; state.Print(); break;
        case 'x': state.HttpError = !state.HttpError; state.Print(); break;
        case 'q': cts.Cancel(); break;
    }
}

listener.Stop();
await serving;
Console.WriteLine("Simulador detenido.");
return 0;

static string Toggle(string value) => value == "OK" ? "ERROR" : "OK";

static async Task ServeAsync(TcpListener listener, ServerState state, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        TcpClient client;
        try
        {
            client = await listener.AcceptTcpClientAsync(token);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            return;
        }

        _ = HandleAsync(client, state, token);
    }
}

static async Task HandleAsync(TcpClient client, ServerState state, CancellationToken token)
{
    using (client)
    {
        try
        {
            using var stream = client.GetStream();

            var requestLine = await ReadRequestAsync(stream, token);
            var query = ParseQuery(requestLine);

            var appServer = query.GetValueOrDefault("app") ?? state.AppServer;
            var dbServer = query.GetValueOrDefault("db") ?? state.DbServer;
            var authServer = query.GetValueOrDefault("auth") ?? state.AuthServer;

            var delay = int.TryParse(query.GetValueOrDefault("delay"), out var ms) ? ms
                      : state.Slow ? 6000
                      : 0;

            var status = int.TryParse(query.GetValueOrDefault("status"), out var code) ? code
                       : state.HttpError ? 500
                       : 200;

            if (delay > 0)
            {
                await Task.Delay(delay, token);
            }

            var body = status == 200
                ? $"""
                   <?xml version="1.0" encoding="utf-8"?>
                   <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                     <soap:Body>
                       <FEDummyResponse xmlns="http://ar.gov.afip.dif.FEV1/">
                         <FEDummyResult>
                           <AppServer>{appServer}</AppServer>
                           <DbServer>{dbServer}</DbServer>
                           <AuthServer>{authServer}</AuthServer>
                         </FEDummyResult>
                       </FEDummyResponse>
                     </soap:Body>
                   </soap:Envelope>
                   """
                : "<html><body>Simulated server error</body></html>";

            var bytes = Encoding.UTF8.GetBytes(body);
            var reason = status == 200 ? "OK" : "Internal Server Error";
            var contentType = status == 200 ? "text/xml; charset=utf-8" : "text/html";

            var header =
                $"HTTP/1.1 {status} {reason}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {bytes.Length}\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);

            Console.WriteLine(
                $"  {DateTime.Now:HH:mm:ss}  ->  {status}  App={appServer} Db={dbServer} Auth={authServer}" +
                (delay > 0 ? $"  (+{delay} ms)" : ""));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
        {
            // Client hung up mid-exchange; nothing worth reporting in a test tool.
        }
    }
}

/// <summary>Reads headers (and drains the body) and returns the request line.</summary>
static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken token)
{
    var buffer = new byte[8192];
    var received = new StringBuilder();
    var total = 0;

    while (total < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(total), token);
        if (read == 0)
        {
            break;
        }

        total += read;
        received.Clear();
        received.Append(Encoding.UTF8.GetString(buffer, 0, total));

        var text = received.ToString();
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
        {
            continue;
        }

        // Drain the declared body so the client is not left writing into a closed pipe.
        var contentLength = 0;
        foreach (var line in text[..headerEnd].Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
            }
        }

        var bodyReceived = total - (headerEnd + 4);
        while (bodyReceived < contentLength)
        {
            var read2 = await stream.ReadAsync(buffer, token);
            if (read2 == 0)
            {
                break;
            }

            bodyReceived += read2;
        }

        break;
    }

    var full = received.ToString();
    var newline = full.IndexOf("\r\n", StringComparison.Ordinal);
    return newline > 0 ? full[..newline] : full;
}

static Dictionary<string, string> ParseQuery(string requestLine)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    var parts = requestLine.Split(' ');
    if (parts.Length < 2)
    {
        return result;
    }

    var questionMark = parts[1].IndexOf('?');
    if (questionMark < 0)
    {
        return result;
    }

    foreach (var pair in parts[1][(questionMark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var equals = pair.IndexOf('=');
        if (equals > 0)
        {
            result[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
    }

    return result;
}

sealed class ServerState
{
    public string AppServer = "OK";
    public string DbServer = "OK";
    public string AuthServer = "OK";
    public bool Slow;
    public bool HttpError;

    public void Print()
    {
        Console.WriteLine(
            $"[estado]  AppServer={AppServer}  DbServer={DbServer}  AuthServer={AuthServer}" +
            $"  lento={(Slow ? "sí" : "no")}  HTTP500={(HttpError ? "sí" : "no")}");
    }
}
