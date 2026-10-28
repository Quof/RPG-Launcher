using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace RpgLauncher;

/// <summary>A font for drawing and for controls; <see cref="Size"/> is in device-independent pixels.</summary>
internal readonly record struct UiFont(Typeface Typeface, double Size)
{
    public FontFamily Family => Typeface.FontFamily;
    public FontWeight Weight => Typeface.Weight;
    public bool Bold => Typeface.Weight >= FontWeight.Bold;

    public void ApplyTo(TemplatedControl control)
    {
        control.FontFamily = Family;
        control.FontSize = Size;
        control.FontWeight = Weight;
    }

    public void ApplyTo(TextBlock block)
    {
        block.FontFamily = Family;
        block.FontSize = Size;
        block.FontWeight = Weight;
    }
}

/// <summary>Vertical placement of text in <see cref="Look.DrawText"/>.</summary>
internal enum TextLine
{
    Top,
    Center,
    Bottom
}

internal static class Look
{
    private static Palette P = Palette.Default;

    /// <summary>The active style; set at startup and whenever the user picks one.</summary>
    public static UiStyle Style { get; private set; } = UiStyle.Default;

    /// <summary>Raised after <see cref="Apply"/> so controls can redraw.</summary>
    public static event Action? Changed;

    public static void Apply(UiStyle style)
    {
        Style = style;
        P = Palette.For(style);
        ApplyResources();
        Changed?.Invoke();
    }

    /// <summary>The active palette.</summary>
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
    public static double BorderWidth => P.BorderWidth;

    /// <summary>Corner radius for cards.</summary>
    public static double CardRadius => P.CardRadius;

    /// <summary>Corner radius for buttons and fields.</summary>
    public static double ControlRadius => P.ControlRadius;

    public static bool IsLight => P.Light;

    /// <summary>Corner radius for the picture inside a card.</summary>
    public static double ImageRadius => Math.Max(0, P.CardRadius - 5);

    /// <summary>Corner radius for tag chips.</summary>
    public static double ChipRadius => Style switch
    {
        UiStyle.Angelic => 2,
        UiStyle.Dark => 6,
        UiStyle.Blob => 12,
        _ => 9
    };

    /// <summary>Outline width for buttons and fields.</summary>
    public static double ControlBorderWidth => P.Light ? 1.5 : 1;

    // ---- fonts ----

    private static readonly Lazy<FontFamily> LazyFamily = new(PickFamily);

    public static FontFamily FontFamily => LazyFamily.Value;

    /// <summary>The family's name, for drawing with SkiaSharp off the UI thread.</summary>
    public static string FontFamilyName => FontFamily.Name;

    /// <summary>A font at a point size, as the WinForms launcher sized them (9 pt = 12 px).</summary>
    public static UiFont Font(double points, bool bold = false) =>
        new(new Typeface(FontFamily, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), points * 96.0 / 72.0);

