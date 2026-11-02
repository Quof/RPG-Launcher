using System.Globalization;

namespace RpgLauncher;

/// <summary>
/// RPG Maker 2000-style drawing for VIPRPG mode, in canvas pixels: blue message windows, shadowed bitmap text,
/// the blinking selection cursor. Everything here is drawn with hard edges.
/// </summary>
internal static class RetroDraw
{
    // The default RTP system graphic's colours, give or take.
    public static readonly Color WindowTop = Color.FromRgb(40, 64, 168);
    public static readonly Color WindowBottom = Color.FromRgb(12, 20, 72);
    public static readonly Color FrameOuter = Color.FromRgb(8, 8, 24);
    public static readonly Color FrameLight = Color.FromRgb(232, 236, 255);
    public static readonly Color FrameMid = Color.FromRgb(136, 148, 208);
    public static readonly Color TextColor = Colors.White;
    public static readonly Color Shadow = Color.FromRgb(0, 0, 16);
    public static readonly Color System = Color.FromRgb(152, 184, 255);
    public static readonly Color Disabled = Color.FromRgb(140, 140, 156);
    public static readonly Color Yellow = Color.FromRgb(255, 240, 96);
    public static readonly Color Red = Color.FromRgb(255, 104, 104);
    public static readonly Color Green = Color.FromRgb(120, 240, 120);

    private static readonly Lazy<FontFamily> Family = new(PickFamily);
    private static readonly Dictionary<(string, double, Color), FormattedText> Texts = new();

    /// <summary>12 px: RPG Maker 2000's own text size.</summary>
    public static readonly double Small = 12;

    /// <summary>Large headings.</summary>
    public static readonly double Large = 24;

    private static FontFamily PickFamily()
    {
        // MS Gothic is the font RPG Maker 2000 draws with; it ships with Windows.
        string[] candidates = { "MS Gothic", "ＭＳ ゴシック", "MS UI Gothic", "IPAGothic", "VL Gothic", "Noto Sans CJK JP", "Noto Sans JP" };
        try
        {
            var installed = FontManager.Current.SystemFonts.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (installed.Contains(candidate)) return new FontFamily(candidate);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Font list unavailable: " + ex.Message);
        }
        return Look.FontFamily;
    }

