using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebServiceAlerter;
using WebServiceAlerter.Alerting;
using WebServiceAlerter.Configuration;
using WebServiceAlerter.Data;
using WebServiceAlerter.Probes;
using WebServiceAlerter.Security;
using WebServiceAlerter.Status;

// --protect-password runs before anything else: it is a setup utility, not part of the service,
// and must work even when the rest of the configuration is still full of placeholders.
if (args.Contains("--protect-password"))
{
    return Cli.ProtectPassword(args);
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Configuration comes in two layers, split by owner rather than by machine:
//
//   appsettings.json   - ours. Shipped, overwritten on upgrade. SMTP account, service profiles,
//                        default thresholds.
//   usersettings.json  - the client's. Lives under ProgramData, never touched by the installer,
//                        edited through the Viewer: URLs and recipients.
//
// usersettings.json is watched for changes because the client is a standard user who cannot
// restart a Windows service; edits have to take effect on their own.
var programData = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WebServiceAlerter");
Directory.CreateDirectory(programData);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddJsonFile("profiles.json", optional: false, reloadOnChange: false)
    .AddJsonFile(new PhysicalFileProvider(programData), "usersettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.AddWindowsService(options => options.ServiceName = "WebServiceAlerter");

builder.Services.Configure<GeneralOptions>(builder.Configuration.GetSection(GeneralOptions.SectionName));
builder.Services.Configure<MonitoringOptions>(builder.Configuration.GetSection(MonitoringOptions.SectionName));
builder.Services.Configure<AlertingOptions>(builder.Configuration.GetSection(AlertingOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.Configure<HttpOptions>(builder.Configuration.GetSection(HttpOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<StatusFileOptions>(builder.Configuration.GetSection(StatusFileOptions.SectionName));

// Service profiles are plain data, bound once. This is the file colleagues contribute to.
var profiles = new Dictionary<string, ServiceProfile>(StringComparer.OrdinalIgnoreCase);
builder.Configuration.GetSection(ServiceProfile.SectionName).Bind(profiles);
builder.Services.AddSingleton<IReadOnlyDictionary<string, ServiceProfile>>(profiles);

builder.Services.AddSingleton<HttpTransport>();
builder.Services.AddSingleton<IProbe, HttpProbe>();
builder.Services.AddSingleton<IProbe, WsdlProbe>();
builder.Services.AddSingleton<IProbe, SoapDummyProbe>();
builder.Services.AddSingleton<IProbe, TcpProbe>();
builder.Services.AddSingleton<ProbeRunner>();

builder.Services.AddSingleton<WebServiceAlerter.Monitoring.CanaryChecker>();
builder.Services.AddSingleton<IAlertSender, SmtpAlertSender>();
builder.Services.AddSingleton<AlertDispatcher>();
builder.Services.AddSingleton<DataRecorder>();
builder.Services.AddSingleton<StatusWriter>();

var interactive = !WindowsServiceHelpers.IsWindowsService();
if (interactive)
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
}

// One-shot commands need the container but not the hosted service.
var oneShot = args.Any(a => a is "--once" or "--list" or "--test" or "--test-mail");
if (!oneShot)
{
    builder.Services.AddHostedService<Worker>();
}

var host = builder.Build();

if (oneShot)
{
    return await Cli.RunAsync(host.Services, args);
}

await host.RunAsync();
return 0;
