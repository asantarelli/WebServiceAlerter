using WebServiceAlerter.Viewer;
using WebServiceAlerter.Viewer.Data;

// El Viewer no monitorea nada: lee lo que el servicio publica. Corre como usuario común, sin
// elevación, y puede abrirse y cerrarse sin afectar la medición.

var dataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WebServiceAlerter");

var statusPath = args.Length > 0 ? args[0] : Path.Combine(dataDirectory, "status.json");
var databasePath = args.Length > 1 ? args[1] : Path.Combine(dataDirectory, "webservicealerter.db");
var settingsPath = Path.Combine(dataDirectory, "usersettings.json");

ApplicationConfiguration.Initialize();
Application.Run(new MainForm(
    new StatusReader(statusPath),
    new HistoryReader(databasePath),
    new UserSettingsStore(settingsPath)));
