using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RpgLauncher;

/// <summary>One entry of a WOLF RPG Editor contest (ウディコン), from the contest site's entry list.</summary>
internal sealed class VipWolfEntry
{
    public int Contest { get; set; }
    public int No { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";

    /// <summary>The download link the entry list gave, which may be gone by now.</summary>
    public string? Link { get; set; }

    /// <summary>The contest's year: 第1回 was 2009.</summary>
    public int Year => 2008 + Contest;
}

/// <summary>
/// Every entry of the WOLF RPG Editor contests 1-18, built into the launcher as Assets/wolf-entries.json, so the
/// WOLF RPG list can show the entries the server has no copy of as well as the ones it does.
/// </summary>
internal static class VipWolf
{
    private sealed class File
    {
        public List<VipWolfEntry> Entries { get; set; } = new();
    }

    private static readonly Lazy<List<VipWolfEntry>> All = new(Load);

    /// <summary>Every entry, by contest and then entry number.</summary>
    public static IReadOnlyList<VipWolfEntry> Entries => All.Value;

    private static List<VipWolfEntry> Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RpgLauncher.Assets.wolf-entries.json");
            var file = stream is null ? null : JsonSerializer.Deserialize<File>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (file is not null) return file.Entries.OrderBy(e => e.Contest).ThenBy(e => e.No).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Diagnostics.Log("WOLF RPG entry list not loaded: " + ex.Message);
        }
        return new List<VipWolfEntry>();
    }

    private static readonly Regex ScreenShotLine = new(@"^([ \t]*ScreenShotFlag[ \t]*=[ \t]*)[^\r\n#]*", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Turns a WOLF RPG Editor game's own screenshot key on or off: ScreenShotFlag in the Game.ini beside its exe,
    /// which the engine reads at start. The file is plain key=value lines with Shift-JIS comments, so every other
    /// byte is kept as it was. Turning it off adds the line (or the file) when it's missing; turning it on only
    /// changes a line that's there, so games that never had a Game.ini don't get one.
    /// </summary>
    public static void SetScreenshotKey(string exe, bool on)
    {
        string ini = Path.Combine(Path.GetDirectoryName(exe) ?? "", "Game.ini");
        try
        {
            bool exists = System.IO.File.Exists(ini);
            if (on && !exists) return;
            string text = exists ? System.IO.File.ReadAllText(ini, System.Text.Encoding.Latin1) : "";
            string value = on ? "1" : "0";
            string updated = ScreenShotLine.IsMatch(text)
                ? ScreenShotLine.Replace(text, m => m.Groups[1].Value + value, 1)
                : on ? text
                : text + (text.Length == 0 || text.EndsWith('\n') ? "" : "\r\n") + "ScreenShotFlag=0\r\n";
            if (updated != text) System.IO.File.WriteAllText(ini, updated, System.Text.Encoding.Latin1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"WOLF screenshot setting not changed for {ini}: {ex.Message}");
        }
    }

    /// <summary>True for the server's contest areas (wodicon01…).</summary>
    public static bool IsContestArea(string? area) => area?.StartsWith("wodicon", StringComparison.Ordinal) == true;

    /// <summary>
    /// The contest and entry number of a server game in a contest area (wodicon04, "11 - アストロラーベ"), or null.
    /// </summary>
    public static (int Contest, int No)? EntryOf(VipServerGame game)
    {
        if (!IsContestArea(game.Area) || !int.TryParse(game.Area!["wodicon".Length..], out int contest)) return null;
        var number = Regex.Match(game.Title ?? "", @"^(\d+) - ");
        return number.Success ? (contest, int.Parse(number.Groups[1].Value)) : null;
    }
}
