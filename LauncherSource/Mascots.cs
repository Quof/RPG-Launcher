using System.Reflection;

namespace RpgLauncher;

/// <summary>Spots where a blob can appear in the Blob style.</summary>
internal enum MascotSpot
{
    StatusBar,
    TagMenu,
    InfoDialog,
    ConnectionDialog,
    PageSizeDialog,
    FramingDialog,
    ReleasesLoading
}

/// <summary>Blob-style mascots. The header blob and each spot's blob are picked at random per launch.</summary>
internal static class Mascots
{
    private static readonly string[] Originals = { "blob-sleepy", "blob-alice", "blob-angel" };
    // Look: -1 faces the viewer's left, +1 the right.
    private static readonly (string Name, int Look)[] Family =
    {
        ("gen-mint", -1), ("gen-kuro", -1), ("gen-momo", -1), ("gen-sora", 1), ("gen-choco", -1)
    };
    private static readonly Dictionary<string, Bitmap?> Cache = new();
    private static readonly int HeaderPick = Random.Shared.Next(Originals.Length);
    private static readonly int FamilyOffset = Random.Shared.Next(Family.Length);

    /// <summary>True when the current style shows blobs.</summary>
    public static bool Enabled => Theme.Style == UiStyle.Blob;

    /// <summary>The blob peeking over the header this session.</summary>
    public static Bitmap? Header => Load(Originals[HeaderPick]);

    /// <summary>The sleepy blob, for empty screens.</summary>
    public static Bitmap? Empty => Load("blob-sleepy");

    /// <summary>The blob for a spot, or null outside the Blob style.</summary>
    public static Bitmap? For(MascotSpot spot)
    {
        if (!Enabled) return null;
        // Blobs face into the window: left at the right edge, right at the left edge.
        int look = spot switch
        {
            MascotSpot.StatusBar or MascotSpot.TagMenu => -1,
            MascotSpot.PageSizeDialog => 1,
            _ => 0
        };
        var candidates = Family.Where(f => look == 0 || f.Look == look).ToArray();
        return Load(candidates[(FamilyOffset + (int)spot) % candidates.Length].Name);
    }

    private static Bitmap? Load(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"RpgLauncher.Assets.{name}.png");
            if (stream is not null)
            {
                using var image = Image.FromStream(stream);
                bitmap = new Bitmap(image);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Mascot {name} not loaded: {ex.Message}");
        }
        Cache[name] = bitmap;
        return bitmap;
    }

    /// <summary>Width of a mascot drawn at <paramref name="height"/>.</summary>
    public static float WidthAt(Bitmap image, float height) => image.Width * height / image.Height;

    /// <summary>Draws a mascot fitted to <paramref name="height"/>, its bottom edge on <paramref name="bottom"/>.</summary>
    public static RectangleF Draw(Graphics g, Bitmap image, float x, float bottom, float height)
    {
        var rect = new RectangleF(x, bottom - height, WidthAt(image, height), height);
        var mode = g.InterpolationMode;
        var offset = g.PixelOffsetMode;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(image, rect);
        g.InterpolationMode = mode;
        g.PixelOffsetMode = offset;
        return rect;
    }

    /// <summary>Draws the spot's blob centered on <paramref name="centerX"/>, peeking up from <paramref name="bottom"/>.</summary>
    public static void DrawCentered(Graphics g, MascotSpot spot, float centerX, float bottom, float height)
    {
        if (For(spot) is not { } image) return;
        Draw(g, image, centerX - WidthAt(image, height) / 2f, bottom, height);
    }
}