    private static FormattedText Format(string text, double size, Color color, IBrush? brush = null)
    {
        var key = (text, size, color);
        if (Texts.TryGetValue(key, out var formatted)) return formatted;
        if (Texts.Count > 4000) Texts.Clear();
        formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Family.Value), size, brush ?? Look.Brush(color));
        Texts[key] = formatted;
        return formatted;
    }

    /// <summary>The system graphic's text colour standing for one of ours (normal, system, disabled…), or -1.</summary>
    private static int SkinColor(Color color) =>
        color == TextColor ? 0 : color == System ? 1 : color == Disabled ? 3 : color == Yellow ? 4 : color == Red ? 5 : color == Green ? 9 : -1;

    public static double Width(string text, double size = 12) =>
        string.IsNullOrEmpty(text) ? 0 : Math.Ceiling(Format(text, size, Colors.White).WidthIncludingTrailingWhitespace);

    /// <summary>Text with the one-pixel drop shadow RPG Maker gives all its text; returns the width drawn.</summary>
    public static double Text(DrawingContext g, string text, double x, double y, Color color, double size = 12, bool shadow = true)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        x = Math.Round(x);
        y = Math.Round(y);
        // With a system graphic, its gradient text colours and shadow, as in the games.
        if (VipSkin.Current is { } skin && SkinColor(color) is int index and >= 0)
        {
            if (shadow) g.DrawText(Format(text, size, skin.Shadow), new Point(x + 1, y + 1));
            var tinted = Color.FromArgb(254, (byte)index, 0, 0); // a cache key no real colour uses
            g.DrawText(Format(text, size, tinted, skin.Text(index)), new Point(x, y));
            return Width(text, size);
        }
        if (shadow) g.DrawText(Format(text, size, Shadow), new Point(x + 1, y + 1));
        g.DrawText(Format(text, size, color), new Point(x, y));
        return Width(text, size);
    }

    /// <summary>
    /// Title-logo text: a dark outline all round, the lower half in <paramref name="color"/> and the upper half in
    /// <paramref name="highlight"/>, like the logos on RPG Maker title screens. Returns the width.
    /// </summary>
    public static double Logo(DrawingContext g, string text, double x, double y, double size, Color color, Color highlight, Color outline)
    {
        x = Math.Round(x);
        y = Math.Round(y);
        var edge = Format(text, size, outline);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 2; dy++)
            {
                if (dx != 0 || dy != 0) g.DrawText(edge, new Point(x + dx, y + dy));
            }
        }
        var body = Format(text, size, color);
        g.DrawText(body, new Point(x, y));
        double width = body.WidthIncludingTrailingWhitespace;
        using (g.PushClip(new Rect(x - 2, y, width + 4, Math.Round(body.Height * 0.52))))
            g.DrawText(Format(text, size, highlight), new Point(x, y));
        return Math.Ceiling(width);
    }

    /// <summary>Text cut to fit a width, ending in "…" when cut.</summary>
    public static string Fit(string text, double width, double size = 12)
    {
        if (Width(text, size) <= width) return text;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (Width(text[..mid] + "…", size) <= width) low = mid;
            else high = mid - 1;
        }
        return text[..low] + "…";
    }

    public static void Fill(DrawingContext g, Rect rect, Color color) => g.FillRectangle(Look.Brush(color), Snap(rect));

    public static Rect Snap(Rect rect) =>
        new(Math.Round(rect.X), Math.Round(rect.Y), Math.Round(rect.Width), Math.Round(rect.Height));

    /// <summary>A hard one-pixel outline just inside the rectangle.</summary>
    public static void Outline(DrawingContext g, Rect rect, Color color)
    {
        rect = Snap(rect);
        var brush = Look.Brush(color);
        g.FillRectangle(brush, new Rect(rect.X, rect.Y, rect.Width, 1));
        g.FillRectangle(brush, new Rect(rect.X, rect.Bottom - 1, rect.Width, 1));
        g.FillRectangle(brush, new Rect(rect.X, rect.Y, 1, rect.Height));
        g.FillRectangle(brush, new Rect(rect.Right - 1, rect.Y, 1, rect.Height));
    }

    /// <summary>
    /// The RPG Maker message window: a blue gradient with faint scanlines inside a light, slightly rounded frame.
    /// <paramref name="top"/> and <paramref name="bottom"/> recolour the inside (matsuri themes).
    /// </summary>
    public static void Window(DrawingContext g, Rect rect, Color? top = null, Color? bottom = null, byte opacity = 255)
    {
        rect = Snap(rect);
        if (rect.Width < 8 || rect.Height < 8) return;
        if (VipSkin.Current is { } skin)
        {
            skin.Window(g, rect);
            return;
        }
        var inner = rect.Deflate(3);
        Color from = top ?? WindowTop, to = bottom ?? WindowBottom;
        // Banded gradient, four pixels a band, every other line a shade darker.
        for (double y = inner.Y; y < inner.Bottom; y += 4)
        {
            double t = inner.Height <= 4 ? 0 : (y - inner.Y) / (inner.Height - 4);
            var band = Look.Blend(from, to, t);
            g.FillRectangle(Look.Brush(Color.FromArgb(opacity, band.R, band.G, band.B)),
                new Rect(inner.X, y, inner.Width, Math.Min(4, inner.Bottom - y)));
        }
        for (double y = inner.Y + 1; y < inner.Bottom; y += 2)
            g.FillRectangle(Look.Brush(Color.FromArgb(28, 0, 0, 0)), new Rect(inner.X, y, inner.Width, 1));

        // Frame: dark edge, light bevel, mid line; corners clipped by a pixel.
        Outline(g, rect.Deflate(new Thickness(1, 0)), FrameOuter);
        Outline(g, rect.Deflate(new Thickness(0, 1)), FrameOuter);
        Outline(g, rect.Deflate(1), FrameLight);
        Outline(g, rect.Deflate(2), FrameMid);
        Fill(g, new Rect(rect.X + 1, rect.Y + 1, 1, 1), FrameMid);
        Fill(g, new Rect(rect.Right - 2, rect.Y + 1, 1, 1), FrameMid);
        Fill(g, new Rect(rect.X + 1, rect.Bottom - 2, 1, 1), FrameMid);
        Fill(g, new Rect(rect.Right - 2, rect.Bottom - 2, 1, 1), FrameMid);
    }

    /// <summary>The selection cursor: a light frame over a translucent fill that pulses with <paramref name="tick"/>.</summary>
    public static void Cursor(DrawingContext g, Rect rect, int tick, bool active = true)
    {
        rect = Snap(rect);
        if (VipSkin.Current is { } skin)
        {
            skin.Cursor(g, rect, tick, active);
            return;
        }
        double pulse = active ? (Math.Sin(tick * 0.35) + 1) / 2 : 0.3;
        byte fill = (byte)(40 + 60 * pulse);
        Fill(g, rect.Deflate(1), Color.FromArgb(fill, 200, 220, 255));
        var frame = Color.FromArgb((byte)(active ? 160 + 95 * pulse : 140), 240, 244, 255);
        Outline(g, rect.Deflate(new Thickness(1, 0)), frame);
        Outline(g, rect.Deflate(new Thickness(0, 1)), frame);
    }

    /// <summary>The little ▼ RPG Maker shows when a window has more below; it bobs with <paramref name="tick"/>.</summary>
    public static void MoreArrow(DrawingContext g, double centerX, double y, int tick, bool down = true)
    {
        y += tick / 4 % 2;
        if (VipSkin.Current is { } skin)
        {
            skin.Arrow(g, centerX, y - 2, down);
            return;
        }
        for (int i = 0; i < 4; i++)
        {
            double row = down ? y + i : y + 3 - i;
            Fill(g, new Rect(Math.Round(centerX) - 3 + i, row, 7 - 2 * i, 1), FrameLight);
        }
    }

    /// <summary>A bitmap drawn pixel for pixel (no scaling) at whole-pixel coordinates.</summary>
    public static void Sprite(DrawingContext g, Bitmap bitmap, Rect source, double x, double y, double scale = 1) =>
        g.DrawImage(bitmap, source, new Rect(Math.Round(x), Math.Round(y), source.Width * scale, source.Height * scale));
}
