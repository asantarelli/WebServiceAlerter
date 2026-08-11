using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Draws the WebServiceAlerter icon set and writes multi-resolution .ico files.
//
// Run it from the repo root:  dotnet run --project tools/IconGenerator -- assets
//
// The icon is drawn from scratch at every size (rather than rendered once and downscaled)
// because every dimension is expressed as a fraction of the canvas: strokes stay crisp at 16px
// instead of turning into grey mush. The tray variants tint the pulse line *and* the status dot,
// since at 16px a small coloured dot alone is not readable at a glance — and the whole point of
// the tray icon is to be readable at a glance.

var outputDir = args.Length > 0 ? args[0] : "assets";
Directory.CreateDirectory(outputDir);

// Background: deep blue -> indigo. Reads as "instrument panel", not as a corporate logo.
var backTop = ColorTranslator.FromHtml("#22337A");
var backBottom = ColorTranslator.FromHtml("#5B4BE0");
var shellDark = ColorTranslator.FromHtml("#151F4A");

var states = new (string Name, Color Pulse, Color Dot)[]
{
    ("tray-ok",      ColorTranslator.FromHtml("#8CF5C0"), ColorTranslator.FromHtml("#22C55E")),
    ("tray-slow",    ColorTranslator.FromHtml("#FFD98A"), ColorTranslator.FromHtml("#F59E0B")),
    ("tray-down",    ColorTranslator.FromHtml("#FFB0B0"), ColorTranslator.FromHtml("#EF4444")),
    ("tray-offline", ColorTranslator.FromHtml("#C3C8D4"), ColorTranslator.FromHtml("#9CA3AF")),
};

// Application icon: white pulse, green dot — the "everything is fine" look.
var appSizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var traySizes = new[] { 16, 20, 24, 32, 48 };

WriteIco(Path.Combine(outputDir, "icon.ico"), appSizes,
    size => Draw(size, Color.White, ColorTranslator.FromHtml("#22C55E")));

foreach (var (name, pulse, dot) in states)
{
    WriteIco(Path.Combine(outputDir, name + ".ico"), traySizes, size => Draw(size, pulse, dot));
}

// A PNG for the README / GitHub social preview.
using (var preview = Draw(256, Color.White, ColorTranslator.FromHtml("#22C55E")))
{
    preview.Save(Path.Combine(outputDir, "icon-256.png"), ImageFormat.Png);
}

// A strip of all four states side by side, to document what the tray icon means.
WriteStateStrip(Path.Combine(outputDir, "tray-states.png"), states);

Console.WriteLine($"Icons written to {Path.GetFullPath(outputDir)}");

