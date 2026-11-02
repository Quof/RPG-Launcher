namespace RpgLauncher;

/// <summary>
/// The look of the VIPRPG screen for the matsuri being browsed, after the event sites: red and white curtains for
/// 紅白, a spring sky for GW, fireworks for 夏の陣, a dark back room for 裏, and a starry night otherwise.
/// </summary>
internal sealed class VipTheme
{
    private enum Kind { Night, Kouhaku, Gw, Natsu, Ura }

    private readonly Kind _kind;

    public Color Page { get; private init; }
    public Color PageCheck { get; private init; }
    public Color Marquee { get; private init; }
    public Color Title { get; private init; }
    public Color Subtitle { get; private init; }
    public Color Sky { get; private init; }
    public Color? WindowTop { get; private init; }
    public Color? WindowBottom { get; private init; }

    /// <summary>The second word of the logo, the logo's upper-half shine and its outline.</summary>
    public Color LogoSecond { get; private init; } = Color.FromRgb(255, 216, 96);
    public Color LogoHighlight { get; private init; } = Colors.White;
    public Color LogoOutline { get; private init; } = Color.FromRgb(16, 16, 64);

    private VipTheme(Kind kind) => _kind = kind;

    private IBrush? _pageBrush;

    /// <summary>The page behind the windows: 8-pixel checks, as tiled backgrounds on old fan sites.</summary>
    public IBrush PageBrush => _pageBrush ??= CheckBrush(Page, PageCheck);

