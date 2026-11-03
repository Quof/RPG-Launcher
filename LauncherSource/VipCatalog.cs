using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RpgLauncher;

/// <summary>A VIPRPG matsuri (festival event) from the VIPRPG wiki.</summary>
internal sealed class VipMatsuri
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Year { get; set; }

    /// <summary>kouhaku, gw, summer or special (fes.viprpg.org's types).</summary>
    public string? Type { get; set; }
    public string? Period { get; set; }
    public string? WikiUrl { get; set; }

    /// <summary>The matsuri's own site, when it still exists.</summary>
    public string? SiteUrl { get; set; }

    /// <summary>The archived copy of the matsuri site on fes.viprpg.org.</summary>
    public string? ArchiveUrl { get; set; }

    /// <summary>The name without the "VIPRPG" every matsuri starts with.</summary>
    [JsonIgnore]
    public string ShortName => Regex.Replace(Name, @"^\s*VIPRPG(?:\s*ツクール)?[\s　-]*", "").Trim() is { Length: > 0 } rest ? rest : Name;

    /// <summary>The works entered in it, in entry order.</summary>
    [JsonIgnore] public List<VipWork> Works { get; } = new();
}

/// <summary>A game entered in a matsuri.</summary>
internal sealed class VipWork
{
    public string Matsuri { get; set; } = "";
    public string? No { get; set; }
    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public string? WikiUrl { get; set; }
    public string? Genre { get; set; }
    public string? Engine { get; set; }

    /// <summary>The work's thread on the matsuri's board (感想スレ).</summary>
    public string? ForumUrl { get; set; }

    /// <summary>The download links the matsuri listed (itch.io, Google Drive, MEGA…), many of them dead by now.</summary>
    public List<string>? Downloads { get; set; }

    [JsonIgnore] public VipMatsuri? Event { get; set; }

    /// <summary>The copy on this PC, once matched.</summary>
    [JsonIgnore] public GameEntry? Local { get; set; }

    /// <summary>An unextracted zip on this PC that looks like this work.</summary>
    [JsonIgnore] public string? LocalZip { get; set; }
}

/// <summary>The VIPRPG works list (embedded from the VIPRPG wiki), and matching it to the games on this PC.</summary>
internal sealed class VipCatalog
{
    public string? Source { get; set; }
    public string? Fetched { get; set; }
    public List<VipMatsuri> Matsuri { get; set; } = new();
    public List<VipWork> Games { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>The catalog built into the launcher; empty if it can't be read.</summary>
    public static VipCatalog Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RpgLauncher.Assets.vip-catalog.json");
            var catalog = stream is null ? null : JsonSerializer.Deserialize<VipCatalog>(stream, Options);
            if (catalog is not null)
            {
                catalog.Link();
                return catalog;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("VIPRPG catalog not loaded: " + ex.Message);
        }
        return new VipCatalog();
    }

    private void Link()
    {
        var byId = new Dictionary<string, VipMatsuri>(StringComparer.OrdinalIgnoreCase);
        foreach (var matsuri in Matsuri) byId.TryAdd(matsuri.Id, matsuri);
        foreach (var work in Games)
        {
            if (!byId.TryGetValue(work.Matsuri, out var matsuri)) continue;
            work.Event = matsuri;
            matsuri.Works.Add(work);
        }
    }

