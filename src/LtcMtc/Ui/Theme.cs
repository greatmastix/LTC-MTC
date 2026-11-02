using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LtcMtc;

/// <summary>The MediaMTXUI dark palette: near-black surfaces, one red accent.</summary>
static class Theme
{
    public static readonly Color Background = Hex(0x0c0c0e), Card = Hex(0x151517), Muted = Hex(0x1c1c1f), Accent = Hex(0x252529),
        Border = Hex(0x2a2a2f), Foreground = Hex(0xf3f3f4), MutedForeground = Hex(0xb3b3ba), Primary = Hex(0xd6323d),
        Signal = Hex(0xef4b55), Good = Hex(0x0ca30c), Warning = Hex(0xfab219), Destructive = Hex(0xff5f68);

    public static readonly Font Body = new("Segoe UI", 9.75f), Small = new("Segoe UI Semibold", 8.25f),
        Title = new("Segoe UI Semibold", 14f), Mono = MonoFont(10f), Big = MonoFont(56f, FontStyle.Bold);

    static Color Hex(int rgb) => Color.FromArgb(255, Color.FromArgb(rgb));

    static Font MonoFont(float size, FontStyle style = FontStyle.Regular) =>
        new(FontFamily.Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas", size, style);

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Dark title bar, caption in the page colour (Windows 11; ignored elsewhere).</summary>
    public static void DarkTitleBar(nint hwnd)
    {
        int on = 1, caption = 0x0e0c0c, border = 0x2f2a2a;   // COLORREF is 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);
        DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
        DwmSetWindowAttribute(hwnd, 34, ref border, 4);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}

/// <summary>A rounded card with a small-caps section title behind a red bar, as on the MediaMTXUI dashboard.</summary>
sealed class Card : Panel
{
    readonly string title;

    public Card(string title)
    {
        this.title = title.ToUpperInvariant();
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Background;
        Padding = new Padding(18, 46, 18, 18);
        Margin = new Padding(0, 0, 0, 16);
    }

    // The card's own background is the page colour (for the rounded corners); its contents sit on the card colour.
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is { } c and not Button) c.BackColor = Theme.Card;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float s = DeviceDpi / 96f;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12 * s))
        using (var fill = new SolidBrush(Theme.Card))
        using (var pen = new Pen(Theme.Border))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        using (var bar = new SolidBrush(Theme.Signal)) using (var p = Theme.Rounded(new RectangleF(18 * s, 19 * s, 3 * s, 13 * s), 1.2f * s)) g.FillPath(bar, p);
        TextRenderer.DrawText(g, title, Theme.Small, new Point((int)(28 * s), (int)(17 * s)), Theme.MutedForeground, Theme.Card);
    }
}

/// <summary>A state as the dashboard shows it: a neutral pill with a coloured dot and a word, never colour alone.</summary>
sealed class Pill : Control
{
    Tone tone;

    public Pill()
    {
        DoubleBuffered = true;
        Font = Theme.Body;
        ForeColor = Theme.Foreground;
        BackColor = Theme.Background;
    }

    public void Set(string text, Tone t)
    {
        if (text == Text && t == tone) return;
        (Text, tone) = (text, t);
        float s = DeviceDpi / 96f;
        Size = new Size(TextRenderer.MeasureText(text, Font).Width + (int)(34 * s), (int)(28 * s));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float s = DeviceDpi / 96f;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Theme.Accent)) using (var p = Theme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f)) g.FillPath(bg, p);
        var dot = tone switch { Tone.Good => Theme.Good, Tone.Warning => Theme.Warning, _ => Theme.MutedForeground };
        using (var b = new SolidBrush(dot)) g.FillEllipse(b, 11 * s, Height / 2f - 4 * s, 8 * s, 8 * s);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(24 * s), 0, Width, Height), ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

/// <summary>A sub-heading inside a card: small caps in the muted colour, then a hairline to the right edge. The red bar
/// stays reserved for card titles, so the two levels read differently.</summary>
sealed class SectionTitle : Control
{
    public SectionTitle(string text)
    {
        Text = text.ToUpperInvariant();
        Font = Theme.Small;
        Height = 34;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Margin = new Padding(0, 6, 0, 2);
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var size = TextRenderer.MeasureText(Text, Font);
        int y = Height - size.Height - 2;
        TextRenderer.DrawText(e.Graphics, Text, Font, new Point(0, y), Theme.MutedForeground, BackColor);
        using var pen = new Pen(Theme.Border);
        int lineY = y + size.Height / 2, x = size.Width + (int)(6 * DeviceDpi / 96f);
        e.Graphics.DrawLine(pen, x, lineY, Width, lineY);
    }
}

/// <summary>
/// A big timecode that stays smooth at any size: each character is rendered once into a cached bitmap for the current
/// size and colour, so a repaint is eleven small blits instead of re-rasterising huge glyphs (which GDI doesn't cache).
/// </summary>
sealed class TimecodeView : Control
{
    const string Glyphs = "0123456789:-";
    readonly Dictionary<char, Bitmap> cache = [];
    string text = "--:--:--:--";
    Color color = Theme.MutedForeground;
    Size cell;

    public TimecodeView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
        BackColor = Theme.Background;
    }

    public void Set(string tc, Color c)
    {
        if (tc == text && c == color) return;
        if (c != color) Clear();
        (text, color) = (tc, c);
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        Clear();
        base.OnResize(e);
    }

    void Clear()
    {
        foreach (var b in cache.Values) b.Dispose();
        cache.Clear();
    }

    /// <summary>Renders the glyphs at the largest size where "00:00:00:00" fits the control.</summary>
    void Build()
    {
        using var probe = new Font(Theme.Big.FontFamily, 100f, FontStyle.Bold);
        var size = TextRenderer.MeasureText("00:00:00:00", probe, Size.Empty, TextFormatFlags.NoPadding);
        float pt = Math.Max(6f, 100f * Math.Min(Width * 0.94f / size.Width, Height * 0.9f / size.Height));
        using var font = new Font(Theme.Big.FontFamily, pt, FontStyle.Bold);
        var full = TextRenderer.MeasureText("00:00:00:00", font, Size.Empty, TextFormatFlags.NoPadding);
        cell = new Size(Math.Max(1, (int)Math.Ceiling(full.Width / 11.0)), Math.Max(1, full.Height));
        foreach (char ch in Glyphs)
        {
            var bmp = new Bitmap(cell.Width, cell.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(BackColor);
                TextRenderer.DrawText(g, ch.ToString(), font, new Rectangle(Point.Empty, cell), color, BackColor,
                    TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            cache[ch] = bmp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (Width < 20 || Height < 10) return;
        if (cache.Count == 0) Build();
        int x = (Width - cell.Width * text.Length) / 2, y = (Height - cell.Height) / 2;
        foreach (char ch in text)
        {
            if (cache.TryGetValue(ch, out var b)) e.Graphics.DrawImageUnscaled(b, x, y);
            x += cell.Width;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Clear();
        base.Dispose(disposing);
    }
}