Bitmap Draw(int size, Color pulseColor, Color dotColor)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;
    g.Clear(Color.Transparent);

    // Rounded-square badge, inset slightly so the antialiased edge is not clipped.
    var inset = size * 0.03f;
    var rect = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
    var radius = size * 0.235f;

    using (var path = RoundedRect(rect, radius))
    using (var brush = new LinearGradientBrush(
               new PointF(rect.Left, rect.Top), new PointF(rect.Right, rect.Bottom), backTop, backBottom))
    {
        g.FillPath(brush, path);
    }

    // ECG-style pulse. Flat, small dip, tall spike, dip, flat — the universal "this thing is
    // alive and I am measuring it" shape.
    var pts = new[]
    {
        Pt(0.09f, 0.55f), Pt(0.29f, 0.55f), Pt(0.37f, 0.70f),
        Pt(0.47f, 0.24f), Pt(0.56f, 0.63f), Pt(0.64f, 0.55f), Pt(0.91f, 0.55f),
    };

    using (var pen = new Pen(pulseColor, size * 0.105f)
           {
               LineJoin = LineJoin.Round,
               StartCap = LineCap.Round,
               EndCap = LineCap.Round,
           })
    {
        g.DrawLines(pen, pts);
    }

    // Status LED, bottom-right. The dark collar punches it out of the pulse line so the two
    // never blur together at small sizes.
    var cx = size * 0.735f;
    var cy = size * 0.745f;
    var dotRadius = size * 0.175f;
    var collar = dotRadius * 1.42f;

    using (var collarBrush = new SolidBrush(shellDark))
    {
        g.FillEllipse(collarBrush, cx - collar, cy - collar, collar * 2, collar * 2);
    }

    using (var dotBrush = new SolidBrush(dotColor))
    {
        g.FillEllipse(dotBrush, cx - dotRadius, cy - dotRadius, dotRadius * 2, dotRadius * 2);
    }

    return bmp;

    PointF Pt(float fx, float fy) => new(rect.Left + rect.Width * fx, rect.Top + rect.Height * fy);
}

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    var d = radius * 2;
    var path = new GraphicsPath();
    path.AddArc(r.Left, r.Top, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

void WriteStateStrip(string path, (string Name, Color Pulse, Color Dot)[] variants)
{
    const int cell = 64;
    const int pad = 12;
    var width = variants.Length * cell + (variants.Length + 1) * pad;
    using var strip = new Bitmap(width, cell + pad * 2, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(strip);
    g.Clear(Color.Transparent);

    for (var i = 0; i < variants.Length; i++)
    {
        using var icon = Draw(cell, variants[i].Pulse, variants[i].Dot);
        g.DrawImage(icon, pad + i * (cell + pad), pad);
    }

    strip.Save(path, ImageFormat.Png);
}

// ---------------------------------------------------------------------------
// ICO container writer.
//
// Frames at 64px and above are stored as PNG (supported since Windows Vista and far smaller on
// disk); smaller frames are stored as classic 32bpp DIBs, which every shell surface and installer
// handles without argument. Mixing the two is what the standard Windows icon files do.
// ---------------------------------------------------------------------------
static void WriteIco(string path, int[] sizes, Func<int, Bitmap> render)
{
    var frames = new List<byte[]>();
    var ordered = sizes.OrderBy(s => s).ToArray();

    foreach (var size in ordered)
    {
        using var bmp = render(size);
        frames.Add(size >= 64 ? EncodePng(bmp) : EncodeDib(bmp));
    }

    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);

    w.Write((ushort)0);                  // reserved
    w.Write((ushort)1);                  // type: icon
    w.Write((ushort)ordered.Length);

    var offset = 6 + 16 * ordered.Length;
    for (var i = 0; i < ordered.Length; i++)
    {
        var size = ordered[i];
        w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);                          // palette size
        w.Write((byte)0);                          // reserved
        w.Write((ushort)1);                        // colour planes
        w.Write((ushort)32);                       // bits per pixel
        w.Write(frames[i].Length);
        w.Write(offset);
        offset += frames[i].Length;
    }

    foreach (var frame in frames)
    {
        w.Write(frame);
    }
}

static byte[] EncodePng(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static byte[] EncodeDib(Bitmap bmp)
{
    var width = bmp.Width;
    var height = bmp.Height;

    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);

    // BITMAPINFOHEADER. Height is doubled because the DIB nominally holds the colour bitmap
    // stacked on top of the AND mask, even when the mask is unused.
    w.Write(40);
    w.Write(width);
    w.Write(height * 2);
    w.Write((ushort)1);
    w.Write((ushort)32);
    w.Write(0);                 // BI_RGB
    w.Write(width * height * 4);
    w.Write(0);
    w.Write(0);
    w.Write(0);
    w.Write(0);

    // Colour data, bottom-up, BGRA.
    var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        var row = new byte[width * 4];
        for (var y = height - 1; y >= 0; y--)
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
            w.Write(row);
        }
    }
    finally
    {
        bmp.UnlockBits(data);
    }

    // AND mask: all zeros (fully opaque). The 32bpp alpha channel is what actually gets used,
    // but the mask must still be present and 4-byte aligned per row.
    var maskStride = (width + 31) / 32 * 4;
    w.Write(new byte[maskStride * height]);

    return ms.ToArray();
}
