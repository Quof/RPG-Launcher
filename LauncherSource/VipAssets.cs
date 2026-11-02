using SkiaSharp;

namespace RpgLauncher;

/// <summary>
/// Pictures for VIPRPG mode, read at run time and never shipped: the user's own sozai in a "vip-sozai" folder
/// beside the launcher first, then the installed RPG Maker 2000/2003 RTP (whose licence covers RPG Maker use, not
/// redistribution). Sheets use palette index 0 as their transparent colour, as RPG Maker does.
/// </summary>
internal static class VipAssets
{
    private static readonly Dictionary<string, Bitmap?> Cache = new();
    private static List<string>? _rtp;

    /// <summary>The folder for the user's own sozai: party.png, face.png, monster.png, system.png.</summary>
    public static string SozaiFolder => Path.Combine(AppContext.BaseDirectory, "vip-sozai");

    /// <summary>RTP folders found on this PC, 2000 first.</summary>
    public static IReadOnlyList<string> RtpFolders => _rtp ??= FindRtp();

    private static List<string> FindRtp()
    {
        var found = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) &&
                !found.Contains(path.TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase))
                found.Add(path.TrimEnd('\\', '/'));
        }

        // EasyRPG's variables, then the paths the RTP installers record.
        Add(Environment.GetEnvironmentVariable("RPG2K_RTP_PATH"));
        Add(Environment.GetEnvironmentVariable("RPG2K3_RTP_PATH"));
        if (OperatingSystem.IsWindows())
        {
            try
            {
                foreach (var (hive, key, value) in new[]
                         {
                             (Microsoft.Win32.Registry.CurrentUser, @"Software\ASCII\RPG2000", "RuntimePackagePath"),
                             (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\ASCII\RPG2000", "RuntimePackagePath"),
                             (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\ASCII\RPG2000", "RuntimePackagePath"),
                             (Microsoft.Win32.Registry.CurrentUser, @"Software\Enterbrain\RPG2003", "RUNTIMEPACKAGEPATH"),
                             (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Enterbrain\RPG2003", "RUNTIMEPACKAGEPATH"),
                             (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Enterbrain\RPG2003", "RUNTIMEPACKAGEPATH")
                         })
                {
                    using var opened = hive.OpenSubKey(key);
                    Add(opened?.GetValue(value) as string);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("RTP registry not read: " + ex.Message);
            }
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            Add(Path.Combine(programs, "ASCII", "RPG2000", "RTP"));
            Add(Path.Combine(programs, "Enterbrain", "RPG2003", "RTP"));
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Add(Path.Combine(home, ".local", "share", "rtp", "2000"));
            Add(Path.Combine(home, ".local", "share", "rtp", "2003"));
            Add("/usr/share/rtp/2000");
            Add("/usr/share/rtp/2003");
        }
        return found;
    }

    /// <summary>
    /// A picture by its sozai name (party, face, monster, system), else the RTP file it stands for
    /// (CharSet\主人公1 and so on). Null when neither exists.
    /// </summary>
    public static Bitmap? Load(string sozaiName, string rtpFolder, string rtpName, bool keyed)
    {
        string cacheKey = sozaiName;
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            string? path = FindFile(SozaiFolder, sozaiName);
            foreach (string rtp in RtpFolders)
            {
                if (path is not null) break;
                path = FindFile(Path.Combine(rtp, rtpFolder), rtpName);
            }
            if (path is not null) bitmap = Decode(File.ReadAllBytes(path), keyed);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"VIPRPG picture {sozaiName} not loaded: {ex.Message}");
        }
        Cache[cacheKey] = bitmap;
        return bitmap;
    }

    private static string? FindFile(string folder, string name)
    {
        foreach (var extension in new[] { ".png", ".bmp" })
        {
            if (Paths.File(Path.Combine(folder, name + extension)) is { } path) return path;
        }
        return null;
    }

    /// <summary>Decodes a PNG or BMP; with <paramref name="keyed"/>, palette colour 0 becomes transparent.</summary>
    private static Bitmap? Decode(byte[] bytes, bool keyed)
    {
        using var image = ImageKit.Decode(bytes);
        if (image is null) return null;
        if (keyed && PaletteZero(bytes) is { } key)
        {
            var pixels = image.Pixels;
            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.Red == key.R && p.Green == key.G && p.Blue == key.B) pixels[i] = SKColors.Transparent;
            }
            image.Pixels = pixels;
        }
        return ImageKit.ToAvalonia(image);
    }

    /// <summary>The first palette colour of an indexed PNG or BMP, or null for true-colour files.</summary>
    private static Color? PaletteZero(byte[] bytes)
    {
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P')
        {
            for (int at = 8; at + 12 <= bytes.Length;)
            {
                int length = (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
                if (length < 0) break;
                if (bytes[at + 4] == 'P' && bytes[at + 5] == 'L' && bytes[at + 6] == 'T' && bytes[at + 7] == 'E' && length >= 3)
                    return Color.FromRgb(bytes[at + 8], bytes[at + 9], bytes[at + 10]);
                if (bytes[at + 4] == 'I' && bytes[at + 5] == 'D') break;
                at += 12 + length;
            }
            return null;
        }
        if (bytes.Length > 54 && bytes[0] == 'B' && bytes[1] == 'M')
        {
            int header = BitConverter.ToInt32(bytes, 14);
            int bits = BitConverter.ToUInt16(bytes, 28);
            int palette = 14 + header;
            if (bits <= 8 && palette + 4 <= bytes.Length) return Color.FromRgb(bytes[palette + 2], bytes[palette + 1], bytes[palette]);
        }
        return null;
    }
}

