using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace RpgLauncher;

internal static class Theme
{
    private static Palette P = Palette.Default;

    /// <summary>The active style; set at startup and whenever the user picks one.</summary>
    public static UiStyle Style { get; private set; } = UiStyle.Default;

    public static void Apply(UiStyle style)
    {
        Style = style;
        P = Palette.For(style);
    }

    /// <summary>The active palette, e.g. to remap colors already set on controls.</summary>
    public static Palette Current => P;

    // Surfaces
    public static Color Bg => P.Bg;
    public static Color HeaderTop => P.HeaderTop;
    public static Color Card => P.Card;
    public static Color CardHover => P.CardHover;
    public static Color CardBorder => P.CardBorder;
    public static Color CardBorderHover => P.CardBorderHover;
    public static Color Field => P.Field;
    public static Color FieldHover => P.FieldHover;
    public static Color FieldBorder => P.FieldBorder;
    public static Color MenuBg => P.MenuBg;

    // Text
    public static Color Text => P.Text;
    public static Color TextMuted => P.TextMuted;
    public static Color TextDim => P.TextDim;

    // Accents ("Gold" is the accent color, gold only in the default style)
    public static Color Gold => P.Accent;
    public static Color GoldBright => P.AccentBright;
    public static Color AccentDim => P.AccentDim;
    public static Color AccentBorder => P.AccentBorder;
    public static Color AccentHover => P.AccentHover;
    public static Color StarFill => P.Star;
    public static Color StarFillBright => P.StarBright;
    public static Color Success => P.Success;
    public static Color StarEmpty => P.StarEmpty;
    public static Color Danger => P.Danger;
    public static Color DangerDim => P.DangerDim;
    public static Color DangerBorder => P.DangerBorder;

    // Chrome
    public static Color StatusBg => P.StatusBg;
    public static Color Divider => P.Divider;
    public static Color HeaderGlow => P.HeaderGlow;
    public static Color ScrollTrack => P.ScrollTrack;
    public static Color ScrollThumb => P.ScrollThumb;
    public static Color ScrollThumbActive => P.ScrollThumbActive;
    public static Color PlaceholderTop => P.PlaceholderTop;
    public static Color PlaceholderBottom => P.PlaceholderBottom;
    public static Color Canvas => P.Canvas;

    /// <summary>Outline width for cards.</summary>
    public static float BorderWidth => P.BorderWidth;

    /// <summary>Corner radius for cards, before DPI scaling.</summary>
    public static float CardRadius => P.CardRadius;

    /// <summary>Corner radius for buttons and fields.</summary>
    public static float ControlRadius => P.ControlRadius;

    public static bool IsLight => P.Light;

    /// <summary>Corner radius for the picture inside a card.</summary>
    public static float ImageRadius => Math.Max(0f, P.CardRadius - 5f);

    /// <summary>Corner radius for tag chips.</summary>
    public static float ChipRadius => Style switch
    {
        UiStyle.Angelic => 2f,
        UiStyle.Dark => 6f,
        UiStyle.Blob => 12f,
        _ => 9f
    };

    /// <summary>Outline width for buttons and fields.</summary>
    public static float ControlBorderWidth => P.Light ? 1.5f : 1f;

    private static readonly Lazy<string> LazyFamily = new(PickFamily);

    public static string FontFamily => LazyFamily.Value;

    public static Font Font(float points, FontStyle style = FontStyle.Regular)
        => new(FontFamily, points, style, GraphicsUnit.Point);

