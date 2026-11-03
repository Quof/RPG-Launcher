using System.IO.Compression;
using System.Reflection;
using Avalonia.Media.Imaging;

namespace RpgLauncher;

/// <summary>
/// Pictures of matsuri works (a screenshot or cover from the matsuri's archived site on fes.viprpg.org, at most
/// 320×240 WebP), built into the launcher as Assets/vip-thumbs.zip with one &lt;matsuri&gt;/&lt;No&gt;.webp per work.
/// </summary>
internal static class VipThumbs
{
    private static readonly Lazy<ZipArchive?> Pack = new(Open);
    private static readonly object Gate = new();

    private static ZipArchive? Open()
    {
        try
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RpgLauncher.Assets.vip-thumbs.zip");
            return stream is null ? null : new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Diagnostics.Log("VIPRPG thumbnails not loaded: " + ex.Message);
            return null;
        }
    }

    /// <summary>True when the launcher has a picture of this work.</summary>
    public static bool Has(VipWork work) => Entry(work) is not null;

    private static ZipArchiveEntry? Entry(VipWork work)
    {
        if (string.IsNullOrWhiteSpace(work.No) || Pack.Value is not { } pack) return null;
        lock (Gate) return pack.GetEntry($"{work.Matsuri}/{work.No}.webp");
    }

    /// <summary>The work's picture, or null if there's none. Safe off the UI thread.</summary>
    public static Bitmap? Load(VipWork work)
    {
        var buffer = new MemoryStream();
        lock (Gate)
        {
            if (Entry(work) is not { } entry) return null;
            using var input = entry.Open();
            input.CopyTo(buffer);
        }
        buffer.Position = 0;
        return new Bitmap(buffer);
    }
}
