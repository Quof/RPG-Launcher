using SkiaSharp;

namespace RpgLauncher;

/// <summary>
/// RPG Maker 2000/2003's stock title pictures (the RTP's sky, map, books and galaxy, watermarked "RPGツクール"),
/// which say nothing about a game. Games often ship a copy, re-saved or recoloured, so they're recognised by a
/// fingerprint of the picture rather than by name or size.
/// </summary>
internal static class Rm2kStockTitles
{
    // 8×8 brightness fingerprints of the RTP's Title\タイトル1–4, 2000 then 2003.
    private static readonly ulong[] Stock =
    {
        0x167FFFCE0319F1E0UL, 0xC1E8FCFCFCF9C1C1UL, 0x000101191F3F7F7FUL, 0x00020E1C3C380000UL,
        0x367FFFCE0319F1E0UL, 0xE1E8FCFCFCE9C1C1UL, 0x000101191F3F3F7FUL, 0xF0020E1C3C380000UL
    };

    /// <summary>True for one of the RTP's own title pictures.</summary>
    public static bool IsStock(SKBitmap image)
    {
        ulong print = Fingerprint(image);
        // Copies match exactly or nearly (re-saved); games' own titles land 4 or more cells away.
        return Stock.Any(stock => ulong.PopCount(stock ^ print) <= 2);
    }

    /// <summary>Which of 64 cells (8×8) are brighter than the picture's average.</summary>
    public static ulong Fingerprint(SKBitmap image)
    {
        using var small = image.Resize(new SKImageInfo(8, 8, SKColorType.Rgba8888), SKFilterQuality.Medium);
        if (small is null) return 0;
        var light = new double[64];
        for (int i = 0; i < 64; i++)
        {
            var c = small.GetPixel(i % 8, i / 8);
            light[i] = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
        }
        double mean = light.Average();
        ulong bits = 0;
        for (int i = 0; i < 64; i++)
        {
            if (light[i] > mean) bits |= 1UL << i;
        }
        return bits;
    }
}
