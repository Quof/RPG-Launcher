using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RpgLauncher;

internal static class GameScanner
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "data", "img", "audio", "js", "css", "fonts", "effects", "icon", "locales",
        "swiftshader", "save", "www", "node_modules", "bin", "obj", "packages",
        ".git", ".svn", ".vs", "System Volume Information", "$RECYCLE.BIN"
    };

    private static readonly string[] ImageExtensions =
        { ".png", ".png_", ".rpgmvp", ".webp", ".jpg", ".jpeg", ".bmp" };

    private static readonly Regex KeyRegex = new(
        @"_encryptionKey\s*=\s*[\""']([0-9a-fA-F]{32})[\""']",
        RegexOptions.Compiled);

    /// <param name="onFound">Called on the scanning thread for each game found.</param>
    /// <param name="known">A folder's previous entry, reused while its <see cref="Signature"/> matches.</param>
    public static List<GameEntry> Scan(string root, Action? onFound = null, Func<string, GameEntry?>? known = null)
    {
        var found = new List<GameEntry>();
        if (!Directory.Exists(root)) return found;

        ScanDirectory(root, 0, 3, root, found, onFound, known);
        found.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    private static void ScanDirectory(string dir, int depth, int maxDepth, string root, List<GameEntry> found,
        Action? onFound, Func<string, GameEntry?>? known)
    {
        string signature = Signature(dir);
        if (signature.Length > 0 && known?.Invoke(dir) is { } previous && previous.Signature == signature)
        {
            found.Add(Reuse(previous, dir, root));
            onFound?.Invoke();
            return;
        }

        if (TryCreateEntry(dir, root, out var entry))
        {
            entry.Signature = signature;
            found.Add(entry);
            onFound?.Invoke();
            return;
        }

        if (depth >= maxDepth) return;

        string[] children;
        try
        {
            children = Directory.GetDirectories(dir);
        }
        catch
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (string.IsNullOrEmpty(name)) continue;
            if (name.StartsWith('.')) continue;
            if (SkipDirs.Contains(name)) continue;
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            ScanDirectory(child, depth + 1, maxDepth, root, found, onFound, known);
        }
    }

    private static bool TryCreateEntry(string dir, string scanRoot, out GameEntry entry)
    {
        entry = (CreateMvMz(dir, scanRoot) ?? CreateRgss(dir, scanRoot) ?? CreateRm2k(dir, scanRoot) ??
                 CreateWolf(dir, scanRoot))!;
        return entry is not null;
    }

    private static readonly string[] SignatureFiles =
    {
        @"data\System.json", @"www\data\System.json", "Game.ini", "Game.rgss3a", "Game.rgss2a", "Game.rgssad",
        "RPG_RT.ldb", "RPG_RT.ini", "Data.wolf", "Game.exe", "RPG_RT.exe", "nw.exe"
    };

    /// <summary>Sizes and times of a folder's game-defining files; unchanged means the game is unchanged.</summary>
    public static string Signature(string dir)
    {
        var builder = new StringBuilder();
        foreach (var file in SignatureFiles)
        {
            try
            {
                if (Paths.File(Path.Combine(dir, file.Replace('\\', Path.DirectorySeparatorChar))) is not { } path) continue;
                var info = new FileInfo(path);
                if (info.Exists)
                    builder.Append(file).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }
            catch
            {
                // unreadable: treated as missing
            }
        }
        return builder.ToString();
    }

    /// <summary>A copy of an earlier scan's entry for a game whose files haven't changed.</summary>
    private static GameEntry Reuse(GameEntry previous, string dir, string scanRoot) => new()
    {
        Key = KeyFor(dir, scanRoot),
        RootPath = dir,
        ExecutablePath = previous.ExecutablePath,
        DataRoot = previous.DataRoot,
        Title = previous.Title,
        Engine = previous.Engine,
        Locale = previous.Locale,
        Title1Path = previous.Title1Path,
        Title2Path = previous.Title2Path,
        IconPath = previous.IconPath,
        EncryptionKey = previous.EncryptionKey,
        LoadError = previous.LoadError,
        Signature = previous.Signature,
        FolderCreatedUtc = FolderCreatedUtc(dir)
    };

    /// <summary>The game's key: its folder relative to the scanned root.</summary>
    private static string KeyFor(string dir, string scanRoot)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(scanRoot, dir);
        }
        catch
        {
            relative = dir;
        }
        if (string.IsNullOrWhiteSpace(relative) || relative == ".") relative = Path.GetFileName(dir);
        return relative.Replace('\\', '/');
    }

    /// <summary>RPG Maker MV and MZ: data/System.json, at the top or under www.</summary>
    private static GameEntry? CreateMvMz(string dir, string scanRoot)
    {
        var systemPath = Paths.File(Path.Combine(dir, "data", "System.json")) ??
                         Paths.File(Path.Combine(dir, "www", "data", "System.json"));
        if (systemPath is null) return null;
        // the folder holding data, js and img: the game folder or its www
        string dataRoot = Path.GetDirectoryName(Path.GetDirectoryName(systemPath)!)!;

        var system = ReadSystem(systemPath);

        var encryptionKey = !string.IsNullOrWhiteSpace(system.EncryptionKey)
            ? system.EncryptionKey!.Trim()
            : FindKeyInScripts(dir, dataRoot);

        var title = FirstNonEmpty(system.Title, ReadPackageTitle(dir), Path.GetFileName(dir))!;
        var engine = DetectEngine(dir, dataRoot, system.PackageName);

        return new GameEntry
        {
            Key = KeyFor(dir, scanRoot),
            RootPath = dir,
            ExecutablePath = FindExecutable(dir),
            DataRoot = dataRoot,
            Title = title,
            Engine = engine,
            Locale = system.Locale,
            Title1Path = FindTitleImage(dataRoot, system.Title1, isTitle2: false),
            Title2Path = FindTitleImage(dataRoot, system.Title2, isTitle2: true),
            IconPath = FindIcon(dir, dataRoot),
            EncryptionKey = encryptionKey,
            LoadError = system.Error,
            FolderCreatedUtc = FolderCreatedUtc(dir)
        };
    }

    private static readonly string[] PictureExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    /// <summary>
    /// RPG Maker XP, VX and VX Ace: Game.ini with the title, data in Data\ or Game.rgssad/.rgss2a/.rgss3a.
    /// Wolf RPG Editor games may have a Game.ini too, but no archive or Scripts/Library entry.
    /// </summary>
    private static GameEntry? CreateRgss(string dir, string scanRoot)
    {
        if (Paths.File(Path.Combine(dir, "Game.ini")) is not { } ini) return null;

        string? archivePath = null;
        try
        {
            archivePath = Directory.EnumerateFiles(dir, "*.rgss*", Paths.AnyCase)
                .FirstOrDefault(p => Path.GetExtension(p).ToLowerInvariant() is ".rgssad" or ".rgss2a" or ".rgss3a");
        }
        catch
        {
            // unreadable folder
        }

        string marker = (archivePath is not null ? Path.GetExtension(archivePath)
                         : TextFiles.IniValue(ini, "Scripts") ?? TextFiles.IniValue(ini, "Library") ?? "").ToLowerInvariant();
        (string Engine, string SystemFile)? kind =
            marker.EndsWith(".rgss3a") || marker.EndsWith(".rvdata2") || marker.Contains("rgss3") ? ("RPG Maker VX Ace", "System.rvdata2")
            : marker.EndsWith(".rgss2a") || marker.EndsWith(".rvdata") || marker.Contains("rgss2") ? ("RPG Maker VX", "System.rvdata")
            : marker.EndsWith(".rgssad") || marker.EndsWith(".rxdata") || marker.Contains("rgss1") ? ("RPG Maker XP", "System.rxdata")
            : null;
        if (kind is not { } found) return null;

        var archive = archivePath is null ? null : RgssArchive.Open(archivePath);

        // The title picture's name lives in the System data; VX always uses Graphics\System\Title.
        byte[]? system = null;
        string? looseSystem = Paths.File(Path.Combine(dir, "Data", found.SystemFile));
        try
        {
            system = looseSystem is not null ? File.ReadAllBytes(looseSystem) : archive?.Read(@"Data\" + found.SystemFile);
        }
        catch
        {
            // no picture name then
        }

        string? Picture(string folder, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var extension in PictureExtensions)
            {
                string relative = Path.Combine("Graphics", folder, name + extension);
                if (Paths.File(Path.Combine(dir, relative)) is { } loose) return loose;
                if (archive?.Contains(relative) == true) return archivePath + RgssArchive.Separator + relative;
            }
            return null;
        }

        string? title1 = null, title2 = null;
        if (found.Engine == "RPG Maker VX Ace" && system is not null)
        {
            title1 = Picture("Titles1", RgssArchive.SystemString(system, "@title1_name"));
            title2 = Picture("Titles2", RgssArchive.SystemString(system, "@title2_name"));
        }
        else if (found.Engine == "RPG Maker XP" && system is not null)
        {
            title1 = Picture("Titles", RgssArchive.SystemString(system, "@title_name"));
        }
        else if (found.Engine == "RPG Maker VX")
        {
            title1 = Picture("System", "Title");
        }

        return new GameEntry
        {
            Key = KeyFor(dir, scanRoot),
            RootPath = dir,
            ExecutablePath = FindExecutable(dir),
            DataRoot = dir,
            Title = FirstNonEmpty(TextFiles.IniValue(ini, "Title"), Path.GetFileName(dir))!,
            Engine = found.Engine,
            Title1Path = title1,
            Title2Path = title2,
            FolderCreatedUtc = FolderCreatedUtc(dir)
        };
    }

    /// <summary>RPG Maker 2000 and 2003: RPG_RT.exe with the database in RPG_RT.ldb.</summary>
    private static GameEntry? CreateRm2k(string dir, string scanRoot)
    {
        string? exe = Paths.File(Path.Combine(dir, "RPG_RT.exe"));
        string? database = Paths.File(Path.Combine(dir, "RPG_RT.ldb"));
        if (exe is null && database is null) return null;

        var (titleName, is2003) = database is not null ? Rm2kFiles.ReadDatabase(database) : (null, false);
        string? title1 = null;
        if (titleName is not null)
        {
            title1 = new[] { ".png", ".bmp", ".xyz" }
                .Select(extension => Paths.File(Path.Combine(dir, "Title", titleName + extension)))
                .FirstOrDefault(path => path is not null);
        }

        return new GameEntry
        {
            Key = KeyFor(dir, scanRoot),
            RootPath = dir,
            ExecutablePath = exe ?? FindExecutable(dir),
            DataRoot = dir,
            Title = FirstNonEmpty(Paths.File(Path.Combine(dir, "RPG_RT.ini")) is { } rtIni ? TextFiles.IniValue(rtIni, "GameTitle") : null,
                Path.GetFileName(dir))!,
            Engine = is2003 ? "RPG Maker 2003" : "RPG Maker 2000",
            Title1Path = title1,
            FolderCreatedUtc = FolderCreatedUtc(dir)
        };
    }

    /// <summary>
    /// Wolf RPG Editor: Game.exe with its data in Data.wolf, Data\*.wolf or Data\BasicData,
    /// and its MIDI library GuruguruSMF4.dll. The title is packed away, so the folder names it.
    /// </summary>
    private static GameEntry? CreateWolf(string dir, string scanRoot)
    {
        string? data = Paths.Folder(Path.Combine(dir, "Data"));
        bool wolf;
        try
        {
            wolf = Paths.File(Path.Combine(dir, "GuruguruSMF4.dll")) is not null || Paths.File(Path.Combine(dir, "Data.wolf")) is not null ||
                   (data is not null && (Paths.Folder(Path.Combine(data, "BasicData")) is not null ||
                                         Directory.EnumerateFiles(data, "*.wolf", Paths.AnyCase).Any()));
        }
        catch
        {
            return null;
        }
        if (!wolf) return null;

        string? exe = new[] { "Game.exe", "GamePro.exe" }.Select(name => Paths.File(Path.Combine(dir, name)))
            .FirstOrDefault(path => path is not null);
        if (exe is null) return null;

        return new GameEntry
        {
            Key = KeyFor(dir, scanRoot),
            RootPath = dir,
            ExecutablePath = exe,
            DataRoot = dir,
            Title = Path.GetFileName(dir),
            Engine = "Wolf RPG Editor",
            FolderCreatedUtc = FolderCreatedUtc(dir)
        };
    }

    /// <summary>A game folder's creation time, the closest thing to its download date.</summary>
    public static DateTime? FolderCreatedUtc(string dir)
    {
        try
        {
            var created = Directory.GetCreationTimeUtc(dir);
            return created.Year > 1601 ? created : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static (string? Title, string? Title1, string? Title2, string? EncryptionKey,
        string? Locale, string? PackageName, string? Error) ReadSystem(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var span = bytes.AsSpan();
            if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                span = span[3..];

            using var doc = JsonDocument.Parse(span.ToArray());
            var root = doc.RootElement;

            string? GetString(string name)
                => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : null;

            return (
                GetString("gameTitle"),
                GetString("title1Name"),
                GetString("title2Name"),
                GetString("encryptionKey"),
                GetString("locale"),
                null,
                null);
        }
        catch (Exception ex)
        {
            return (null, null, null, null, null, null, ex.Message);
        }
    }

    private static string? ReadPackageTitle(string gameRoot)
    {
        try
        {
            if (Paths.File(Path.Combine(gameRoot, "package.json")) is not { } path) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.TryGetProperty("window", out var window) &&
                window.TryGetProperty("title", out var title) &&
                title.ValueKind == JsonValueKind.String)
            {
                return title.GetString();
            }
        }
        catch
        {
            // ignore malformed package.json
        }
        return null;
    }

    private static string DetectEngine(string gameRoot, string dataRoot, string? packageName)
    {
        bool mz = Paths.File(Path.Combine(dataRoot, "js", "rmmz_core.js")) is not null;
        bool mv = Paths.File(Path.Combine(dataRoot, "js", "rpg_core.js")) is not null;
        if (mz) return "RPG Maker MZ";
        if (mv) return "RPG Maker MV";
        if (string.Equals(packageName, "rmmz-game", StringComparison.OrdinalIgnoreCase)) return "RPG Maker MZ";
        return "RPG Maker";
    }

    private static string? FindExecutable(string gameRoot)
    {
        foreach (var name in new[] { "Game.exe", "game.exe", "nw.exe", "NW.exe" })
        {
            if (Paths.File(Path.Combine(gameRoot, name)) is { } candidate) return candidate;
        }

        try
        {
            return Directory.EnumerateFiles(gameRoot, "*.exe", Paths.AnyCase)
                .Where(p =>
                {
                    var file = Path.GetFileName(p);
                    return !file.Contains("notification", StringComparison.OrdinalIgnoreCase) &&
                           !file.Contains("Launcher", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindTitleImage(string dataRoot, string? name, bool isTitle2)
    {
        if (Paths.Folder(Path.Combine(dataRoot, "img", "titles1")) is not { } dir) return null;

        if (!string.IsNullOrWhiteSpace(name))
        {
            var trimmed = name.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (Paths.File(Path.Combine(dir, trimmed)) is { } direct) return direct;

            foreach (var ext in ImageExtensions)
            {
                if (Paths.File(Path.Combine(dir, trimmed + ext)) is { } candidate) return candidate;
            }
        }

        if (isTitle2) return null;

        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(p => !Path.GetFileNameWithoutExtension(p)
                    .Contains("title2", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindIcon(string gameRoot, string dataRoot)
    {
        foreach (var baseDir in new[] { gameRoot, dataRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in new[] { "icon.png", "icon.ico", "icon.bmp" })
            {
                if (Paths.File(Path.Combine(baseDir, "icon", name)) is { } path) return path;
            }
        }
        return null;
    }

    private static string? FindKeyInScripts(string gameRoot, string dataRoot)
    {
        foreach (var baseDir in new[] { dataRoot, gameRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in new[] { "rmmz_core.js", "rpg_core.js" })
            {
                try
                {
                    if (Paths.File(Path.Combine(baseDir, "js", file)) is not { } path) continue;
                    // Stream line by line so a match near the top doesn't read the whole script.
                    foreach (var line in File.ReadLines(path, Encoding.UTF8))
                    {
                        if (!line.Contains("_encryptionKey", StringComparison.Ordinal)) continue;
                        var match = KeyRegex.Match(line);
                        if (match.Success) return match.Groups[1].Value;
                    }
                }
                catch
                {
                    // ignore unreadable script
                }
            }
        }
        return null;
    }
}
