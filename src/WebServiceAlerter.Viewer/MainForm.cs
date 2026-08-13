using ScottPlot.WinForms;
using WebServiceAlerter.Viewer.Data;

namespace WebServiceAlerter.Viewer;

/// <summary>
/// Ventana única: arriba el semáforo, en el medio la latencia, abajo los incidentes.
///
/// El orden no es casual. La pregunta que trae al usuario es "¿puedo facturar o no?", y ésa se
/// contesta con un color. El gráfico y el historial son para después, cuando ya sabe la respuesta
/// y quiere entender qué pasó.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly TimeSpan StatusRefresh = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ChartRefresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChartWindow = TimeSpan.FromHours(24);

    private readonly StatusReader _status;
    private readonly HistoryReader _history;
    private readonly UserSettingsStore _settings;

    private readonly Label _summaryLabel;
    private readonly Label _updatedLabel;
    private readonly Panel _cardsHost;
    private readonly FormsPlot _plot;
    private readonly ListView _incidents;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _chartTimer;

    private readonly Dictionary<string, EndpointCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    private string? _selectedEndpointId;
    private DisplayState _lastOverall = DisplayState.Unknown;
    private bool _reallyClosing;

    public MainForm(StatusReader status, HistoryReader history, UserSettingsStore settings)
    {
        _status = status;
        _history = history;
        _settings = settings;

        Text = "WebServiceAlerter";
        Width = 1060;
        Height = 720;
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(249, 250, 251);

        var appIcon = TrayIcons.Application();
        if (appIcon is not null)
        {
            Icon = appIcon;
        }

        // ---- Encabezado: el veredicto, en grande ------------------------------------------
        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Color.White };

        _summaryLabel = new Label
        {
            Left = 20,
            Top = 12,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 17f, FontStyle.Bold),
            Text = "Consultando…",
        };

        _updatedLabel = new Label
        {
            Left = 22,
            Top = 45,
            AutoSize = true,
            ForeColor = Color.FromArgb(107, 114, 128),
            Font = new Font(Font.FontFamily, 8.25f),
            Text = "",
        };

        var settingsButton = new Button
        {
            Text = "Configuración",
            Width = 130,
            Height = 30,
            Top = 20,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        settingsButton.Left = header.Width - settingsButton.Width - 20;
        settingsButton.Click += (_, _) => OpenSettings();

        header.Controls.Add(_summaryLabel);
        header.Controls.Add(_updatedLabel);
        header.Controls.Add(settingsButton);

        // ---- Semáforo ----------------------------------------------------------------------
        _cardsHost = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.White, AutoScroll = false };

        // ---- Incidentes --------------------------------------------------------------------
        var incidentsPanel = new Panel { Dock = DockStyle.Bottom, Height = 172, Padding = new Padding(12, 8, 12, 12) };

        var incidentsTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = "Incidentes de las últimas 24 horas",
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(55, 65, 81),
        };

        _incidents = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
        };

        _incidents.Columns.Add("Inicio", 130);
        _incidents.Columns.Add("Servicio", 260);
        _incidents.Columns.Add("Duración", 100);
        _incidents.Columns.Add("Motivo", 420);

        incidentsPanel.Controls.Add(_incidents);
        incidentsPanel.Controls.Add(incidentsTitle);

        // ---- Gráfico -----------------------------------------------------------------------
        var chartPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 4) };

        var chartTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = "Latencia",
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(55, 65, 81),
        };

        // Leyenda como texto y no como leyenda del gráfico: son marcas verticales, no series, y
        // explicarlas en una línea evita robarle espacio al gráfico.
        var chartLegend = new Label
        {
            Dock = DockStyle.Top,
            Height = 18,
            Text = "Marcas verticales:   amarillo = respuesta lenta      naranja = fallo aislado      rojo = incidente confirmado",
            Font = new Font(Font.FontFamily, 8.25f),
            ForeColor = Color.FromArgb(107, 114, 128),
        };

        _plot = new FormsPlot { Dock = DockStyle.Fill };
        chartPanel.Controls.Add(_plot);
        chartPanel.Controls.Add(chartLegend);
        chartPanel.Controls.Add(chartTitle);

        Controls.Add(chartPanel);
        Controls.Add(incidentsPanel);
        Controls.Add(_cardsHost);
        Controls.Add(header);

        // ---- Bandeja -------------------------------------------------------------------------
        _tray = new NotifyIcon
        {
            Icon = TrayIcons.For(DisplayState.Unknown),
            Visible = true,
            Text = "WebServiceAlerter",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Mostrar", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => { _reallyClosing = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        _statusTimer = new System.Windows.Forms.Timer { Interval = (int)StatusRefresh.TotalMilliseconds };
        _statusTimer.Tick += (_, _) => RefreshStatus();

        _chartTimer = new System.Windows.Forms.Timer { Interval = (int)ChartRefresh.TotalMilliseconds };
        _chartTimer.Tick += (_, _) => { RefreshChart(); RefreshIncidents(); };

        Load += (_, _) =>
        {
            RefreshStatus();
            RefreshChart();
            RefreshIncidents();
            _statusTimer.Start();
            _chartTimer.Start();
        };
    }

    /// <summary>
    /// Cerrar la ventana la manda a la bandeja en vez de terminar el programa: el icono de
    /// bandeja es el aviso principal, y perderlo por un clic en la X dejaría al usuario sin
    /// enterarse de nada. Para salir de verdad está la opción del menú.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyClosing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_settings);

        if (form.ShowDialog(this) == DialogResult.OK)
        {
            // El servicio vigila el archivo y lo relee solo; se refresca la pantalla para que el
            // nombre nuevo aparezca sin esperar al próximo tick.
            RefreshStatus();
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void RefreshStatus()
    {
        var snapshot = _status.TryRead();

        if (snapshot is null)
        {
            _summaryLabel.Text = "El monitor no está corriendo";
            _summaryLabel.ForeColor = DisplayState.Unknown.ToColor();
            _updatedLabel.Text = $"No encontré {_status.Path}. Arrancá el servicio o ejecutá WebServiceAlerter en una consola.";
            ApplyTray(DisplayState.Unknown, "El monitor no está corriendo");
            return;
        }

        var stale = snapshot.Age > EndpointView.StaleAfter;

        var views = snapshot.Endpoints
            .Select(e => EndpointView.Build(e, snapshot.HasInternet, stale, _history))
            .ToList();

        SyncCards(views);

        var overall = DisplayStateExtensions.Worst(views.Select(v => v.State));
        _summaryLabel.Text = BuildSummary(overall, views, stale, snapshot.HasInternet);
        _summaryLabel.ForeColor = overall.ToColor();

        _updatedLabel.Text = stale
            ? $"Último dato hace {(int)snapshot.Age.TotalMinutes} min — el monitor parece detenido."
            : $"Actualizado {snapshot.UpdatedAt.LocalDateTime:HH:mm:ss}   ·   {snapshot.Endpoints.Count} servicio(s) monitoreado(s)";

        ApplyTray(overall, _summaryLabel.Text);
    }

    private static string BuildSummary(DisplayState overall, List<EndpointView> views, bool stale, bool hasInternet)
    {
        if (stale)
        {
            return "El monitor no está corriendo";
        }

        if (!hasInternet)
        {
            return "Sin conexión a internet";
        }

        return overall switch
        {
            DisplayState.Down => views.Count(v => v.State == DisplayState.Down) == 1
                ? $"{views.First(v => v.State == DisplayState.Down).Name}: CAÍDO"
                : $"{views.Count(v => v.State == DisplayState.Down)} servicios caídos",
            DisplayState.Warning => "Funcionando con problemas",
            DisplayState.Ok => "Todos los servicios funcionando",
            _ => "Sin datos todavía",
        };
    }

    private void SyncCards(List<EndpointView> views)
    {
        foreach (var view in views)
        {
            _names[view.Id] = view.Name;

            if (!_cards.TryGetValue(view.Id, out var card))
            {
                card = new EndpointCard();
                card.Click += (_, _) => SelectEndpoint(view.Id);
                _cards[view.Id] = card;

                // Insertar al principio mantiene el orden de llegada con Dock=Top invertido,
                // así que se agregan y después se reordena por nombre.
                _cardsHost.Controls.Add(card);
            }

            card.Update(view);
            card.Selected = string.Equals(view.Id, _selectedEndpointId, StringComparison.OrdinalIgnoreCase);
        }

        // Dock=Top apila en orden inverso al de inserción: recorrer al revés deja el primer
        // servicio arriba.
        foreach (var card in _cards.Values.OrderByDescending(c => _names.GetValueOrDefault(c.EndpointId, "")))
        {
            card.BringToFront();
        }

        _cardsHost.Height = Math.Max(10, _cards.Count * 74);

        if (_selectedEndpointId is null && views.Count > 0)
        {
            SelectEndpoint(views[0].Id);
        }
    }

    private void SelectEndpoint(string endpointId)
    {
        _selectedEndpointId = endpointId;

        foreach (var (id, card) in _cards)
        {
            card.Selected = string.Equals(id, endpointId, StringComparison.OrdinalIgnoreCase);
        }

        RefreshChart();
    }

    private void RefreshChart()
    {
        var plot = _plot.Plot;
        plot.Clear();

        if (_selectedEndpointId is null)
        {
            _plot.Refresh();
            return;
        }

        var points = _history.GetLatency(_selectedEndpointId, ChartWindow);
        var answered = points.Where(p => p.LatencyMs is not null).ToList();

        // Los incidentes confirmados se cargan primero para poder distinguir, en el gráfico, un
        // fallo aislado de uno que formó parte de una caída real. Ver la diferencia de un vistazo
        // es lo que separa "ARCA hipó una vez" de "ARCA estuvo caído un minuto".
        var incidents = _history.GetIncidents(ChartWindow)
            .Where(i => string.Equals(i.EndpointId, _selectedEndpointId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        bool BelongsToIncident(DateTime at) =>
            incidents.Any(i => at >= i.StartedAt.AddSeconds(-1) && at <= (i.ResolvedAt ?? DateTime.Now).AddSeconds(1));

        // Los incidentes se dibujan como una banda que cubre toda su duración, no como una marca
        // puntual: lo que importa de una caída es cuánto duró, y una raya de un píxel no lo dice.
        // Se les da un ancho mínimo para que un corte de 30 segundos no quede invisible dentro de
        // una ventana de 24 horas.
        foreach (var incident in incidents)
        {
            var desde = incident.StartedAt;
            var hasta = incident.ResolvedAt ?? DateTime.Now;

            var minimo = TimeSpan.FromMinutes(3);
            if (hasta - desde < minimo)
            {
                var centro = desde + (hasta - desde) / 2;
                desde = centro - minimo / 2;
                hasta = centro + minimo / 2;
            }

            var banda = plot.Add.HorizontalSpan(desde.ToOADate(), hasta.ToOADate());
            banda.FillColor = new ScottPlot.Color(239, 68, 68).WithAlpha(0.22);
            banda.LineColor = new ScottPlot.Color(239, 68, 68).WithAlpha(0.55);
            banda.LineWidth = 1;
        }

        foreach (var point in points)
        {
            var isFailure = point.LatencyMs is null ||
                            (point.Outcome != HistoryReader.OutcomeOk &&
                             point.Outcome != HistoryReader.OutcomeSlow &&
                             point.Outcome != HistoryReader.OutcomeNotVerifiable);

            ScottPlot.Color color;
            float width;

            if (isFailure)
            {
                // Rojo si fue parte de un incidente confirmado; naranja si fue un fallo suelto.
                color = BelongsToIncident(point.At)
                    ? new ScottPlot.Color(220, 38, 38).WithAlpha(0.90)
                    : new ScottPlot.Color(234, 88, 12).WithAlpha(0.85);
                width = 5;
            }
            else if (point.Outcome == HistoryReader.OutcomeSlow)
            {
                color = new ScottPlot.Color(245, 158, 11).WithAlpha(0.75);
                width = 4;
            }
            else
            {
                continue;
            }

            var barra = plot.Add.VerticalLine(point.At.ToOADate());
            barra.Color = color;
            barra.LineWidth = width;
        }

        // La serie se dibuja al final para que quede por encima de las marcas de evento.
        if (answered.Count > 0)
        {
            var xs = answered.Select(p => p.At.ToOADate()).ToArray();
            var ys = answered.Select(p => p.LatencyMs!.Value).ToArray();

            var scatter = plot.Add.Scatter(xs, ys);
            scatter.Color = new ScottPlot.Color(37, 99, 235);
            scatter.MarkerSize = 0;
            scatter.LineWidth = 1.6f;
        }

        plot.Axes.DateTimeTicksBottom();
        plot.YLabel("milisegundos");
        plot.Axes.AutoScale();
        _plot.Refresh();
    }

    private void RefreshIncidents()
    {
        _incidents.BeginUpdate();
        _incidents.Items.Clear();

        foreach (var incident in _history.GetIncidents(TimeSpan.FromHours(24)))
        {
            var item = new ListViewItem(incident.StartedAt.ToString("dd/MM HH:mm:ss"));
            item.SubItems.Add(_names.GetValueOrDefault(incident.EndpointId, incident.EndpointId));
            item.SubItems.Add(FormatDuration(incident.Duration) + (incident.IsOpen ? " (en curso)" : ""));
            item.SubItems.Add(incident.Detail ?? "");
            item.ForeColor = incident.IsOpen ? Color.FromArgb(185, 28, 28) : Color.FromArgb(55, 65, 81);
            _incidents.Items.Add(item);
        }

        if (_incidents.Items.Count == 0)
        {
            var item = new ListViewItem("—");
            item.SubItems.Add("Sin incidentes en las últimas 24 horas");
            item.ForeColor = Color.FromArgb(107, 114, 128);
            _incidents.Items.Add(item);
        }

        _incidents.EndUpdate();
    }

    private void ApplyTray(DisplayState state, string summary)
    {
        _tray.Icon = TrayIcons.For(state);

        // El texto del tooltip de bandeja se corta a 63 caracteres en Windows.
        var text = $"WebServiceAlerter — {summary}";
        _tray.Text = text.Length > 63 ? text[..60] + "…" : text;

        if (state == _lastOverall)
        {
            return;
        }

        // Sólo se avisa al empeorar o al recuperarse, no en cada refresco.
        if (_lastOverall != DisplayState.Unknown || state == DisplayState.Down)
        {
            _tray.ShowBalloonTip(
                5000,
                state == DisplayState.Ok ? "Servicios recuperados" : "Atención",
                summary,
                state == DisplayState.Down ? ToolTipIcon.Error :
                state == DisplayState.Warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }

        _lastOverall = state;
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" :
        span.TotalMinutes >= 1 ? $"{span.Minutes} min {span.Seconds} s" :
        $"{span.Seconds} s";
}
