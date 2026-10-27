using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RpgLauncher;

/// <summary>One game from DLsite's release calendar.</summary>
internal sealed class ReleaseItem
{
    public string ProductId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Maker { get; set; } = "";
    public string WorkType { get; set; } = "";
    public string WorkTypeName { get; set; } = "";
    public string? Description { get; set; }
    public int Price { get; set; }
    public int OfficialPrice { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> Options { get; set; } = new();
    public double? Rating { get; set; }
    public int RatingCount { get; set; }
    public int? Sales { get; set; }

    /// <summary>Image path under images2/ without size or extension, e.g. "work/doujin/{folder}/{id}_img_main".</summary>
    public string? ImageBase { get; set; }

    public string Url { get; set; } = "";

    /// <summary>English circle, type and tags from the English calendar page, when fetched.</summary>
    public ReleaseText? English { get; set; }

    [JsonIgnore] public bool HasEnglish => Options.Contains("ENG");

    public string MakerFor(MetadataLanguage language) =>
        language == MetadataLanguage.English && !string.IsNullOrWhiteSpace(English?.Maker) ? English!.Maker : Maker;

    public string TypeFor(MetadataLanguage language) =>
        language == MetadataLanguage.English && !string.IsNullOrWhiteSpace(English?.WorkTypeName)
            ? English!.WorkTypeName
            : WorkTypeName;

    public IReadOnlyList<string> TagsFor(MetadataLanguage language) =>
        language == MetadataLanguage.English && English is { Tags.Count: > 0 } english ? english.Tags : Tags;

    /// <summary>Language names for DLsite's option codes, e.g. ENG, CHI_HANT.</summary>
    public IEnumerable<string> LanguageNames()
    {
        foreach (var option in Options)
        {
            string? name = option switch
            {
                "JPN" => "Japanese",
                "ENG" => "English",
                "CHI" or "CHI_HANS" => "Chinese (Simplified)",
                "CHI_HANT" => "Chinese (Traditional)",
                "KO_KR" or "KOR" => "Korean",
                "SPA" => "Spanish",
                "GER" => "German",
                "FRE" => "French",
                "IND" => "Indonesian",
                "ITA" => "Italian",
                "POR" => "Portuguese",
                "SWE" => "Swedish",
                "THA" => "Thai",
                "VIE" => "Vietnamese",
                _ => null
            };
            if (name is not null) yield return name;
        }
    }
    [JsonIgnore] public int DiscountPercent =>
        OfficialPrice > 0 && Price < OfficialPrice ? (int)Math.Round(100.0 * (OfficialPrice - Price) / OfficialPrice) : 0;

    public string? CoverUrl(bool large) => ImageBase is null
        ? null
        : large
            ? $"https://img.dlsite.jp/modpub/images2/{ImageBase}.jpg"
            : $"https://img.dlsite.jp/resize/images2/{ImageBase}_300x300.jpg";
}

/// <summary>English store text for a release (DLsite keeps titles and descriptions in Japanese).</summary>
internal sealed class ReleaseText
{
    public string Maker { get; set; } = "";
    public string WorkTypeName { get; set; } = "";
    public List<string> Tags { get; set; } = new();
}

/// <summary>All game releases for one calendar day (Japan time).</summary>
internal sealed class ReleaseDay
{
    public DateTime Date { get; set; }
    public DateTime FetchedUtc { get; set; }
    public List<ReleaseItem> Items { get; set; } = new();

    /// <summary>True once the English page for this fetch has been merged in.</summary>
    public bool EnglishFetched { get; set; }
}

/// <summary>A calendar day to download, or only its English page.</summary>
internal readonly record struct ReleaseFetch(DateTime Date, bool EnglishOnly);

/// <summary>
/// Caches a month of DLsite's game release calendar. Each day is downloaded once, and again after
/// it ends if it was first read while still current, to pick up late releases.
/// </summary>
internal sealed class ReleaseStore
{
    public const int DaysBack = 30;
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public List<ReleaseDay> Days { get; set; } = new();
    }

