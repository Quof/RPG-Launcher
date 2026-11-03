using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RpgLauncher;

/// <summary>A file in the VIPRPG uploader archive.</summary>
internal sealed class VipUpload
{
    public required VipUploadArea Area { get; init; }
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? Extension { get; init; }
    public long Size { get; init; }
    public string? Md5 { get; init; }
    public string? Comment { get; init; }

    /// <summary>False when the archive holds no copy (antivirus exclusions, password-protected uploads).</summary>
    public bool Hosted { get; init; }

    public string Url => $"https://archive.org/download/{VipUploader.Item}/{Area.Id}/{Id:D6}.{Extension}";

    /// <summary>True for archives and programs (a game), false for pictures, text, and saves, music or maps by their name.</summary>
    public bool IsGame => Extension?.ToLowerInvariant() is "zip" or "rar" or "7z" or "lzh" or "exe" &&
                          !VipUploader.NotGameName.IsMatch(System.IO.Path.GetFileNameWithoutExtension(Name));

    /// <summary>A patch or fix rather than the game itself, going by its name or its upload area.</summary>
    public bool IsPatch => VipUploader.PatchName.IsMatch(System.IO.Path.GetFileNameWithoutExtension(Name)) || VipUploader.PatchArea.IsMatch(Area.Id) ||
                           (VipUploader.FixName.IsMatch(Name) && Size < 2_000_000);
}

/// <summary>An upload area (one matsuri uploader, a thread's uploader…) as the archive groups them.</summary>
internal sealed class VipUploadArea
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Title { get; init; } = "";
    public List<VipUpload> Files { get; } = new();

    /// <summary>The matsuri this area served, when its label names one.</summary>
    public VipMatsuri? Matsuri { get; set; }

    /// <summary>
    /// A matsuri's own uploader (its section label starts with the year, "2010 夏之阵"), even one the catalog has no
    /// matsuri for. Its works are the matsuri's entries; what's left over is patches and re-uploads.
    /// </summary>
    public bool Festival => Matsuri is not null || FestivalLabel.IsMatch(Label);

    private static readonly Regex FestivalLabel = new(@"^\s*(?:19|20)[0-9]{2}\b", RegexOptions.Compiled);
}

/// <summary>
/// The VIPRPG uploader archive (uploader.viprpg.org, files on archive.org): an index built into the launcher of
/// every file from the old matsuri and thread uploaders, matched to catalog works, and downloads from it.
/// </summary>
internal sealed class VipUploader
{
    public const string Item = "VIPRPG-uploaderjp-Archive";

