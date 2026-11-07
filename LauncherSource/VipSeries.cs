using System.Reflection;
using System.Text.Json;

namespace RpgLauncher;

/// <summary>One game of a series: the launcher's key for it (as a playlist has them) and the title the series page gives.</summary>
internal sealed class VipSeriesGame
{
    /// <summary>"&lt;matsuri&gt;/&lt;No&gt;" for a festival work, "dl:&lt;path&gt;" for a server game, "title:&lt;title&gt;" when no copy is known.</summary>
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>
    /// The collection the game is only found inside (the key is that collection's): the row shows the game's own title
    /// and "（…収録）", and opens the collection.
    /// </summary>
    public string? Via { get; set; }
}

/// <summary>A series of works, its games in release order (the series page's order).</summary>
internal sealed class VipSeriesInfo
{
    public string Name { get; set; } = "";
    public string NameEn { get; set; } = "";

    /// <summary>The wiki page the series was taken from.</summary>
    public string Source { get; set; } = "";
    public List<VipSeriesGame> Games { get; set; } = new();
}

/// <summary>
/// The series built into the launcher as Assets/vip-series.json, from VIPRPG@Wiki's series pages and VIPRPGコレクション@Wiki's
/// シリーズ作品 pages, for the シリーズ別 list.
/// </summary>
internal static class VipSeries
{
    private sealed class File
    {
        public List<VipSeriesInfo> Series { get; set; } = new();
    }

    private static readonly Lazy<List<VipSeriesInfo>> All = new(Load);

    /// <summary>Every series, in the file's order.</summary>
    public static IReadOnlyList<VipSeriesInfo> List => All.Value;

    private static List<VipSeriesInfo> Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RpgLauncher.Assets.vip-series.json");
            var file = stream is null ? null : JsonSerializer.Deserialize<File>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (file is not null) return file.Series;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Diagnostics.Log("Series list not loaded: " + ex.Message);
        }
        return new List<VipSeriesInfo>();
    }
}
