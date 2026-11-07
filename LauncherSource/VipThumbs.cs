using System.IO.Compression;
using System.Reflection;
using Avalonia.Media.Imaging;

namespace RpgLauncher;

/// <summary>
/// Pictures of matsuri works (a screenshot or cover from the matsuri's archived site on fes.viprpg.org, at most
/// 320×240 WebP), built into the launcher as Assets/vip-thumbs-1.zip, -2.zip… with one &lt;matsuri&gt;/&lt;No&gt;.webp per
/// work. The pack is split into parts under 100 MB so it fits in a GitHub repository; each name is in one part.
/// </summary>
internal static class VipThumbs
{
    private static readonly Lazy<List<ZipArchive>> Pack = new(Open);
    private static readonly object Gate = new();

    private static List<ZipArchive> Open()
    {
        var parts = new List<ZipArchive>();
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("RpgLauncher.Assets.vip-thumbs-", StringComparison.Ordinal) && n.EndsWith(".zip", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        foreach (string name in names)
        {
            try
            {
                if (assembly.GetManifestResourceStream(name) is { } stream) parts.Add(new ZipArchive(stream, ZipArchiveMode.Read));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Diagnostics.Log($"VIPRPG thumbnails not loaded ({name}): {ex.Message}");
            }
        }
        return parts;
    }

    /// <summary>The pack's name for a work's picture ("2005-kouhaku/01"), or null without an entry number.</summary>
    public static string? KeyFor(VipWork work) => string.IsNullOrWhiteSpace(work.No) ? null : $"{work.Matsuri}/{work.No}";

    /// <summary>
    /// The pack's name for a WOLF RPG Editor contest entry's picture ("wolf/04/11", from the contest site's entry
    /// list): from its area (wodicon04) and the entry number its title starts with ("11 - アストロラーベ").
    /// </summary>
    /// <remarks>Any other server game's picture is named by its path without ".zip" ("server/games/thread/other/tkool2000/hanayome").</remarks>
    public static string? KeyFor(VipServerGame game) => VipWolf.EntryOf(game) is { } entry ? WolfKey(entry.Contest, entry.No)
        : game.Path.Length > 0 ? "server/" + (game.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? game.Path[..^4] : game.Path) : null;

    /// <summary>The pack's name for a WOLF RPG Editor contest entry's picture, whether or not the server has the game.</summary>
    public static string KeyFor(VipWolfEntry entry) => WolfKey(entry.Contest, entry.No);

    private static string WolfKey(int contest, int no) => $"wolf/{contest:00}/{no}";

    /// <summary>True when the launcher has a picture with this name.</summary>
    public static bool Has(string? key) => Entry(key) is not null;

    private static ZipArchiveEntry? Entry(string? key)
    {
        if (key is null) return null;
        lock (Gate)
        {
            foreach (var part in Pack.Value)
                if (part.GetEntry(key + ".webp") is { } entry) return entry;
            return null;
        }
    }

    /// <summary>The picture with this name, or null if there's none. Safe off the UI thread.</summary>
    public static Bitmap? Load(string? key)
    {
        var buffer = new MemoryStream();
        lock (Gate)
        {
            if (Entry(key) is not { } entry) return null;
            using var input = entry.Open();
            input.CopyTo(buffer);
        }
        buffer.Position = 0;
        return new Bitmap(buffer);
    }
}
