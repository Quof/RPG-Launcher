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

/// <summary>Blob-style mascots. The header always shows Yuuka; each spot's blob is picked at random per launch.</summary>
internal static class Mascots
{
    // Look: -1 faces the viewer's left, +1 the right.
    private static readonly (string Name, int Look)[] Family =
    {
        ("gen-mint", -1), ("gen-kuro", -1), ("gen-momo", -1), ("gen-sora", 1), ("gen-choco", -1)
    };
    private static readonly Dictionary<string, Bitmap?> Cache = new();
    private static readonly int FamilyOffset = Random.Shared.Next(Family.Length);

    /// <summary>True when the current style shows blobs.</summary>
    public static bool Enabled => Look.Style == UiStyle.Blob;

    /// <summary>The blob peeking over the header, beside the logo.</summary>
    public static Bitmap? Header => Load("blob-yuuka");

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
            if (stream is not null) bitmap = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Mascot {name} not loaded: {ex.Message}");
        }
        Cache[name] = bitmap;
        return bitmap;
    }

    /// <summary>Width of a mascot drawn at <paramref name="height"/>.</summary>
    public static double WidthAt(Bitmap image, double height) => image.Size.Width * height / image.Size.Height;

    // Each blob at each size it has been drawn at, scaled sharply once.
    private static readonly Dictionary<(Bitmap, double), SharpPicture> Scaled = new();

    /// <summary>Draws a mascot fitted to <paramref name="height"/>, its bottom edge on <paramref name="bottom"/>.</summary>
    public static Rect Draw(DrawingContext g, Bitmap image, double x, double bottom, double height)
    {
        var rect = new Rect(x, bottom - height, WidthAt(image, height), height);
        if (!Scaled.TryGetValue((image, height), out var picture))
        {
            if (Scaled.Count > 32) Scaled.Clear();
            Scaled[(image, height)] = picture = new SharpPicture();
        }
        picture.Draw(g, Probe, image, rect);
        return rect;
    }

    // A control with no window: the sharp copy then uses the scale of the launcher's window (Look.PixelScale).
    private static readonly Control Probe = new();

    /// <summary>Draws the spot's blob centered on <paramref name="centerX"/>, peeking up from <paramref name="bottom"/>.</summary>
    public static void DrawCentered(DrawingContext g, MascotSpot spot, double centerX, double bottom, double height)
    {
        if (For(spot) is not { } image) return;
        Draw(g, image, centerX - WidthAt(image, height) / 2, bottom, height);
    }
}
