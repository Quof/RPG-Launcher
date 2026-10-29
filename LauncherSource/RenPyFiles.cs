using System.Text.RegularExpressions;

namespace RpgLauncher;

/// <summary>
/// A Ren'Py game's files under game\, loose or in its .rpa archives. Finds the game's name and the picture its main
/// menu shows by following its scripts the way Ren'Py does.
/// </summary>
internal sealed class RenPyFiles
{
    private static readonly string[] PictureExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
    private const long MaxScriptBytes = 48 * 1024 * 1024;

    private static readonly Regex NameRegex = new(
        @"^[ \t]*(?:define[ \t]+)?config\.name[ \t]*=[ \t]*(?:_\([ \t]*)?(?<q>[""'])(?<name>(?:(?!\k<q>).)+)\k<q>",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex BackgroundRegex = new(
        @"^[ \t]*(?:define[ \t]+)?gui\.main_menu_background[ \t]*=[ \t]*(?<expr>[^\r\n]+)", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex MainMenuScreenRegex = new(
        @"^(?<indent>[ \t]*)screen[ \t]+main_menu[ \t]*(?:\([^)]*\))?[ \t]*:", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex ImageStatementRegex = new(
        @"^(?<indent>[ \t]*)image[ \t]+(?<name>[^=:\r\n]+?)[ \t]*(?<op>[=:])(?<rest>[^\r\n]*)", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex LiteralRegex = new(@"(?<q>[""'])(?<text>(?:(?!\k<q>)[^\r\n])*)\k<q>", RegexOptions.Compiled);

    private readonly string _game;
    private readonly List<(string Path, RpaArchive Archive)> _archives = new();
    private List<(string Name, string Text)>? _likelyScripts, _otherScripts;
    private List<string>? _looseImages;

    public RenPyFiles(string game)
    {
        _game = game;
        try
        {
            foreach (var path in Directory.EnumerateFiles(game, "*.rpa", Paths.AnyCase).Order(StringComparer.OrdinalIgnoreCase))
                if (RpaArchive.Open(path) is { } archive) _archives.Add((path, archive));
        }
        catch
        {
            // unreadable folder: loose files only
        }
    }

    /// <summary>Where a file under game\ is: its path, or "archive.rpa::name"; null when the game has none.</summary>
    public string? Find(string name)
    {
        name = name.Replace('\\', '/').TrimStart('/');
        if (Paths.File(Path.Combine(_game, name.Replace('/', Path.DirectorySeparatorChar))) is { } loose) return loose;
        foreach (var (path, archive) in _archives)
            if (archive.Contains(name)) return path + RgssArchive.Separator + name;
        return null;
    }

    /// <summary>config.name from the scripts (options.rpy, loose or archived).</summary>
    public string? ConfigName() =>
        Scripts().Select(script => NameRegex.Match(script.Text)).FirstOrDefault(match => match.Success)?.Groups["name"].Value;

    /// <summary>The window icon (gui\window_icon), loose or archived.</summary>
    public string? Icon() => FindPicture("gui/window_icon");

    /// <summary>
    /// The picture the main menu shows: the main_menu screen's first "add", gui.main_menu_background, the image
    /// statement or images\ file that names, then gui\main_menu and the loading screen. Blank ones (like the template's
    /// flat background) and see-through ones (logos, overlays) are passed over. The loading screen's background comes
    /// with its foreground to draw over it, as Ren'Py shows them.
    /// </summary>
    public (string? Picture, string? Overlay) MenuPicture()
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in MenuCandidates())
        {
            foreach (string file in Expand(candidate, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            {
                if (tried.Add(file) && Usable(file)) return (file, null);
            }
        }
        // The template's background, then the loading screen's pictures, beside the scripts in game\.
        foreach (string fallback in new[] { "gui/main_menu", "presplash", "presplash_background" })
        {
            if (FindPicture(fallback) is not { } file || !tried.Add(file) || !Usable(file)) continue;
            return (file, fallback == "presplash_background" ? FindPicture("presplash_foreground") : null);
        }
        return (null, null);
    }

    private IEnumerable<string> MenuCandidates()
    {
        string? background = Scripts().Select(script => BackgroundRegex.Match(script.Text)).Where(match => match.Success)
            .Select(match => FirstLiteral(match.Groups["expr"].Value)).FirstOrDefault(value => value is not null);
        foreach (string added in MainMenuAdds())
        {
            if (added != "gui.main_menu_background") yield return added;
            else if (background is not null) yield return background;
        }
        if (background is not null) yield return background;
    }

    /// <summary>What the main_menu screen adds, in order: quoted names, or "gui.main_menu_background".</summary>
    private IEnumerable<string> MainMenuAdds()
    {
        foreach (var (_, text) in Scripts())
        {
            var screen = MainMenuScreenRegex.Match(text);
            if (!screen.Success) continue;
            foreach (string line in Block(text, screen.Index + screen.Length, screen.Groups["indent"].Value.Length))
            {
                string statement = line.Trim();
                if (!statement.StartsWith("add ", StringComparison.Ordinal)) continue;
                string expr = statement[4..].TrimStart();
                if (expr.StartsWith("gui.main_menu_background", StringComparison.Ordinal)) yield return "gui.main_menu_background";
                else if (expr.Length > 0 && expr[0] is '"' or '\'' && FirstLiteral(expr) is { } literal) yield return literal;
            }
            yield break;
        }
    }

    /// <summary>The files a name in a script stands for: a file path, or an image name and what defines it.</summary>
    private IEnumerable<string> Expand(string text, int depth, HashSet<string> seen)
    {
        text = text.Trim();
        if (depth > 4 || text.Length == 0 || text.StartsWith('#') || !seen.Add(text)) yield break;

        string extension = Path.GetExtension(text);
        if (PictureExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            if (Find(text) is { } file) yield return file;
            yield break;
        }
        // Other files (a movie, say) can't be shown on a card; a path without an extension can be any picture.
        if (extension.Length > 0 || text.Contains('/'))
        {
            if (extension.Length == 0 && FindPicture(text) is { } file) yield return file;
            yield break;
        }

        // An image name: "image name = …" or an "image name:" block in a script, or a picture of that name in images\.
        string name = Words(text);
        foreach (var (_, script) in Scripts())
        {
            foreach (Match statement in ImageStatementRegex.Matches(script))
            {
                if (!Words(statement.Groups["name"].Value).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                var parts = statement.Groups["op"].Value == "="
                    ? Literals(statement.Groups["rest"].Value)
                    : Block(script, statement.Index + statement.Length, statement.Groups["indent"].Value.Length).SelectMany(Literals);
                foreach (string part in parts)
                    foreach (string file in Expand(part, depth + 1, seen))
                        yield return file;
            }
        }
        foreach (string file in ImagesNamed(name)) yield return file;
    }

    private string? FindPicture(string pathWithoutExtension) =>
        PictureExtensions.Select(extension => Find(pathWithoutExtension + extension)).FirstOrDefault(file => file is not null);

    /// <summary>Pictures under images\, loose or archived, whose file name is the image name (Ren'Py's automatic images).</summary>
    private IEnumerable<string> ImagesNamed(string name)
    {
        bool Named(string file) =>
            PictureExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) &&
            Words(Path.GetFileNameWithoutExtension(file)).Equals(name, StringComparison.OrdinalIgnoreCase);

        _looseImages ??= LooseFiles("images", "*.*");
        foreach (string file in _looseImages.Where(Named)) yield return file;
        foreach (var (path, archive) in _archives)
            foreach (string file in archive.Names.Where(file => file.StartsWith("images/", StringComparison.OrdinalIgnoreCase) && Named(file)))
                yield return path + RgssArchive.Separator + file;
    }

    /// <summary>
    /// True for a picture that decodes, is big enough for a card, isn't one flat color and is solid nearly all over
    /// (a background, not a logo or an overlay).
    /// </summary>
    private static bool Usable(string file)
    {
        using var picture = GameImageLoader.LoadImage(file, null);
        if (picture is not { Width: >= 320, Height: >= 180 } || GameImageLoader.IsBlank(picture)) return false;
        int solid = 0, samples = 0;
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++, samples++)
                if (picture.GetPixel(x * (picture.Width - 1) / 15, y * (picture.Height - 1) / 15).Alpha >= 250) solid++;
        }
        return solid * 100 >= samples * 85;
    }

    /// <summary>The scripts, those named like settings (gui, screens, options, images) first; each set read once, when needed.</summary>
    private IEnumerable<(string Name, string Text)> Scripts()
    {
        _likelyScripts ??= LoadScripts(likely: true);
        foreach (var script in _likelyScripts) yield return script;
        _otherScripts ??= LoadScripts(likely: false);
        foreach (var script in _otherScripts) yield return script;
    }

    private List<(string Name, string Text)> LoadScripts(bool likely)
    {
        static bool Likely(string name)
        {
            string file = Path.GetFileName(name);
            return file.Contains("gui", StringComparison.OrdinalIgnoreCase) || file.Contains("screen", StringComparison.OrdinalIgnoreCase) ||
                   file.Contains("option", StringComparison.OrdinalIgnoreCase) || file.Contains("image", StringComparison.OrdinalIgnoreCase);
        }

        var scripts = new List<(string, string)>();
        long total = 0;
        void Add(string name, Func<byte[]?> read)
        {
            if (Likely(name) != likely || total > MaxScriptBytes || read() is not { } bytes) return;
            total += bytes.Length;
            scripts.Add((name, TextFiles.Decode(bytes)));
        }

        foreach (string file in LooseFiles("", "*.rpy"))
        {
            string relative = Path.GetRelativePath(_game, file).Replace('\\', '/');
            // Translations and caches aren't the game's own settings.
            if (relative.StartsWith("tl/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("cache/", StringComparison.OrdinalIgnoreCase)) continue;
            Add(relative, () => File.ReadAllBytes(file));
        }
        foreach (var (_, archive) in _archives)
            foreach (string name in archive.Names.Where(name => name.EndsWith(".rpy", StringComparison.OrdinalIgnoreCase)).ToList())
                Add(name, () => archive.Read(name));
        return scripts;
    }

    private List<string> LooseFiles(string folder, string pattern)
    {
        try
        {
            string? root = folder.Length == 0 ? _game : Paths.Folder(Path.Combine(_game, folder));
            if (root is null) return new List<string>();
            return Directory.EnumerateFiles(root, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                IgnoreInaccessible = true
            }).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>The lines after <paramref name="start"/> indented deeper than <paramref name="indent"/>: a block's body.</summary>
    private static IEnumerable<string> Block(string text, int start, int indent)
    {
        int lineEnd = text.IndexOf('\n', start);
        if (lineEnd < 0) yield break;
        foreach (string line in text[(lineEnd + 1)..].Split('\n'))
        {
            string body = line.TrimEnd('\r');
            if (body.Trim().Length == 0 || body.TrimStart().StartsWith('#')) continue;
            int depth = body.Length - body.TrimStart(' ', '\t').Length;
            if (depth <= indent) yield break;
            yield return body;
        }
    }

    private static IEnumerable<string> Literals(string text) =>
        LiteralRegex.Matches(text).Select(match => match.Groups["text"].Value).ToList();

    private static string? FirstLiteral(string text) => LiteralRegex.Match(text) is { Success: true } match ? match.Groups["text"].Value : null;

    /// <summary>An image name's words, single-spaced, as Ren'Py compares them.</summary>
    private static string Words(string name) => Regex.Replace(name.Trim(), @"\s+", " ");
}
