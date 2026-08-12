using System.Reflection;

namespace WebServiceAlerter.Viewer;

/// <summary>
/// Lo que el semáforo puede mostrar. Es deliberadamente más chico que el modelo interno del
/// servicio: al usuario le sirve un color y una frase, no una taxonomía.
/// </summary>
public enum DisplayState
{
    Ok,

    /// <summary>Amarillo: responde lento, o falla de a ratos sin llegar a caerse.</summary>
    Warning,

    Down,

    /// <summary>Gris: sin conexión, sin datos, o el servicio de monitoreo no está corriendo.</summary>
    Unknown,
}

public static class DisplayStateExtensions
{
    public static Color ToColor(this DisplayState state) => state switch
    {
        DisplayState.Ok => Color.FromArgb(34, 197, 94),
        DisplayState.Warning => Color.FromArgb(245, 158, 11),
        DisplayState.Down => Color.FromArgb(239, 68, 68),
        _ => Color.FromArgb(156, 163, 175),
    };

    public static string ToTitle(this DisplayState state) => state switch
    {
        DisplayState.Ok => "FUNCIONANDO",
        DisplayState.Warning => "CON PROBLEMAS",
        DisplayState.Down => "CAÍDO",
        _ => "SIN DATOS",
    };

    /// <summary>El peor estado manda: si un servicio está caído, el semáforo general está rojo.</summary>
    public static DisplayState Worst(IEnumerable<DisplayState> states)
    {
        var worst = DisplayState.Unknown;
        var seenAny = false;

        foreach (var state in states)
        {
            seenAny = true;
            if (Rank(state) > Rank(worst) || worst == DisplayState.Unknown && state == DisplayState.Ok)
            {
                worst = state;
            }
        }

        return seenAny ? worst : DisplayState.Unknown;
    }

    private static int Rank(DisplayState state) => state switch
    {
        DisplayState.Ok => 0,
        DisplayState.Unknown => 1,
        DisplayState.Warning => 2,
        DisplayState.Down => 3,
        _ => 0,
    };
}

/// <summary>Carga los iconos embebidos, uno por estado.</summary>
public static class TrayIcons
{
    private static readonly Dictionary<DisplayState, Icon> Cache = new();

    public static Icon For(DisplayState state)
    {
        if (Cache.TryGetValue(state, out var cached))
        {
            return cached;
        }

        var name = state switch
        {
            DisplayState.Ok => "tray-ok.ico",
            DisplayState.Warning => "tray-slow.ico",
            DisplayState.Down => "tray-down.ico",
            _ => "tray-offline.ico",
        };

        var icon = Load(name) ?? SystemIcons.Application;
        Cache[state] = icon;
        return icon;
    }

    public static Icon? Application() => Load("icon.ico");

    private static Icon? Load(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        return stream is null ? null : new Icon(stream);
    }
}