    private static IBrush CheckBrush(Color a, Color b)
    {
        var bitmap = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Opaque);
        using (var buffer = bitmap.Lock())
        {
            var pixels = new int[16 * 16];
            int Bgra(Color c) => c.B | (c.G << 8) | (c.R << 16) | (255 << 24);
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                    pixels[y * 16 + x] = (x / 8 + y / 8) % 2 == 0 ? Bgra(a) : Bgra(b);
            }
            for (int y = 0; y < 16; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * 16, buffer.Address + y * buffer.RowBytes, 16);
        }
        return new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute)
        };
    }

    private static readonly VipTheme Night = new(Kind.Night)
    {
        Page = Color.FromRgb(0, 0, 40), PageCheck = Color.FromRgb(0, 0, 52), Marquee = Color.FromRgb(255, 240, 96),
        Title = Color.FromRgb(120, 200, 255), Subtitle = Color.FromRgb(176, 200, 255), Sky = Color.FromRgb(16, 24, 72)
    };

    private static readonly VipTheme Kouhaku = new(Kind.Kouhaku)
    {
        Page = Color.FromRgb(72, 8, 24), PageCheck = Color.FromRgb(84, 12, 30), Marquee = Colors.White,
        WindowTop = Color.FromRgb(150, 36, 60), WindowBottom = Color.FromRgb(60, 6, 22),
        Title = Color.FromRgb(255, 72, 88), Subtitle = Color.FromRgb(255, 216, 216), Sky = Color.FromRgb(88, 8, 24),
        LogoSecond = Colors.White, LogoHighlight = Color.FromRgb(255, 236, 236), LogoOutline = Color.FromRgb(48, 0, 8)
    };

    private static readonly VipTheme Gw = new(Kind.Gw)
    {
        Page = Color.FromRgb(0, 28, 16), PageCheck = Color.FromRgb(0, 38, 22), Marquee = Color.FromRgb(160, 255, 120),
        Title = Color.FromRgb(88, 216, 72), Subtitle = Colors.White, Sky = Color.FromRgb(104, 168, 240),
        LogoOutline = Color.FromRgb(8, 48, 8)
    };

    private static readonly VipTheme Natsu = new(Kind.Natsu)
    {
        Page = Color.FromRgb(16, 64, 136), PageCheck = Color.FromRgb(24, 76, 152), Marquee = Color.FromRgb(255, 224, 96),
        Title = Color.FromRgb(255, 224, 64), Subtitle = Colors.White, Sky = Color.FromRgb(72, 144, 232),
        LogoSecond = Color.FromRgb(255, 255, 255), LogoOutline = Color.FromRgb(8, 32, 88),
        WindowTop = Color.FromRgb(48, 120, 72), WindowBottom = Color.FromRgb(14, 56, 30)
    };

    private static readonly VipTheme Ura = new(Kind.Ura)
    {
        Page = Color.FromRgb(8, 0, 12), PageCheck = Color.FromRgb(16, 0, 22), Marquee = Color.FromRgb(96, 255, 96),
        Title = Color.FromRgb(96, 255, 96), Subtitle = Color.FromRgb(96, 255, 96), Sky = Color.FromRgb(20, 0, 28),
        LogoSecond = Color.FromRgb(200, 255, 200), LogoHighlight = Color.FromRgb(220, 255, 220), LogoOutline = Colors.Black,
        WindowTop = Color.FromRgb(56, 40, 72), WindowBottom = Color.FromRgb(16, 8, 24)
    };

    public static VipTheme For(VipMatsuri? matsuri)
    {
        if (matsuri is null) return Night;
        string text = (matsuri.Id + " " + matsuri.Name).ToLowerInvariant();
        if (text.Contains('裏') || text.Contains("ura")) return Ura;
        if (text.Contains("紅白") || text.Contains("kouhaku") || text.Contains("kohaku")) return Kouhaku;
        if (text.Contains("gw") || text.Contains("ゴールデン")) return Gw;
        if (text.Contains('夏') || text.Contains("natsu") || text.Contains("summer")) return Natsu;
        return Night;
    }

    public void PaintBanner(DrawingContext g, Rect rect, int tick)
    {
        switch (_kind)
        {
            case Kind.Kouhaku:
                // A year-end red sky with falling snow, and the red and white curtain (紅白幕) along the bottom.
                Gradient(g, rect, Color.FromRgb(96, 8, 24), Color.FromRgb(184, 32, 56));
                for (int i = 0; i < (int)(rect.Width / 14); i++)
                {
                    double x = rect.X + (i * 53 + Math.Sin((tick + i * 7) * 0.05) * 6) % rect.Width;
                    double y = rect.Y + (i * 17 + tick * (1 + i % 3) / 3.0) % rect.Height;
                    RetroDraw.Fill(g, new Rect(x, y, i % 4 == 0 ? 2 : 1, i % 4 == 0 ? 2 : 1), Color.FromArgb(220, 255, 255, 255));
                }
                for (double x = rect.X; x < rect.Right; x += 16)
                {
                    RetroDraw.Fill(g, new Rect(x, rect.Bottom - 6, 8, 6), Color.FromRgb(216, 24, 48));
                    RetroDraw.Fill(g, new Rect(x + 8, rect.Bottom - 6, 8, 6), Color.FromRgb(250, 246, 246));
                }
                break;
            case Kind.Gw:
                Gradient(g, rect, Color.FromRgb(56, 120, 224), Color.FromRgb(152, 208, 255));
                for (int i = 0; i < 5; i++)
                {
                    double x = (i * 211 + tick / 3) % (rect.Width + 80) - 40;
                    Cloud(g, rect.X + x, rect.Y + 6 + i * 9 % 30);
                }
                break;
            case Kind.Natsu:
                // 夏だ！海だ！: a summer sky over the sea, clouds drifting by.
                Gradient(g, rect, Color.FromRgb(40, 112, 216), Color.FromRgb(128, 192, 255));
                for (int i = 0; i < 6; i++)
                {
                    double x = (i * 173 + tick / 4) % (rect.Width + 80) - 40;
                    Cloud(g, rect.X + x, rect.Y + 5 + i * 11 % 26);
                }
                Fireworks(g, rect, tick);
                break;
            case Kind.Ura:
                Gradient(g, rect, Color.FromRgb(4, 0, 8), Color.FromRgb(28, 0, 36));
                for (double y = rect.Y; y < rect.Bottom; y += 3)
                    RetroDraw.Fill(g, new Rect(rect.X, y, rect.Width, 1), Color.FromArgb(24, 96, 255, 96));
                break;
            default:
                Gradient(g, rect, Color.FromRgb(0, 0, 24), Color.FromRgb(24, 40, 120));
                Stars(g, rect, tick);
                break;
        }
    }

    private static void Gradient(DrawingContext g, Rect rect, Color top, Color bottom)
    {
        for (double y = rect.Y; y < rect.Bottom; y += 2)
            RetroDraw.Fill(g, new Rect(rect.X, y, rect.Width, 2), Look.Blend(top, bottom, (y - rect.Y) / Math.Max(1, rect.Height)));
    }

    private static void Stars(DrawingContext g, Rect rect, int tick)
    {
        // A fixed scatter of stars; a few twinkle.
        for (int i = 0; i < (int)(rect.Width / 9); i++)
        {
            double x = rect.X + (i * 97) % (int)Math.Max(1, rect.Width);
            double y = rect.Y + (i * 37 + i * i) % (int)Math.Max(1, rect.Height);
            bool bright = (i + tick / 5) % 7 == 0;
            RetroDraw.Fill(g, new Rect(x, y, 1, 1), bright ? Colors.White : Color.FromRgb(120, 136, 200));
            if (bright)
            {
                RetroDraw.Fill(g, new Rect(x - 1, y, 3, 1), Color.FromArgb(160, 255, 255, 255));
                RetroDraw.Fill(g, new Rect(x, y - 1, 1, 3), Color.FromArgb(160, 255, 255, 255));
            }
        }
    }

    private static void Fireworks(DrawingContext g, Rect rect, int tick)
    {
        Color[] colors = { Color.FromRgb(255, 96, 96), Color.FromRgb(255, 224, 96), Color.FromRgb(128, 224, 255), Color.FromRgb(200, 128, 255) };
        for (int burst = 0; burst < 3; burst++)
        {
            int life = (tick + burst * 23) % 60;
            if (life > 28) continue;
            double cx = rect.X + (rect.Width * (0.55 + 0.15 * burst) + burst * 131 * ((tick + burst * 23) / 60)) % rect.Width;
            double cy = rect.Y + 14 + burst * 7;
            double radius = life * 0.8;
            var color = colors[(burst + (tick + burst * 23) / 60) % colors.Length];
            byte alpha = (byte)Math.Max(0, 255 - life * 8);
            for (int ray = 0; ray < 12; ray++)
            {
                double angle = ray * Math.PI / 6;
                RetroDraw.Fill(g, new Rect(cx + Math.Cos(angle) * radius, cy + Math.Sin(angle) * radius * 0.8, 2, 2),
                    Color.FromArgb(alpha, color.R, color.G, color.B));
            }
        }
    }

    private static void Cloud(DrawingContext g, double x, double y)
    {
        var white = Color.FromArgb(230, 255, 255, 255);
        RetroDraw.Fill(g, new Rect(x + 4, y, 16, 4), white);
        RetroDraw.Fill(g, new Rect(x, y + 4, 28, 5), white);
        RetroDraw.Fill(g, new Rect(x + 8, y - 3, 8, 3), white);
    }
}