    private static string PickFamily()
    {
        try
        {
            using var installed = new InstalledFontCollection();
            var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in new[] { "Yu Gothic UI", "Meiryo UI", "Segoe UI", "Tahoma" })
            {
                if (names.Contains(candidate)) return candidate;
            }
        }
        catch
        {
            // ignore and use a safe default
        }
        return "Segoe UI";
    }

    public static float Scale(Control control) => control.DeviceDpi / 96f;

    public static int Px(Control control, float value)
        => Math.Max(1, (int)Math.Round(value * Scale(control)));

    public static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        radius = Math.Max(0f, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f));
        if (radius <= 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }

        float d = radius * 2f;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static GraphicsPath Star(RectangleF rect)
    {
        var points = new PointF[10];
        float cx = rect.X + rect.Width / 2f;
        float cy = rect.Y + rect.Height / 2f;
        float outer = Math.Min(rect.Width, rect.Height) / 2f;
        float inner = outer * 0.45f;
        for (int i = 0; i < 10; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI / 5;
            float radius = (i % 2 == 0) ? outer : inner;
            points[i] = new PointF(
                cx + (float)Math.Cos(angle) * radius,
                cy + (float)Math.Sin(angle) * radius);
        }
        var path = new GraphicsPath();
        path.AddPolygon(points);
        return path;
    }

    public static GraphicsPath Heart(RectangleF r)
    {
        float w = r.Width, h = r.Height;
        float x = r.X, y = r.Y;
        var path = new GraphicsPath();
        path.StartFigure();
        path.AddBezier(
            x + w * 0.50f, y + h * 0.92f,
            x + w * 0.04f, y + h * 0.60f,
            x + w * 0.02f, y + h * 0.28f,
            x + w * 0.24f, y + h * 0.17f);
        path.AddBezier(
            x + w * 0.24f, y + h * 0.17f,
            x + w * 0.36f, y + h * 0.08f,
            x + w * 0.48f, y + h * 0.15f,
            x + w * 0.50f, y + h * 0.28f);
        path.AddBezier(
            x + w * 0.50f, y + h * 0.28f,
            x + w * 0.52f, y + h * 0.15f,
            x + w * 0.64f, y + h * 0.08f,
            x + w * 0.76f, y + h * 0.17f);
        path.AddBezier(
            x + w * 0.76f, y + h * 0.17f,
            x + w * 0.98f, y + h * 0.28f,
            x + w * 0.96f, y + h * 0.60f,
            x + w * 0.50f, y + h * 0.92f);
        path.CloseFigure();
        return path;
    }

    public static void DrawText(
        Graphics g,
        string text,
        Font font,
        Color color,
        RectangleF rect,
        StringAlignment align = StringAlignment.Near,
        StringAlignment line = StringAlignment.Center,
        StringTrimming trim = StringTrimming.EllipsisCharacter)
    {
        g.DrawString(text ?? string.Empty, font, CachedBrush(color), rect, CachedFormat(align, line, trim));
    }

    // GDI+ objects aren't thread-safe and thumbnails draw text on a worker, so each thread has its own cache.
    [ThreadStatic] private static Dictionary<(StringAlignment, StringAlignment, StringTrimming), StringFormat>? _formats;
    [ThreadStatic] private static Dictionary<Color, SolidBrush>? _brushes;

    private static StringFormat CachedFormat(StringAlignment align, StringAlignment line, StringTrimming trim)
    {
        _formats ??= new();
        var key = (align, line, trim);
        if (!_formats.TryGetValue(key, out var format))
        {
            format = new StringFormat(StringFormatFlags.NoWrap)
            {
                Alignment = align,
                LineAlignment = line,
                Trimming = trim,
                FormatFlags = StringFormatFlags.NoWrap
            };
            _formats[key] = format;
        }
        return format;
    }

    private static SolidBrush CachedBrush(Color color)
    {
        _brushes ??= new();
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new SolidBrush(color);
            _brushes[color] = brush;
        }
        return brush;
    }

    public static void DrawCover(Graphics g, Image image, RectangleF dest, InterpolationMode mode = InterpolationMode.HighQualityBicubic)
    {
        if (image.Width <= 0 || image.Height <= 0) return;
        float scale = Math.Max(dest.Width / image.Width, dest.Height / image.Height);
        float w = image.Width * scale;
        float h = image.Height * scale;
        float x = dest.X + (dest.Width - w) / 2f;
        float y = dest.Y + (dest.Height - h) / 2f;
        var previous = g.InterpolationMode;
        g.InterpolationMode = mode;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Mirror pixels past the image edge; the default blends the edge with the background, leaving a dark line.
        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        var corners = new[] { new PointF(x, y), new PointF(x + w, y), new PointF(x, y + h) };
        g.DrawImage(image, corners, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);
        g.InterpolationMode = previous;
    }

    /// <summary>Draws the whole image fitted inside <paramref name="dest"/>.</summary>
    public static void DrawContain(Graphics g, Image image, RectangleF dest)
    {
        if (image.Width <= 0 || image.Height <= 0) return;
        float scale = Math.Min(dest.Width / image.Width, dest.Height / image.Height);
        float w = image.Width * scale, h = image.Height * scale;
        float x = dest.X + (dest.Width - w) / 2f, y = dest.Y + (dest.Height - h) / 2f;
        var previous = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        var corners = new[] { new PointF(x, y), new PointF(x + w, y), new PointF(x, y + h) };
        g.DrawImage(image, corners, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);
        g.InterpolationMode = previous;
    }

    public static string FormatDuration(long seconds)
    {
        if (seconds < 60) return "<1m";
        long minutes = seconds / 60;
        if (minutes < 60) return $"{minutes}m";
        long hours = minutes / 60;
        long rem = minutes % 60;
        return rem == 0 ? $"{hours}h" : $"{hours}h {rem}m";
    }

    public static string LastPlayedText(DateTime? utc)
    {
        if (utc is null) return "";
        var span = DateTime.UtcNow - utc.Value.ToUniversalTime();
        if (span.TotalMinutes < 2) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 2) return "yesterday";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
        return $"{(int)(span.TotalDays / 365)}y ago";
    }
}