    /// <summary>Authors with their works, most prolific first.</summary>
    public List<(string Author, List<VipWork> Works)> ByAuthor() =>
        Games.Where(work => !string.IsNullOrWhiteSpace(work.Author))
            .GroupBy(work => work.Author!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.First().Author!.Trim(), group.ToList()))
            .OrderByDescending(entry => entry.Item2.Count)
            .ThenBy(entry => entry.Item1, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // ---- matching games on this PC to works ----

    // Folder and zip names such as "kouhaku2024_32", "kohaku_011", "gw16_18", "No.21 Title", "12th_1924".
    private static readonly Regex NumberRegex = new(@"(?:no\.?\s*|[_#-])0*(?<no>[0-9]{1,3})(?![0-9])|^0*(?<no>[0-9]{1,3})(?=[-_ .])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YearRegex = new(@"(?<![0-9])(?:20)?(?<year>0[4-9]|1[0-9]|2[0-9])(?![0-9])|(?<year4>20[0-2][0-9])", RegexOptions.Compiled);

    private static readonly (string Pattern, string Kind)[] Kinds =
    {
        ("(?<![a-z])ura|裏", "ura"),
        (@"kouhaku|kohaku|紅白|kh(?=\d)", "kouhaku"),
        ("(?<![a-z])gw|ゴールデン|golden", "gw"),
        ("natsu|summer|夏", "natsu")
    };

    /// <summary>
    /// Points works at the games and zips on this PC: by entry number when a folder names its matsuri ("Kouhaku 2024",
    /// "kouhaku2024_32"), otherwise by title. Returns the games no work matched.
    /// </summary>
    public List<GameEntry> Match(IReadOnlyList<GameEntry> games, IReadOnlyList<string> zips)
    {
        foreach (var work in Games)
        {
            work.Local = null;
            work.LocalZip = null;
        }

        var byTitle = new Dictionary<string, List<VipWork>>(StringComparer.Ordinal);
        foreach (var work in Games)
        {
            string key = TitleKey(work.Title);
            if (key.Length == 0) continue;
            if (!byTitle.TryGetValue(key, out var list)) byTitle[key] = list = new List<VipWork>();
            list.Add(work);
        }

        var unmatched = new List<GameEntry>();
        foreach (var game in games)
        {
            var work = FindWork(game.RootPath, new[] { game.DisplayTitle, game.LocalName, game.FolderName }, byTitle, w => w.Local is null);
            if (work is null) unmatched.Add(game);
            else work.Local = game;
        }
        foreach (string zip in zips)
        {
            string name = Path.GetFileNameWithoutExtension(zip);
            var work = FindWork(zip, new[] { name }, byTitle, w => w.Local is null && w.LocalZip is null);
            if (work is not null) work.LocalZip = zip;
        }
        return unmatched;
    }

    private VipWork? FindWork(string path, IEnumerable<string> names, Dictionary<string, List<VipWork>> byTitle,
        Func<VipWork, bool> free)
    {
        var events = EventsFor(path);

        // An entry number with a matsuri named by the folder or a parent folder.
        if (events.Count > 0 && NumberRegex.Match(Path.GetFileName(path)) is { Success: true } number)
        {
            int no = int.Parse(number.Groups["no"].Value);
            foreach (var matsuri in events)
            {
                var work = matsuri.Works.FirstOrDefault(w => free(w) && int.TryParse(Digits(w.No), out int n) && n == no);
                if (work is not null) return work;
            }
        }

        // The title, preferring a work from a matsuri the path names.
        foreach (string name in names)
        {
            string key = TitleKey(name);
            if (key.Length < 2 || !byTitle.TryGetValue(key, out var candidates)) continue;
            var free_ = candidates.Where(free).ToList();
            if (free_.Count == 0) continue;
            return free_.FirstOrDefault(w => w.Event is not null && events.Contains(w.Event)) ?? free_[0];
        }
        return null;
    }

    private static string Digits(string? text) => new((text ?? "").Where(char.IsAsciiDigit).ToArray());

    /// <summary>
    /// Matsuri a path seems to belong to: a kind (kouhaku, gw, natsu, ura) and a year named by the file or folder
    /// names, deepest first ("Kouhaku 2024\kouhaku2024_32", "2006\kohaku_011", "gw16_18").
    /// </summary>
    private List<VipMatsuri> EventsFor(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        int? year = null;
        var kinds = new List<string>();
        foreach (string part in parts.Reverse().Take(3))
        {
            string lower = part.ToLowerInvariant();
            if (year is null && YearRegex.Match(lower) is { Success: true } match)
            {
                year = match.Groups["year4"].Success ? int.Parse(match.Groups["year4"].Value)
                    : 2000 + int.Parse(match.Groups["year"].Value);
            }
            foreach (var (pattern, kind) in Kinds)
            {
                if (!kinds.Contains(kind) && Regex.IsMatch(lower, pattern)) kinds.Add(kind);
            }
        }
        var found = new List<VipMatsuri>();
        if (year is null) return found;
        foreach (var kind in kinds)
            found.AddRange(Matsuri.Where(m => m.Year == year && KindOf(m) == kind && !found.Contains(m)));
        return found;
    }

    private static string? KindOf(VipMatsuri matsuri)
    {
        if (matsuri.Name.Contains('裏')) return "ura";
        switch (matsuri.Type)
        {
            case "kouhaku": return "kouhaku";
            case "gw": return "gw";
            case "summer": return "natsu";
        }
        string text = (matsuri.Id + " " + matsuri.Name).ToLowerInvariant();
        foreach (var (pattern, kind) in Kinds)
        {
            if (Regex.IsMatch(text, pattern)) return kind;
        }
        return null;
    }

    /// <summary>A title reduced for comparing: no spaces, brackets, versions or punctuation; full-width folded.</summary>
    public static string TitleKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        string text = title.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
        text = Regex.Replace(text, @"ver\.?\s*[0-9][0-9.]*[a-z]?|v[0-9][0-9.]+", "");
        text = Regex.Replace(text, @"^(?:no\.?\s*\d+|\d+[\s._-]+)", "");
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
        }
        return builder.ToString();
    }
}