    private static FontFamily PickFamily()
    {
        // Japanese-capable families first: titles and tags are often Japanese.
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "Yu Gothic UI", "Meiryo UI", "Segoe UI", "Tahoma" }
            : new[] { "Noto Sans CJK JP", "Noto Sans JP", "Source Han Sans JP", "IPAexGothic", "IPAGothic", "VL Gothic",
                      "Noto Sans", "DejaVu Sans", "Liberation Sans", "Ubuntu", "Cantarell" };
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
        return FontFamily.Default;
    }

    // ---- brushes, pens, shapes ----

    private static readonly ConcurrentDictionary<Color, IImmutableSolidColorBrush> Brushes = new();
    private static readonly ConcurrentDictionary<(Color, double, bool), ImmutablePen> Pens = new();

    public static IImmutableSolidColorBrush Brush(Color color) =>
        Brushes.GetOrAdd(color, c => new ImmutableSolidColorBrush(c));

    /// <summary>A pen; <paramref name="round"/> gives round caps and joins.</summary>
    public static ImmutablePen Pen(Color color, double width, bool round = false) =>
        Pens.GetOrAdd((color, width, round), key => new ImmutablePen(Brush(key.Item1), key.Item2, null,
            key.Item3 ? PenLineCap.Round : PenLineCap.Flat, key.Item3 ? PenLineJoin.Round : PenLineJoin.Miter));

    /// <summary>The color with a new alpha (0–255).</summary>
    public static Color Alpha(int alpha, Color color) =>
        Color.FromArgb((byte)Math.Clamp(alpha, 0, 255), color.R, color.G, color.B);

    /// <summary>Mixes two opaque colors; <paramref name="amount"/> 0 gives <paramref name="from"/>.</summary>
    public static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromArgb(Mix(from.A, to.A), Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    /// <summary>A rounded rectangle; the radius is clamped to half the shorter side.</summary>
    public static RoundedRect Rounded(Rect rect, double radius) =>
        new(rect, Math.Max(0, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2)));

    /// <summary>A rounded rectangle as a geometry, for clipping or hit tests.</summary>
    public static Geometry RoundedGeometry(Rect rect, double radius)
    {
        radius = Math.Max(0, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        if (radius <= 0.5)
        {
            ctx.BeginFigure(rect.TopLeft, true);
            ctx.LineTo(rect.TopRight);
            ctx.LineTo(rect.BottomRight);
            ctx.LineTo(rect.BottomLeft);
            ctx.EndFigure(true);
            return geometry;
        }
        var corner = new Size(radius, radius);
        ctx.BeginFigure(new Point(rect.Left + radius, rect.Top), true);
        ctx.LineTo(new Point(rect.Right - radius, rect.Top));
        ctx.ArcTo(new Point(rect.Right, rect.Top + radius), corner, 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(rect.Right, rect.Bottom - radius));
        ctx.ArcTo(new Point(rect.Right - radius, rect.Bottom), corner, 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(rect.Left + radius, rect.Bottom));
        ctx.ArcTo(new Point(rect.Left, rect.Bottom - radius), corner, 0, false, SweepDirection.Clockwise);
        ctx.LineTo(new Point(rect.Left, rect.Top + radius));
        ctx.ArcTo(new Point(rect.Left + radius, rect.Top), corner, 0, false, SweepDirection.Clockwise);
        ctx.EndFigure(true);
        return geometry;
    }

    public static Geometry Star(Rect rect)
    {
        double cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
        double outer = Math.Min(rect.Width, rect.Height) / 2, inner = outer * 0.45;
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        for (int i = 0; i < 10; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI / 5;
            double radius = i % 2 == 0 ? outer : inner;
            var point = new Point(cx + Math.Cos(angle) * radius, cy + Math.Sin(angle) * radius);
            if (i == 0) ctx.BeginFigure(point, true);
            else ctx.LineTo(point);
        }
        ctx.EndFigure(true);
        return geometry;
    }

    public static Geometry Heart(Rect r)
    {
        double w = r.Width, h = r.Height, x = r.X, y = r.Y;
        Point P(double fx, double fy) => new(x + w * fx, y + h * fy);
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(P(0.50, 0.92), true);
        ctx.CubicBezierTo(P(0.04, 0.60), P(0.02, 0.28), P(0.24, 0.17));
        ctx.CubicBezierTo(P(0.36, 0.08), P(0.48, 0.15), P(0.50, 0.28));
        ctx.CubicBezierTo(P(0.52, 0.15), P(0.64, 0.08), P(0.76, 0.17));
        ctx.CubicBezierTo(P(0.98, 0.28), P(0.96, 0.60), P(0.50, 0.92));
        ctx.EndFigure(true);
        return geometry;
    }

    /// <summary>A polyline (open) or polygon (closed) through the points.</summary>
    public static Geometry Lines(bool closed, params Point[] points)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(points[0], closed);
        for (int i = 1; i < points.Length; i++) ctx.LineTo(points[i]);
        ctx.EndFigure(closed);
        return geometry;
    }

    /// <summary>A linear gradient across whatever it fills; 90° runs top to bottom, 0° left to right.</summary>
    public static IBrush Gradient(Color from, Color to, double angle = 90)
    {
        double radians = angle * Math.PI / 180, dx = Math.Cos(radians), dy = Math.Sin(radians);
        double longest = Math.Max(Math.Abs(dx), Math.Abs(dy));
        dx /= longest;
        dy /= longest;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5 - dx / 2, 0.5 - dy / 2, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5 + dx / 2, 0.5 + dy / 2, RelativeUnit.Relative),
            GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) }
        };
    }

    // ---- text ----

    public static FormattedText Format(string text, UiFont font, Color color) =>
        new(text ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, font.Typeface, font.Size, Brush(color));

    /// <summary>Size of one line of text.</summary>
    public static Size Measure(string text, UiFont font)
    {
        var formatted = Format(text, font, Colors.Black);
        return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    /// <summary>
    /// Draws text inside a rectangle: one line trimmed with an ellipsis, or wrapped lines when
    /// <paramref name="wrap"/> is set (cut off at the rectangle's bottom).
    /// </summary>
    public static void DrawText(DrawingContext g, string text, UiFont font, Color color, Rect rect,
        TextAlignment align = TextAlignment.Left, TextLine line = TextLine.Center, bool wrap = false)
    {
        if (string.IsNullOrEmpty(text) || rect.Width < 1 || rect.Height < 1) return;
        var formatted = Format(text, font, color);
        formatted.MaxTextWidth = rect.Width;
        formatted.TextAlignment = align;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        if (wrap) formatted.MaxTextHeight = Math.Max(font.Size * 1.2, rect.Height);
        else formatted.MaxLineCount = 1;

        double y = line switch
        {
            TextLine.Top => rect.Y,
            TextLine.Bottom => rect.Bottom - formatted.Height,
            _ => rect.Y + (rect.Height - formatted.Height) / 2
        };
        using (g.PushClip(rect.Inflate(new Thickness(2, 4))))
            g.DrawText(formatted, new Point(rect.X, y));
    }

    // ---- pictures ----

    // Pictures are scaled with ImageKit.Resample and drawn 1:1 (SharpPicture): the renderer's own scaling shrinks
    // through mipmaps on the GPU, which softens them.

    /// <summary>Screen pixels per unit where the launcher's window is, for drawing that has no control at hand.</summary>
    public static double PixelScale { get; set; } = 1;

    // ---- standard controls ----

    /// <summary>Colors the Fluent controls the launcher uses (text boxes, tooltips, scroll bars) for the active style.</summary>
    public static void ApplyResources()
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        var r = app.Resources;
        void Set(string key, Color color) => r[key] = new SolidColorBrush(color);

        foreach (var state in new[] { "", "PointerOver", "Focused" })
        {
            Set("TextControlForeground" + state, Text);
            Set("TextControlPlaceholderForeground" + state, TextDim);
        }
        Set("TextControlForegroundDisabled", TextDim);
        Set("TextControlBackground", Field);
        Set("TextControlBackgroundPointerOver", Field);
        Set("TextControlBackgroundFocused", Field);
        Set("TextControlBackgroundDisabled", Field);
        Set("TextControlBorderBrush", FieldBorder);
        Set("TextControlBorderBrushPointerOver", FieldBorder);
        Set("TextControlBorderBrushFocused", CardBorderHover);
        Set("TextControlBorderBrushDisabled", FieldBorder);
        Set("TextControlSelectionHighlightColor", AccentBorder);
        Set("TextControlButtonForeground", TextDim);
        Set("TextControlButtonForegroundPointerOver", Text);
        r["TextControlBorderThemeThickness"] = new Thickness(ControlBorderWidth);
        r["TextControlBorderThemeThicknessFocused"] = new Thickness(ControlBorderWidth);
        r["TextControlThemeMinHeight"] = 0.0;
        r["ControlCornerRadius"] = new CornerRadius(ControlRadius);

        Set("ToolTipBackground", MenuBg);
        Set("ToolTipForeground", Text);
        Set("ToolTipBorderBrush", FieldBorder);

        Set("ScrollBarThumbFill", ScrollThumb);
        Set("ScrollBarThumbFillPointerOver", ScrollThumbActive);
        Set("ScrollBarThumbFillPressed", ScrollThumbActive);
        Set("ScrollBarTrackFill", ScrollTrack);
        Set("ScrollBarTrackFillPointerOver", ScrollTrack);
        Set("ScrollBarButtonArrowForeground", TextDim);

        r["SystemAccentColor"] = Gold;
        Set("SystemControlHighlightAccentBrush", Gold);
        Set("SystemControlFocusVisualPrimaryBrush", AccentBorder);
        Set("SystemControlFocusVisualSecondaryBrush", Bg);
    }

    // ---- text helpers ----

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