    private readonly object _lock = new();
    private readonly Dictionary<DateTime, ReleaseDay> _days = new();

    public string Directory { get; }
    public string CoverDirectory => Path.Combine(Directory, "covers");
    private string FilePath => Path.Combine(Directory, "new-releases.json");

    public ReleaseStore(string directory)
    {
        Directory = directory;
    }

    /// <summary>Today's date in Japan, which DLsite's release days use.</summary>
    public static DateTime TodayJst() => ToJst(DateTime.UtcNow).Date;

    private static DateTime ToJst(DateTime utc)
    {
        try
        {
            var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tokyo);
        }
        catch
        {
            return utc.AddHours(9);
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(FilePath, Encoding.UTF8), Options);
            if (file is null || file.Version != CurrentVersion) return;
            lock (_lock)
            {
                _days.Clear();
                foreach (var day in file.Days) _days[day.Date.Date] = day;
            }
            Prune();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Release cache ignored: " + ex.Message);
        }
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(new CacheFile
                {
                    Version = CurrentVersion,
                    Days = _days.Values.OrderByDescending(d => d.Date).ToList()
                }, Options);
            }
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Release cache not saved: " + ex.Message);
        }
    }

    /// <summary>Days in the window, newest first.</summary>
    public List<ReleaseDay> Days()
    {
        lock (_lock)
        {
            return _days.Values.OrderByDescending(d => d.Date).ToList();
        }
    }

    /// <summary>Forgets every cached day so the whole month is fetched again (covers are kept).</summary>
    public void Clear()
    {
        lock (_lock) _days.Clear();
        Save();
    }

    public void Put(ReleaseDay day)
    {
        lock (_lock) _days[day.Date.Date] = day;
    }

    /// <summary>Drops days older than the window and covers that no longer belong to any day.</summary>
    public void Prune()
    {
        var oldest = TodayJst().AddDays(-DaysBack);
        HashSet<string> keep;
        lock (_lock)
        {
            foreach (var date in _days.Keys.Where(d => d < oldest).ToList()) _days.Remove(date);
            keep = _days.Values.SelectMany(d => d.Items).Select(i => i.ProductId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            if (!System.IO.Directory.Exists(CoverDirectory)) return;
            foreach (var file in System.IO.Directory.EnumerateFiles(CoverDirectory, "*.jpg"))
            {
                string id = Path.GetFileNameWithoutExtension(file).Split('_')[0];
                if (!keep.Contains(id)) File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Release cover prune failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Days to download, newest first: days never fetched, and days fetched while still in progress
    /// (except today, unless <paramref name="refresh"/>). With <paramref name="english"/>, other days
    /// missing English text get only their English page.
    /// </summary>
    public List<ReleaseFetch> DaysToFetch(bool refresh, bool english)
    {
        var today = TodayJst();
        var result = new List<ReleaseFetch>();
        lock (_lock)
        {
            for (int i = 0; i <= DaysBack; i++)
            {
                var date = today.AddDays(-i);
                if (!_days.TryGetValue(date, out var day))
                {
                    result.Add(new ReleaseFetch(date, EnglishOnly: false));
                    continue;
                }

                var fetchedOn = ToJst(day.FetchedUtc).Date;
                bool stillOpenWhenFetched = fetchedOn <= date;
                if (stillOpenWhenFetched && (refresh || fetchedOn < today))
                    result.Add(new ReleaseFetch(date, EnglishOnly: false));
                else if (english && !day.EnglishFetched)
                    result.Add(new ReleaseFetch(date, EnglishOnly: true));
            }
        }
        return result;
    }

    public ReleaseDay? Day(DateTime date)
    {
        lock (_lock) return _days.TryGetValue(date.Date, out var day) ? day : null;
    }

    // ---- fetching ----

    private static readonly Regex ItemSplit = new("<div class=\"n_worklist_item\"", RegexOptions.Compiled);
    private static readonly Regex IdRegex = new("data-product_id=\"(?<v>[A-Za-z]{2}\\d+)\"", RegexOptions.Compiled);
    private static readonly Regex DataNameRegex = new("data-work_name=\"(?<v>[^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex WorkTypeRegex = new("data-work_type=\"(?<v>[A-Za-z0-9]+)\"", RegexOptions.Compiled);
    private static readonly Regex PriceRegex = new("data-price=\"(?<v>\\d+)\"", RegexOptions.Compiled);
    private static readonly Regex OfficialPriceRegex = new("data-official_price=\"(?<v>\\d+)\"", RegexOptions.Compiled);
    private static readonly Regex OptionsRegex = new("data-options=\"(?<v>[^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex TitleLinkRegex = new(
        "<dt class=\"work_name\">.*?<a href=\"(?<url>[^\"]+)\"[^>]*title=\"(?<v>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex TypeNameRegex = new(
        "class=\"work_category type_[A-Za-z0-9]+\"><a[^>]*>(?<v>[^<]*)</a>", RegexOptions.Compiled);
    private static readonly Regex MakerRegex = new(
        "<dd class=\"maker_name\">.*?<a[^>]*>(?<v>[^<]*)</a>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex TextRegex = new(
        "<dd class=\"work_text\">(?<v>.*?)</dd>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex TagBlockRegex = new(
        "<dd class=\"search_tag\">(?<v>.*?)</dd>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex AnchorTextRegex = new("<a[^>]*>(?<v>[^<]*)</a>", RegexOptions.Compiled);
    private static readonly Regex RatingRegex = new(
        "star_rating star_(?<s>\\d+)[^\"]*\">\\((?<n>[\\d,]+)\\)", RegexOptions.Compiled);
    private static readonly Regex SalesRegex = new("_dl_count_[A-Za-z0-9]+\">(?<v>[\\d,]+)<", RegexOptions.Compiled);
    private static readonly Regex ImageRegex = new(
        "images2/(?<v>[^'\"]+?_img_main)(?:_\\d+x\\d+)?\\.(?:jpg|webp)", RegexOptions.Compiled);
    private static readonly Regex TagStripRegex = new("<[^>]+>", RegexOptions.Compiled);

    public static string DayUrl(DateTime date) =>
        $"https://www.dlsite.com/maniax/new/=/date/{date:yyyy-MM-dd}/cdate/{date:yyyy-MM}/work_type_category/game/show_layout/2";

    private static string EnglishDayUrl(DateTime date) => DayUrl(date) + "?locale=en_US";

    /// <summary>
    /// Downloads a day's Japanese page and, with <paramref name="english"/>, its English page after a
    /// short pause. A failed English page is logged and retried next time.
    /// </summary>
    public static async Task<ReleaseDay> FetchDayAsync(DateTime date, bool english, CancellationToken token)
    {
        string html = await MetadataService.Client.GetStringAsync(DayUrl(date), token).ConfigureAwait(false);
        var day = new ReleaseDay { Date = date.Date, FetchedUtc = DateTime.UtcNow, Items = ParseDay(html) };
        if (english && day.Items.Count > 0)
        {
            try
            {
                await Task.Delay(700, token).ConfigureAwait(false);
                await AddEnglishAsync(day, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"English releases for {date:yyyy-MM-dd} failed: {ex.Message}");
            }
        }
        else if (day.Items.Count == 0)
        {
            day.EnglishFetched = true;
        }
        return day;
    }

    /// <summary>Fetches only the English page for a cached day and returns an updated copy.</summary>
    public static async Task<ReleaseDay> FetchEnglishAsync(ReleaseDay cached, CancellationToken token)
    {
        var day = new ReleaseDay
        {
            Date = cached.Date,
            FetchedUtc = cached.FetchedUtc,
            Items = cached.Items.Select(item => item).ToList()
        };
        await AddEnglishAsync(day, token).ConfigureAwait(false);
        return day;
    }

    private static async Task AddEnglishAsync(ReleaseDay day, CancellationToken token)
    {
        string html = await MetadataService.Client.GetStringAsync(EnglishDayUrl(day.Date), token).ConfigureAwait(false);
        var english = ParseDay(html).ToDictionary(item => item.ProductId, StringComparer.OrdinalIgnoreCase);
        foreach (var item in day.Items)
        {
            if (!english.TryGetValue(item.ProductId, out var en)) continue;
            item.English = new ReleaseText { Maker = en.Maker, WorkTypeName = en.WorkTypeName, Tags = en.Tags };
        }
        day.EnglishFetched = true;
    }

    /// <summary>Parses a calendar day page, skipping video works.</summary>
    public static List<ReleaseItem> ParseDay(string html)
    {
        var items = new List<ReleaseItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chunks = ItemSplit.Split(html);

        static string Value(Regex regex, string text) =>
            regex.Match(text) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups["v"].Value).Trim() : "";

        static int Number(Regex regex, string text) =>
            int.TryParse(Value(regex, text).Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

        for (int i = 1; i < chunks.Length; i++)
        {
            string chunk = chunks[i];
            string id = Value(IdRegex, chunk).ToUpperInvariant();
            if (id.Length == 0 || !seen.Add(id)) continue;

            string workType = Value(WorkTypeRegex, chunk);
            if (string.Equals(workType, "MOV", StringComparison.OrdinalIgnoreCase)) continue;

            var link = TitleLinkRegex.Match(chunk);
            string title = link.Success ? WebUtility.HtmlDecode(link.Groups["v"].Value).Trim() : Value(DataNameRegex, chunk);
            string url = link.Success
                ? link.Groups["url"].Value
                : $"https://www.dlsite.com/maniax/work/=/product_id/{id}.html";

            var item = new ReleaseItem
            {
                ProductId = id,
                Title = title,
                Url = url,
                Maker = Value(MakerRegex, chunk),
                WorkType = workType,
                WorkTypeName = Value(TypeNameRegex, chunk),
                Price = Number(PriceRegex, chunk),
                OfficialPrice = Number(OfficialPriceRegex, chunk),
                Options = Value(OptionsRegex, chunk).Split('#', StringSplitOptions.RemoveEmptyEntries).ToList(),
                ImageBase = ImageRegex.Match(chunk) is { Success: true } image ? image.Groups["v"].Value : null
            };

            if (TextRegex.Match(chunk) is { Success: true } text)
            {
                string description = WebUtility.HtmlDecode(TagStripRegex.Replace(text.Groups["v"].Value, " ")).Trim();
                if (description.Length > 0) item.Description = Regex.Replace(description, "\\s+", " ");
            }

            if (TagBlockRegex.Match(chunk) is { Success: true } tags)
            {
                foreach (Match tag in AnchorTextRegex.Matches(tags.Groups["v"].Value))
                {
                    string name = WebUtility.HtmlDecode(tag.Groups["v"].Value).Trim();
                    if (name.Length > 0) item.Tags.Add(name);
                }
            }

            if (RatingRegex.Match(chunk) is { Success: true } rating)
            {
                item.Rating = int.Parse(rating.Groups["s"].Value, CultureInfo.InvariantCulture) / 10.0;
                item.RatingCount = int.Parse(rating.Groups["n"].Value.Replace(",", ""), CultureInfo.InvariantCulture);
            }

            if (SalesRegex.Match(chunk) is { Success: true } sales &&
                int.TryParse(sales.Groups["v"].Value.Replace(",", ""), out int sold))
                item.Sales = sold;

            items.Add(item);
        }

        return items;
    }
}
