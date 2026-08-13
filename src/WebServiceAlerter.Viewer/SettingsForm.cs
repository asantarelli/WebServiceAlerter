using System.Diagnostics;
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

    private readonly TextBox _siteName;
    private readonly TextBox _recipients;
    private readonly NumericUpDown _failures;
    private readonly NumericUpDown _successes;
    private readonly NumericUpDown _reminder;
    private readonly Button _testMail;
    private readonly Label _feedback;

    public SettingsForm(UserSettingsStore store)
    {
        _store = store;

        Text = "Configuración";
        Width = 620;
        Height = 470;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;

        var icon = TrayIcons.Application();
        if (icon is not null)
        {
            Icon = icon;
        }

        var y = 18;

        AddSectionTitle("Identificación", ref y);
        AddLabel("Nombre de este equipo", "Aparece en el asunto de cada alerta, para saber de qué instalación viene.", ref y);
        _siteName = AddTextBox(ref y);

        y += 10;
        AddSectionTitle("Avisos", ref y);
        AddLabel("Mails de destino", "Separados por coma. La dirección desde la que se envía se configura aparte.", ref y);
        _recipients = AddTextBox(ref y);

        _testMail = new Button
        {
            Text = "Enviar mail de prueba",
            Left = 24,
            Top = y,
            Width = 180,
            Height = 28,
        };
        _testMail.Click += async (_, _) => await SendTestMailAsync();

        _feedback = new Label
        {
            Left = 214,
            Top = y + 6,
            Width = 360,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(107, 114, 128),
        };

        Controls.Add(_testMail);
        Controls.Add(_feedback);
        y += 44;

        AddSectionTitle("Sensibilidad", ref y);
        AddLabel("Cuántos chequeos fallidos seguidos hacen falta para avisar que algo se cayó",
                 "Más bajo avisa antes, pero puede avisar por un tropiezo momentáneo.", ref y);
        _failures = AddNumeric(1, 10, ref y);

        AddLabel("Cuántos chequeos correctos seguidos hacen falta para darlo por recuperado", null, ref y);
        _successes = AddNumeric(1, 10, ref y);

        AddLabel("Cada cuántos minutos repetir el aviso mientras siga caído", null, ref y);
        _reminder = AddNumeric(1, 240, ref y);

        var save = new Button
        {
            Text = "Guardar",
            Width = 110,
            Height = 30,
            Left = Width - 250,
            Top = Height - 82,
            DialogResult = DialogResult.OK,
        };
        save.Click += (_, _) => Save();

        var cancel = new Button
        {
            Text = "Cancelar",
            Width = 110,
            Height = 30,
            Left = Width - 132,
            Top = Height - 82,
            DialogResult = DialogResult.Cancel,
        };

        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;

        Load += (_, _) => LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _store.Read();

        _siteName.Text = settings.SiteName;
        _recipients.Text = settings.Recipients;
        _failures.Value = Math.Clamp(settings.FailuresToAlert, 1, 10);
        _successes.Value = Math.Clamp(settings.SuccessesToRecover, 1, 10);
        _reminder.Value = Math.Clamp(settings.ReminderIntervalMinutes, 1, 240);
    }

    private void Save()
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
            return;
        }

        try
        {
            _store.Write(new UserSettings
            {
                SiteName = _siteName.Text.Trim(),
                Recipients = _recipients.Text.Trim(),
                FailuresToAlert = (int)_failures.Value,
                SuccessesToRecover = (int)_successes.Value,
                ReminderIntervalMinutes = (int)_reminder.Value,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No pude guardar la configuración:\r\n\r\n{ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.None;
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
        var exe = FindServiceExecutable();

        if (exe is null)
        {
            _feedback.ForeColor = Color.FromArgb(185, 28, 28);
            _feedback.Text = "No encontré WebServiceAlerter.exe junto al Viewer.";
            return;
        }

        Save();

        _testMail.Enabled = false;
        _feedback.ForeColor = Color.FromArgb(107, 114, 128);
        _feedback.Text = "Enviando…";

        try
        {
            var proceso = Process.Start(new ProcessStartInfo(exe, "--test-mail")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            })!;

            await proceso.WaitForExitAsync();

            var ok = proceso.ExitCode == 0;
            _feedback.ForeColor = ok ? Color.FromArgb(21, 128, 61) : Color.FromArgb(185, 28, 28);
            _feedback.Text = ok
                ? "Enviado. Revisá la bandeja de entrada."
                : "No se pudo enviar. Revisá los destinatarios y el log.";
        }
        catch (Exception ex)
        {
            _feedback.ForeColor = Color.FromArgb(185, 28, 28);
            _feedback.Text = ex.Message;
        }
        finally
        {
            _testMail.Enabled = true;
        }
    }

    /// <summary>El Viewer se instala en una subcarpeta del servicio; en desarrollo puede estar
    /// en cualquier lado, así que se prueban las ubicaciones razonables.</summary>
    private static string? FindServiceExecutable()
    {
        var baseDir = AppContext.BaseDirectory;

        var candidatos = new[]
        {
            System.IO.Path.Combine(baseDir, "WebServiceAlerter.exe"),
            System.IO.Path.Combine(baseDir, "..", "WebServiceAlerter.exe"),
            System.IO.Path.Combine(baseDir, "..", "..", "WebServiceAlerter.exe"),
        };

        return candidatos.Select(System.IO.Path.GetFullPath).FirstOrDefault(System.IO.File.Exists);
    }

    // ---- Helpers de armado del formulario ------------------------------------------------

    private void AddSectionTitle(string text, ref int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            Left = 20,
            Top = y,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 99, 235),
        });

        y += 26;
    }

    private void AddLabel(string text, string? hint, ref int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            Left = 24,
            Top = y,
            Width = 560,
            Height = 16,
            ForeColor = Color.FromArgb(31, 41, 55),
        });

        y += 18;

        if (hint is not null)
        {
            Controls.Add(new Label
            {
                Text = hint,
                Left = 24,
                Top = y,
                Width = 560,
                Height = 15,
                Font = new Font(Font.FontFamily, 7.75f),
                ForeColor = Color.FromArgb(107, 114, 128),
            });

            y += 17;
        }
    }

    private TextBox AddTextBox(ref int y)
    {
        var box = new TextBox { Left = 24, Top = y, Width = 555 };
        Controls.Add(box);
        y += 32;
        return box;
    }

    private NumericUpDown AddNumeric(int min, int max, ref int y)
    {
        var control = new NumericUpDown { Left = 24, Top = y, Width = 80, Minimum = min, Maximum = max };
        Controls.Add(control);
        y += 30;
        return control;
    }
}