    // Patch files ("no28パッチ2.zip", "差分", "fix") and patch areas ("vipkouhaku2024_patch", "kh2015p", "vipsum_p").
    // Also map fixes ("45no1map改.zip") and patch-suffixed numbers ("No.094p.zip").
    internal static readonly Regex PatchName = new(@"パッチ|patch|差分|(?<![a-z])fix(?![a-z])|(?<![a-z])map(?![a-z])|[0-9]map|(?<=[0-9])p$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Saves, music, maps and materials ("セーブ1.zip", "Save01", "Music.zip", "Map0020.zip", "素材.zip").
    internal static readonly Regex NotGameName = new(@"セーブ|^save[0-9]*$|^music$|^bgm$|^map[0-9]+$|^system[0-9]*$|素材|^manual$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Fixes ("ネクロ修正.zip", "Legacy_hotfix1"): patches when small, a whole fixed release otherwise.
    internal static readonly Regex FixName = new(@"修正|修復|hotfix|s(?:y|h)uu?sei", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static readonly Regex PatchArea = new(@"patch|_p[0-9]*$|[0-9]p$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearRegex = new(@"(?<![0-9])(20[0-2][0-9])(?![0-9])", RegexOptions.Compiled);
    // Entry numbers in file names: "No.39 えあわせ", "URAsummer2016-no26" (strong); "vipgw17_08", "n04v02", "game52" (weak).
    private static readonly Regex StrongNumber = new(@"(?:(?<![a-z])|(?<=teisyutu|teisyutsu|sakuhin))no\.?\s*0*(?<no>[0-9]{1,3})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Dates in names ("007_11_01_12", "LAM差分20101228") aren't entry numbers.
    private static readonly Regex DateText = new(@"(?<![0-9])(?:[0-9]{2}[_\-.][0-9]{2}[_\-.][0-9]{2}|20[0-9]{6})(?![0-9])", RegexOptions.Compiled);
    private static readonly Regex WeakNumber = new(@"(?:[_#\-\s　]|^|(?<![a-z])n|^game|^entry)0*(?<no>[0-9]{1,3})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VersionText = new(@"v(?:er)?\.?\s*[0-9]+(?:[.\-_][0-9]+)*[a-z]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<VipUploadArea> Areas { get; } = new();

    /// <summary>The index built into the launcher, linked to the catalog's matsuri; empty if it can't be read.</summary>
    public static VipUploader Load(VipCatalog catalog)
    {
        var uploader = new VipUploader();
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RpgLauncher.Assets.vip-uploader.json");
            if (stream is null) return uploader;
            using var document = JsonDocument.Parse(stream);
            foreach (var areaElement in document.RootElement.GetProperty("areas").EnumerateArray())
            {
                var area = new VipUploadArea
                {
                    Id = areaElement.GetProperty("id").GetString() ?? "",
                    Label = areaElement.TryGetProperty("label", out var label) ? label.GetString() ?? "" : "",
                    Title = areaElement.TryGetProperty("title", out var title) ? title.GetString() ?? "" : ""
                };
                // Files are [id, name, ext, size, md5, comment, hosted].
                foreach (var file in areaElement.GetProperty("files").EnumerateArray())
                {
                    area.Files.Add(new VipUpload
                    {
                        Area = area,
                        Id = file[0].GetInt32(),
                        Name = file[1].GetString() ?? "",
                        // Files the archive didn't keep have no type recorded; their name still says it.
                        Extension = file[2].ValueKind == JsonValueKind.String ? file[2].GetString()
                            : Path.GetExtension(file[1].GetString() ?? "").TrimStart('.') is { Length: > 0 } ext ? ext.ToLowerInvariant() : null,
                        Size = file[3].ValueKind == JsonValueKind.Number ? file[3].GetInt64() : 0,
                        Md5 = file[4].ValueKind == JsonValueKind.String ? file[4].GetString() : null,
                        Comment = file[5].ValueKind == JsonValueKind.String ? file[5].GetString() : null,
                        Hosted = file[6].GetInt32() == 1
                    });
                }
                area.Matsuri = MatsuriFor(area, catalog);
                uploader.Areas.Add(area);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("VIPRPG uploader index not loaded: " + ex.Message);
        }
        return uploader;
    }

    /// <summary>The matsuri an area served, from its section label ("2024 红白", "2016 里夏之阵", "2012 魔法祭").</summary>
    private static VipMatsuri? MatsuriFor(VipUploadArea area, VipCatalog catalog)
    {
        string text = area.Label + " " + area.Title;
        if (YearRegex.Match(text) is not { Success: true } year) return null;
        int y = int.Parse(year.Value);
        var sameYear = catalog.Matsuri.Where(m => m.Year == y).ToList();
        bool ura = text.Contains('里') || text.Contains('裏');
        string? type = text.Contains("红白") || text.Contains("紅白") ? "kouhaku"
            : text.Contains("夏") ? "summer"
            : text.Contains("GW", StringComparison.OrdinalIgnoreCase) ? "gw"
            : null;
        if (type is not null)
        {
            var matches = sameYear.Where(m => m.Type == type).ToList();
            return matches.FirstOrDefault(m => m.Name.Contains('裏') == ura) ?? matches.FirstOrDefault();
        }
        // Special matsuri: a word of the label in the matsuri's name.
        foreach (var (word, name) in new[] { ("魔法", "魔法"), ("电波", "電波"), ("ksg", "ksg") })
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                return sameYear.FirstOrDefault(m => m.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    /// <summary>The best archived download for a work: a game file from its matsuri's areas, by entry number or title.</summary>
    public VipUpload? FindFor(VipWork work, bool hosted = true, ISet<VipUpload>? taken = null)
    {
        if (work.Event is not { } matsuri) return null;
        VipUpload? best = null;
        int bestScore = 0;
        foreach (var area in Areas.Where(a => a.Matsuri == matsuri))
        {
            // Only files the archive kept: an unhosted best match would leave the work with nothing.
            foreach (var file in area.Files.Where(f => f.Hosted == hosted))
            {
                if (taken?.Contains(file) == true) continue;
                int score = Score(work, file);
                if (score == 0) continue;
                // Later uploads replace earlier ones (fixed versions).
                if (best is null || score > bestScore || (score == bestScore && file.Id > best.Id))
                {
                    best = file;
                    bestScore = score;
                }
            }
        }
        return best;
    }

    /// <summary>How well a file fits a work: 0 for not at all; more for its entry number and title.</summary>
    public static int Score(VipWork work, VipUpload file)
    {
        // A patch alone isn't playable.
        if (!file.IsGame || file.IsPatch) return 0;
        int? no = int.TryParse(new string((work.No ?? "").Where(char.IsAsciiDigit).ToArray()), out int n) ? n : null;
        string title = VipCatalog.TitleKey(work.Title);
        int score = 0;
        string name = file.Name + " " + file.Comment;
        // Version numbers ("Ver1-4", "v1.21") aren't entry numbers.
        // NFKC first, so "№29" reads as "No29" and full-width digits as digits.
        string stem = DateText.Replace(VersionText.Replace(Path.GetFileNameWithoutExtension(file.Name).Normalize(System.Text.NormalizationForm.FormKC), " "), " ");
        if (no is not null)
        {
            if (StrongNumber.Matches(stem).Any(m => int.Parse(m.Groups["no"].Value) == no)) score += 10;
            // Only the first loose number: later ones are versions ("sakuhin_114_2" is entry 114's second upload).
            else if (WeakNumber.Match(stem) is { Success: true } weak && int.Parse(weak.Groups["no"].Value) == no &&
                     !NamesOtherGame(stem, title)) score += 6;
        }
        string key = VipCatalog.TitleKey(name);
        if (title.Length >= 3 && key.Contains(title)) score += 12;
        return score;
    }

    // Words in file names that say nothing about which game it is: matsuri names, "game", "entry", "sakuhin"…
    private static readonly Regex NameWords = new(@"[a-z]{3,}|[^\x00-\x7F0-9]{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> MatsuriWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "vip", "viprpg", "rpg", "kohaku", "kouhaku", "kouhaku", "summer", "natsu", "natu", "natunozin", "natsunojin",
        "natunojin", "sakuhin", "game", "entry", "ver", "final", "ura", "gw", "matsuri", "tkool",
        "紅白", "夏", "夏の陣", "作品", "祭り", "祭", "提出", "提出用", "本体", "完成版", "修正版", "体験版", "最新版"
    };

    /// <summary>
    /// True when a file's name carries a word of its own (not a matsuri word) that isn't in the work's title:
    /// "EMPIRE_003" is the EMPIRE game's version 003, not entry 003.
    /// </summary>
    private static bool NamesOtherGame(string stem, string titleKey) =>
        NameWords.Matches(stem).Select(m => m.Value).Any(word =>
            !MatsuriWords.Contains(word) && !MatsuriPrefix.IsMatch(word.ToLowerInvariant()) &&
            !titleKey.Contains(VipCatalog.TitleKey(word), StringComparison.Ordinal));

    /// <summary>True for an https link on archive.org, where the uploader archive keeps its files.</summary>
    public static bool IsArchiveUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("archive.org", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The archive as downloadable games: each catalog work's best file, and every other archived game file
    /// (thread uploads and the like) on its own, by area. Pictures, saves and patches not matched to a work are left out.
    /// </summary>
    public List<VipServerGame> ToGames(VipCatalog catalog)
    {
        var games = new List<VipServerGame>();
        var used = new HashSet<VipUpload>();
        foreach (var work in catalog.Games)
        {
            // A file is one work's: a work whose best file is taken gets its next best.
            if (string.IsNullOrWhiteSpace(work.No) || FindFor(work, taken: used) is not { } file || !used.Add(file)) continue;
            games.Add(Game(file, work));
        }
        // Files still unclaimed in a matsuri's uploaders, for works still without one, by a looser title match.
        var matched = new HashSet<VipWork>(games.Select(g => catalog.Games.First(w => w.Matsuri == g.Matsuri && w.No == g.No)));
        foreach (var area in Areas.Where(a => a.Matsuri is not null))
        {
            var open = catalog.Games.Where(w => w.Event == area.Matsuri && !string.IsNullOrWhiteSpace(w.No) && !matched.Contains(w)).ToList();
            // Newest first: later uploads are the fixed versions.
            foreach (var file in area.Files.OrderByDescending(f => f.Id))
            {
                if (!file.Hosted || !file.IsGame || file.IsPatch || used.Contains(file)) continue;
                if (LooseMatch(file, open) is not { } work) continue;
                used.Add(file);
                matched.Add(work);
                open.Remove(work);
                games.Add(Game(file, work));
            }
        }
        // Works still without one, from any uploader (the wiki's and lost-works uploaders kept many), when the file is
        // named exactly for the work: its title, maybe after "No.26_" or the matsuri's name.
        var byTitle = catalog.Games.Where(w => !string.IsNullOrWhiteSpace(w.No) && !matched.Contains(w))
            .GroupBy(w => VipCatalog.TitleKey(w.Title)).Where(g => g.Key.Length >= 4 && g.Count() == 1 && !GenericTitle.IsMatch(g.Key))
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var file in Areas.SelectMany(a => a.Files).OrderByDescending(f => f.Size))
        {
            if (!file.Hosted || !file.IsGame || file.IsPatch || used.Contains(file)) continue;
            if (ExactMatch(file, byTitle) is not { } work) continue;
            used.Add(file);
            matched.Add(work);
            byTitle.Remove(VipCatalog.TitleKey(work.Title));
            games.Add(Game(file, work));
        }
        // Works whose only match is a file the archive didn't keep: many are still on the live uploader.
        foreach (var work in catalog.Games)
        {
            if (string.IsNullOrWhiteSpace(work.No) || matched.Contains(work) || FindFor(work, hosted: false, taken: used) is not { } file ||
                !used.Add(file)) continue;
            games.Add(Live(file, work));
        }
        // The thread and wiki storage uploaders' other games. A matsuri uploader's unclaimed files are left out: they
        // are its patches, extra copies and re-uploads, not games of their own.
        foreach (var area in Areas.Where(a => !a.Festival))
        {
            foreach (var file in area.Files)
            {
                if (!file.Hosted || !file.IsGame || file.IsPatch || used.Contains(file)) continue;
                games.Add(Game(file, null));
            }
        }
        return games;
    }

    // Placeholder titles many unrelated games share ("Project2", "Unnamed project", "untitled").
    private static readonly Regex GenericTitle = new(@"^(?:project[0-9]*|unnamedproject|untitled|新規プロジェクト|ksg|test|game|sample|サンプル)$",
        RegexOptions.Compiled);

    // Leading years and matsuri words in file names ("2024紅白_金リカ2V4"), dropped before comparing titles.
    private static readonly Regex MatsuriPrefix = new(@"^(?:20[0-9]{2}|vip|rpg|紅白|夏|gw|kouhaku|kohaku|summer|natsu|natu|ura|sun|sum)+", RegexOptions.Compiled);

    /// <summary>
    /// The one work whose title holds a file's name, letter by letter in order ("もしも紅葉雪降り" in
    /// "もしも紅葉舞い、雪降る時だったら"), or null when none or several do.
    /// </summary>
    private static VipWork? LooseMatch(VipUpload file, List<VipWork> works)
    {
        string stem = MatsuriPrefix.Replace(VipCatalog.TitleKey(VersionText.Replace(Path.GetFileNameWithoutExtension(file.Name), " ")), "");
        if (stem.Length < 4) return null;
        VipWork? found = null;
        foreach (var work in works)
        {
            string title = VipCatalog.TitleKey(work.Title);
            if (title.Length < 4 || CommonLetters(stem, title) < 0.9 * stem.Length) continue;
            if (found is not null) return null;
            found = work;
        }
        return found;
    }

    /// <summary>The work a file is named for, title for title, or with its entry number in front ("電波祭りNo.26_三馬鹿道中記").</summary>
    private static VipWork? ExactMatch(VipUpload file, Dictionary<string, VipWork> byTitle)
    {
        string raw = VersionText.Replace(Path.GetFileNameWithoutExtension(file.Name), " ");
        string stem = VipCatalog.TitleKey(raw);
        if (byTitle.TryGetValue(stem, out var work)) return work;
        foreach (Match number in StrongNumber.Matches(raw))
        {
            string rest = VipCatalog.TitleKey(raw[(number.Index + number.Length)..]);
            if (byTitle.TryGetValue(rest, out work) &&
                int.TryParse(new string((work.No ?? "").Where(char.IsAsciiDigit).ToArray()), out int no) &&
                no == int.Parse(number.Groups["no"].Value))
                return work;
        }
        return null;
    }

    /// <summary>The longest common subsequence of two strings.</summary>
    private static int CommonLetters(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
                current[j] = a[i - 1] == b[j - 1] ? previous[j - 1] + 1 : Math.Max(previous[j], current[j - 1]);
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>A work's file as it still is on getuploader, fetched through its download page at download time.</summary>
    private static VipServerGame Live(VipUpload file, VipWork work) => new()
    {
        Path = $"live/{file.Area.Id}/{file.Id}",
        Url = $"https://ux.getuploader.com/{file.Area.Id}/download/{file.Id}",
        Size = file.Size,
        Md5 = file.Md5,
        Area = file.Area.Id,
        Source = "live",
        Matsuri = work.Matsuri,
        No = work.No,
        Title = work.Title,
        Author = work.Author,
        Thread = file.Area.Title,
        Section = file.Area.Label,
        Year = file.Area.Matsuri?.Year,
        Engine = work.Engine,
        Note = file.Comment
    };

    private static VipServerGame Game(VipUpload file, VipWork? work) => new()
    {
        Path = $"{file.Area.Id}/{file.Id:D6}.{file.Extension}",
        Url = file.Url,
        Size = file.Size,
        Md5 = file.Md5,
        Area = file.Area.Id,
        Source = work is null ? "uploader" : "matsuri",
        Matsuri = work?.Matsuri,
        No = work?.No,
        Title = work?.Title ?? System.IO.Path.GetFileNameWithoutExtension(file.Name),
        Author = work?.Author,
        Thread = file.Area.Title,
        Section = file.Area.Label,
        Year = file.Area.Matsuri?.Year,
        Engine = work?.Engine,
        Note = file.Comment
    };
}