/// <summary>
/// An RPG Maker 2000 system graphic (System\システム.png): the window background and frame, the cursor, the
/// scroll arrows and the gradient text colours. Drawn pixel for pixel in canvas coordinates.
/// </summary>
internal sealed class VipSkin
{
    private static readonly RenderOptions Pixels = new() { BitmapInterpolationMode = BitmapInterpolationMode.None };
    private static VipSkin? _current;
    private static bool _looked;

    private readonly Bitmap _bitmap;
    private readonly IBrush[] _text = new IBrush[20];
    public Color Shadow { get; }

    private VipSkin(Bitmap bitmap)
    {
        _bitmap = bitmap;
        using var copy = ToSkia(bitmap);
        Shadow = Pick(copy, 16, 32);
        for (int i = 0; i < 20; i++)
        {
            int x = i % 10 * 16, y = 48 + i / 10 * 16;
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative)
            };
            brush.GradientStops.Add(new GradientStop(Pick(copy, x + 8, y + 3), 0));
            brush.GradientStops.Add(new GradientStop(Pick(copy, x + 8, y + 12), 1));
            _text[i] = brush;
        }
    }

    /// <summary>The skin in use, or null to draw windows procedurally.</summary>
    public static VipSkin? Current
    {
        get
        {
            if (_looked) return _current;
            _looked = true;
            var bitmap = VipAssets.Load("system", "System", "システム", keyed: true);
            if (bitmap is { PixelSize.Width: >= 160, PixelSize.Height: >= 80 }) _current = new VipSkin(bitmap);
            return _current;
        }
    }

    private static SKBitmap ToSkia(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return SKBitmap.Decode(stream.ToArray());
    }

    private static Color Pick(SKBitmap bitmap, int x, int y)
    {
        var c = bitmap.GetPixel(Math.Min(x, bitmap.Width - 1), Math.Min(y, bitmap.Height - 1));
        return Color.FromRgb(c.Red, c.Green, c.Blue);
    }

    /// <summary>Gradient brush for one of the 20 text colours (0 normal, 1 system, 3 disabled…).</summary>
    public IBrush Text(int index) => _text[Math.Clamp(index, 0, 19)];

    private void Blit(DrawingContext g, Rect source, Rect dest)
    {
        if (dest.Width <= 0 || dest.Height <= 0) return;
        g.DrawImage(_bitmap, source, dest);
    }

    /// <summary>The window: the background stretched inside, the 8-pixel frame around it.</summary>
    public void Window(DrawingContext g, Rect rect)
    {
        rect = RetroDraw.Snap(rect);
        using var _ = g.PushRenderOptions(Pixels);
        Blit(g, new Rect(0, 0, 32, 32), rect.Deflate(2));
        double x = rect.X, y = rect.Y, r = rect.Right, b = rect.Bottom;
        // Corners.
        Blit(g, new Rect(32, 0, 8, 8), new Rect(x, y, 8, 8));
        Blit(g, new Rect(56, 0, 8, 8), new Rect(r - 8, y, 8, 8));
        Blit(g, new Rect(32, 24, 8, 8), new Rect(x, b - 8, 8, 8));
        Blit(g, new Rect(56, 24, 8, 8), new Rect(r - 8, b - 8, 8, 8));
        // Edges, stretched between the corners.
        Blit(g, new Rect(40, 0, 16, 8), new Rect(x + 8, y, rect.Width - 16, 8));
        Blit(g, new Rect(40, 24, 16, 8), new Rect(x + 8, b - 8, rect.Width - 16, 8));
        Blit(g, new Rect(32, 8, 8, 16), new Rect(x, y + 8, 8, rect.Height - 16));
        Blit(g, new Rect(56, 8, 8, 16), new Rect(r - 8, y + 8, 8, rect.Height - 16));
    }

    /// <summary>The selection cursor, fading in and out like RPG_RT's.</summary>
    public void Cursor(DrawingContext g, Rect rect, int tick, bool active)
    {
        rect = RetroDraw.Snap(rect);
        double pulse = active ? 0.55 + 0.45 * (Math.Sin(tick * 0.35) + 1) / 2 : 0.45;
        using var _ = g.PushRenderOptions(Pixels);
        using var __ = g.PushOpacity(pulse);
        double x = rect.X, y = rect.Y, r = rect.Right, b = rect.Bottom;
        double w = rect.Width, h = rect.Height;
        Blit(g, new Rect(72, 8, 16, 16), new Rect(x + 8, y + 8, w - 16, h - 16));
        Blit(g, new Rect(64, 0, 8, 8), new Rect(x, y, 8, 8));
        Blit(g, new Rect(88, 0, 8, 8), new Rect(r - 8, y, 8, 8));
        Blit(g, new Rect(64, 24, 8, 8), new Rect(x, b - 8, 8, 8));
        Blit(g, new Rect(88, 24, 8, 8), new Rect(r - 8, b - 8, 8, 8));
        Blit(g, new Rect(72, 0, 16, 8), new Rect(x + 8, y, w - 16, 8));
        Blit(g, new Rect(72, 24, 16, 8), new Rect(x + 8, b - 8, w - 16, 8));
        Blit(g, new Rect(64, 8, 8, 16), new Rect(x, y + 8, 8, h - 16));
        Blit(g, new Rect(88, 8, 8, 16), new Rect(r - 8, y + 8, 8, h - 16));
    }

    /// <summary>The ▲/▼ scroll arrow from inside the frame graphic, centred on <paramref name="centerX"/>.</summary>
    public void Arrow(DrawingContext g, double centerX, double y, bool down)
    {
        using var _ = g.PushRenderOptions(Pixels);
        Blit(g, new Rect(40, down ? 16 : 8, 16, 8), new Rect(Math.Round(centerX) - 8, Math.Round(y), 16, 8));
    }
}
