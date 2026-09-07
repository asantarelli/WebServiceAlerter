using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using WebServiceAlerter.Viewer.Data;

namespace WebServiceAlerter.Viewer;

/// <summary>
/// Lo poco que el cliente puede configurar. Deliberadamente chica: no expone la casilla de envío
/// ni las URLs monitoreadas, y sólo muestra parámetros que el servicio relee en caliente — el
/// cliente no puede reiniciar un servicio de Windows, así que un campo que exija reinicio sería
/// una promesa falsa.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly UserSettingsStore _store;

    private readonly Panel _content;
    private readonly TextBox _siteName;
    private readonly TextBox _recipients;
    private readonly CheckBox _mailEnabled;
    private readonly CheckBox _discordEnabled;
    private readonly NumericUpDown _interval;
    private readonly NumericUpDown _failures;
    private readonly NumericUpDown _successes;
    private readonly NumericUpDown _reminder;
    private readonly Button _testMail;
    private readonly Label _feedback;

    private int _y;

    public SettingsForm(UserSettingsStore store)
    {
        _store = store;

        Text = "Configuración";
        ClientSize = new Size(620, 650);
        MinimumSize = new Size(560, 420);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;

        var icon = TrayIcons.Application();
        if (icon is not null)
        {
            Icon = icon;
        }

        // Contenido desplazable y botonera anclada abajo. Posicionar a mano contra Width/Height
        // era el bug anterior: esas son las medidas exteriores de la ventana, no las del área de
        // contenido, así que los botones caían encima de los campos. Con paneles acoplados la
        // ubicación sale bien sola, y además aguanta que el usuario tenga otro escalado de
        // pantalla o agrande la ventana.
        _content = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(20, 14, 20, 8),
            BackColor = Color.White,
        };

        var buttonBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            BackColor = Color.FromArgb(249, 250, 251),
            Padding = new Padding(12, 12, 20, 12),
        };

        var cancel = new Button
        {
            Text = "Cancelar",
            Width = 110,
            Height = 32,
            Dock = DockStyle.Right,
            DialogResult = DialogResult.Cancel,
        };

        var separator = new Panel { Width = 10, Dock = DockStyle.Right };

        var save = new Button
        {
            Text = "Guardar",
            Width = 110,
            Height = 32,
            Dock = DockStyle.Right,
            DialogResult = DialogResult.OK,
        };
        save.Click += (_, _) => Save();

        buttonBar.Controls.Add(cancel);
        buttonBar.Controls.Add(separator);
        buttonBar.Controls.Add(save);

        // El Fill se agrega primero para que la botonera reserve su espacio y el contenido ocupe
        // el resto: WinForms acopla en orden inverso al de la colección.
        Controls.Add(_content);
        Controls.Add(buttonBar);

        AcceptButton = save;
        CancelButton = cancel;

        _y = 0;

        AddSectionTitle("Identificación");
        AddLabel("Nombre de este equipo", "Aparece en el asunto de cada alerta, para saber de qué instalación viene.");
        _siteName = AddTextBox();

        _y += 12;
        AddSectionTitle("Avisos");

        AddLabel("Qué avisos manda ESTE equipo",
                 "En el servidor van encendidos. En las terminales, apagados: ven el semáforo y el " +
                 "gráfico, pero no avisan — así una misma caída no dispara un aviso por máquina.");

        _mailEnabled = AddCheckBox("Enviar alertas por mail");
        _discordEnabled = AddCheckBox("Publicar en el canal de Discord");

        if (!store.DiscordConfigured)
        {
            _discordEnabled.Enabled = false;
            _discordEnabled.Text = "Publicar en el canal de Discord  (no configurado en este equipo)";
        }

        _y += 6;
        AddLabel("Mails de destino", "Separados por coma. La dirección desde la que se envía se configura aparte.");
        _recipients = AddTextBox();

        _testMail = new Button { Text = "Enviar mail de prueba", Left = 4, Top = _y, Width = 180, Height = 30 };
        _testMail.Click += async (_, _) => await SendTestMailAsync();
        _content.Controls.Add(_testMail);

        _feedback = new Label
        {
            Left = 194,
            Top = _y + 7,
            Width = 380,
            Height = 34,
            ForeColor = Color.FromArgb(107, 114, 128),
        };
        _content.Controls.Add(_feedback);
        _y += 46;

        _y += 12;
        AddSectionTitle("Sensibilidad");
        AddLabel("Cada cuántos segundos se chequea cada servicio",
                 "Más seguido detecta antes las caídas cortas, pero consulta más a servidores ajenos.");
        _interval = AddNumeric(5, 3600);

        AddLabel("Cuántos chequeos fallidos seguidos hacen falta para avisar que algo se cayó",
                 "Más bajo avisa antes, pero puede avisar por un tropiezo momentáneo.");
        _failures = AddNumeric(1, 10);

        AddLabel("Cuántos chequeos correctos seguidos hacen falta para darlo por recuperado", null);
        _successes = AddNumeric(1, 10);

        AddLabel("Cada cuántos minutos repetir el aviso mientras siga caído", null);
        _reminder = AddNumeric(1, 240);

        Load += (_, _) => LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _store.Read();

        _siteName.Text = settings.SiteName;
        _recipients.Text = settings.Recipients;
        _mailEnabled.Checked = settings.MailEnabled;
        _discordEnabled.Checked = settings.DiscordEnabled && _discordEnabled.Enabled;
        _interval.Value = Math.Clamp(settings.IntervalSeconds, 5, 3600);
        _failures.Value = Math.Clamp(settings.FailuresToAlert, 1, 10);
        _successes.Value = Math.Clamp(settings.SuccessesToRecover, 1, 10);
        _reminder.Value = Math.Clamp(settings.ReminderIntervalMinutes, 1, 240);
    }

    private bool Save()
    {
        var invalidos = InvalidAddresses(_recipients.Text);
        if (invalidos.Count > 0)
        {
            // Un typo en un destinatario deja la instalación muda y nadie se entera hasta la
            // caída real, así que se valida antes de guardar en vez de fallar en silencio.
            MessageBox.Show(
                this,
                $"Estas direcciones no parecen válidas:\r\n\r\n  {string.Join("\r\n  ", invalidos)}",
                "Revisá los destinatarios",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            DialogResult = DialogResult.None;
            return false;
        }

        try
        {
            _store.Write(new UserSettings
            {
                SiteName = _siteName.Text.Trim(),
                Recipients = _recipients.Text.Trim(),
                MailEnabled = _mailEnabled.Checked,
                DiscordEnabled = _discordEnabled.Checked,
                IntervalSeconds = (int)_interval.Value,
                FailuresToAlert = (int)_failures.Value,
                SuccessesToRecover = (int)_successes.Value,
                ReminderIntervalMinutes = (int)_reminder.Value,
            });

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No pude guardar la configuración:\r\n\r\n{ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.None;
            return false;
        }
    }

    private static List<string> InvalidAddresses(string value) =>
        value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
             .Where(direccion => !Regex.IsMatch(direccion, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
             .ToList();

    /// <summary>
    /// Guarda y delega el envío al ejecutable del servicio, que es quien tiene las credenciales.
    /// El Viewer nunca ve la casilla de envío ni su contraseña.
    /// </summary>
    private async Task SendTestMailAsync()
    {
        if (!Save())
        {
            return;
        }

        DialogResult = DialogResult.None;

        // Sin esto el envío fallaría en silencio y el usuario no tendría cómo saber que la causa
        // es la casilla de arriba, que él mismo acaba de destildar.
        if (!_mailEnabled.Checked)
        {
            ShowFeedback("El envío por mail está desactivado en este equipo. Marcá la casilla de arriba.", error: true);
            return;
        }

        var exe = FindServiceExecutable();

        if (exe is null)
        {
            ShowFeedback("No encontré WebServiceAlerter.exe. ¿Está instalado el servicio?", error: true);
            return;
        }

        _testMail.Enabled = false;
        ShowFeedback("Enviando…", error: false);

        try
        {
            var info = new ProcessStartInfo(exe, "--test-mail")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
            };

            using var proceso = Process.Start(info)!;

            var salida = new StringBuilder();
            salida.Append(await proceso.StandardOutput.ReadToEndAsync());
            salida.Append(await proceso.StandardError.ReadToEndAsync());

            await proceso.WaitForExitAsync();

            if (proceso.ExitCode == 0)
            {
                ShowFeedback("Enviado. Revisá la bandeja de entrada.", error: false);
                return;
            }

            // Mostrar el motivo real y no un genérico: sin esto, "no se pudo enviar" obliga a ir
            // a buscar el log para saber si fue la casilla, la red o un destinatario mal escrito.
            var motivo = salida.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .LastOrDefault(l => l.Length > 0);

            ShowFeedback(motivo is { Length: > 0 } ? motivo : "No se pudo enviar. Revisá el log.", error: true);
        }
        catch (Exception ex)
        {
            ShowFeedback(ex.Message, error: true);
        }
        finally
        {
            _testMail.Enabled = true;
        }
    }

    private void ShowFeedback(string text, bool error)
    {
        _feedback.ForeColor = error ? Color.FromArgb(185, 28, 28) : Color.FromArgb(21, 128, 61);
        _feedback.Text = text;
    }

    /// <summary>
    /// Instalado, el Viewer vive en una subcarpeta del servicio, así que alcanza con mirar el
    /// directorio padre. En desarrollo puede estar en cualquier lado, de modo que se sube por el
    /// árbol buscando también una carpeta publish.
    /// </summary>
    private static string? FindServiceExecutable()
    {
        const string nombre = "WebServiceAlerter.exe";
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);

        for (var nivel = 0; nivel < 6 && directorio is not null; nivel++)
        {
            var directo = System.IO.Path.Combine(directorio.FullName, nombre);
            if (File.Exists(directo))
            {
                return directo;
            }

            var publicado = System.IO.Path.Combine(directorio.FullName, "publish", nombre);
            if (File.Exists(publicado))
            {
                return publicado;
            }

            directorio = directorio.Parent;
        }

        return null;
    }

    // ---- Helpers de armado del formulario ------------------------------------------------

    private void AddSectionTitle(string text)
    {
        _content.Controls.Add(new Label
        {
            Text = text,
            Left = 0,
            Top = _y,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 99, 235),
        });

        _y += 28;
    }

    private void AddLabel(string text, string? hint)
    {
        _content.Controls.Add(new Label
        {
            Text = text,
            Left = 4,
            Top = _y,
            Width = 560,
            Height = 17,
            ForeColor = Color.FromArgb(31, 41, 55),
        });

        _y += 19;

        if (hint is not null)
        {
            _content.Controls.Add(new Label
            {
                Text = hint,
                Left = 4,
                Top = _y,
                Width = 560,
                Height = 16,
                Font = new Font(Font.FontFamily, 7.75f),
                ForeColor = Color.FromArgb(107, 114, 128),
            });

            _y += 18;
        }
    }

    private TextBox AddTextBox()
    {
        var box = new TextBox { Left = 4, Top = _y, Width = 555 };
        _content.Controls.Add(box);
        _y += 34;
        return box;
    }

    private CheckBox AddCheckBox(string text)
    {
        var control = new CheckBox { Text = text, Left = 4, Top = _y, Width = 555, Height = 22 };
        _content.Controls.Add(control);
        _y += 26;
        return control;
    }

    private NumericUpDown AddNumeric(int min, int max)
    {
        var control = new NumericUpDown { Left = 4, Top = _y, Width = 80, Minimum = min, Maximum = max };
        _content.Controls.Add(control);
        _y += 34;
        return control;
    }
}