/// <summary>
/// A picture scaled once to the screen pixels it is drawn at and then copied 1:1, made again when its size changes.
/// The renderer's own scaling shrinks through mipmaps, which softens pictures.
/// </summary>
internal sealed class SharpPicture
{
    private static readonly RenderOptions PixelCopy = new() { BitmapInterpolationMode = BitmapInterpolationMode.None };

    private Bitmap? _scaled;
    private Bitmap? _source;
    private PixelSize _size;
    private double _scale;

    /// <summary>Draws <paramref name="source"/> into <paramref name="dest"/>, in the coordinates of <paramref name="visual"/>.</summary>
    public void Draw(DrawingContext g, Visual visual, Bitmap source, Rect dest)
    {
        double scale = TopLevel.GetTopLevel(visual)?.RenderScaling ?? Look.PixelScale;
        var size = new PixelSize(Math.Max(1, (int)Math.Round(dest.Width * scale)), Math.Max(1, (int)Math.Round(dest.Height * scale)));
        if (_scaled is null || !ReferenceEquals(_source, source) || _size != size || _scale != scale)
        {
            Release();
            _scaled = ImageKit.Resample(source, new Rect(source.Size), size, scale);
            _source = source;
            _size = size;
            _scale = scale;
        }
        var at = new Rect(Math.Round(dest.X * scale) / scale, Math.Round(dest.Y * scale) / scale, size.Width / scale, size.Height / scale);
        using (g.PushRenderOptions(PixelCopy))
            g.DrawImage(_scaled, new Rect(_scaled.Size), at);
    }

    public void Release()
    {
        if (_scaled is not null && !ReferenceEquals(_scaled, _source)) _scaled.Dispose();
        _scaled = null;
        _source = null;
    }
}
