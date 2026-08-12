using System.ComponentModel;

namespace WebServiceAlerter.Viewer;

/// <summary>
/// Una fila del semáforo. Se dibuja a mano porque lo que importa es que se lea de lejos y de un
/// vistazo: un punto grande de color, el nombre, y una frase en castellano que diga qué pasa.
/// </summary>
public sealed class EndpointCard : Panel
{
    private EndpointView? _view;
    private bool _selected;

    public EndpointCard()
    {
        Height = 74;
        Dock = DockStyle.Top;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        Padding = new Padding(0);
    }

    public string EndpointId => _view?.Id ?? "";

    // Este control se construye por código y nunca pasa por el diseñador; sin esto el analizador
    // de WinForms exige que la propiedad declare cómo serializarse en un archivo .Designer.cs
    // que no existe.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            Invalidate();
        }
    }

    public void Update(EndpointView view)
    {
        _view = view;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var background = _selected ? Color.FromArgb(238, 242, 255) : Color.White;
        using (var brush = new SolidBrush(background))
        {
            g.FillRectangle(brush, ClientRectangle);
        }

        using (var pen = new Pen(Color.FromArgb(229, 231, 235)))
        {
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }

        if (_view is null)
        {
            return;
        }

        var color = _view.State.ToColor();

        // Barra de color a la izquierda: se distingue incluso de reojo.
        using (var brush = new SolidBrush(color))
        {
            g.FillRectangle(brush, 0, 0, 5, Height);
            g.FillEllipse(brush, 18, Height / 2 - 11, 22, 22);
        }

        using var nameFont = new Font(Font.FontFamily, 10.5f, FontStyle.Bold);
        using var stateFont = new Font(Font.FontFamily, 9.75f, FontStyle.Bold);
        using var detailFont = new Font(Font.FontFamily, 8.25f);

        var textLeft = 54;

        TextRenderer.DrawText(g, _view.Name, nameFont, new Point(textLeft, 10),
            Color.FromArgb(17, 24, 39), TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(g, _view.Headline, stateFont, new Point(textLeft, 31),
            color, TextFormatFlags.NoPrefix);

        var footer = BuildFooter(_view);
        if (footer.Length > 0)
        {
            TextRenderer.DrawText(g, footer, detailFont, new Point(textLeft, 51),
                Color.FromArgb(107, 114, 128), TextFormatFlags.NoPrefix);
        }

        // Latencia a la derecha, grande: es el número que se mira todo el tiempo.
        if (_view.LatencyMs is { } ms)
        {
            using var latencyFont = new Font(Font.FontFamily, 13f, FontStyle.Bold);
            var text = $"{ms:F0} ms";
            var size = TextRenderer.MeasureText(text, latencyFont);
            TextRenderer.DrawText(g, text, latencyFont,
                new Point(Width - size.Width - 18, Height / 2 - size.Height / 2),
                Color.FromArgb(55, 65, 81), TextFormatFlags.NoPrefix);
        }
    }

    private static string BuildFooter(EndpointView view)
    {
        var parts = new List<string>();

        if (view.State == DisplayState.Down && view.Since is { } since)
        {
            parts.Add($"desde las {since.LocalDateTime:HH:mm:ss} ({Format(DateTimeOffset.Now - since)})");
        }

        if (!string.IsNullOrWhiteSpace(view.Detail))
        {
            parts.Add(view.Detail!);
        }

        if (view.UptimePercent is { } uptime)
        {
            parts.Add($"disponibilidad 24 h: {uptime:P2}");
        }

        if (view.CertificateDaysToExpiry is { } days && days < 30)
        {
            parts.Add($"certificado vence en {days} días");
        }

        return string.Join("   ·   ", parts);
    }

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1 ? $"hace {(int)span.TotalHours} h {span.Minutes} min" :
        span.TotalMinutes >= 1 ? $"hace {span.Minutes} min" :
        $"hace {span.Seconds} s";
}
