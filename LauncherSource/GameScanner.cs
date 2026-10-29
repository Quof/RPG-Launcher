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
        RemoveSmallerBuilds(found);
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
                 CreateWolf(dir, scanRoot) ?? CreateRenPy(dir, scanRoot) ?? CreateUnity(dir, scanRoot) ??
                 CreateTyrano(dir, scanRoot))!;
        return entry is not null;
    }

    // "32" or "x86" as a word of its own or with "bit", not inside a number such as v1.32.
    private static readonly Regex BuildRegex32 = new(@"(?<![\d.])32(?!\d)|(?<![A-Za-z\d])x86(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Drops the 32-bit copy of a game shipped as 32- and 64-bit builds side by side ("Windows_32bit" beside
    /// "Windows_64bit", "x86" beside "x64").
    /// </summary>
    private static void RemoveSmallerBuilds(List<GameEntry> found)
    {
        var byFolder = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in found) byFolder.TryAdd(game.RootPath, game);
        found.RemoveAll(game =>
        {
            string name = game.FolderName;
            string twin = BuildRegex32.Replace(name, match => match.Value.Equals("x86", StringComparison.OrdinalIgnoreCase) ? "x64" : "64");
            if (twin == name || Path.GetDirectoryName(game.RootPath) is not { } parent) return false;
            return byFolder.TryGetValue(Path.Combine(parent, twin), out var other) &&
                   string.Equals(other.Engine, game.Engine, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(other.Title, game.Title, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static readonly string[] SignatureFiles =
    {
        @"data\System.json", @"www\data\System.json", "Game.ini", "Game.rgss3a", "Game.rgss2a", "Game.rgssad",
        "RPG_RT.ldb", "RPG_RT.ini", "Data.wolf", "Game.exe", "RPG_RT.exe", "nw.exe",
        @"game\options.rpy", @"game\script_version.txt", @"game\gui.rpy", @"game\archive.rpa", "log.txt", "UnityPlayer.dll",
        "package.json", "package.nw",
        @"data\system\Config.tjs"
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
        string? title1 = FindTitleImage(dataRoot, system.Title1, isTitle2: false);

        // The pictures themselves give the key; System.json's can be a decoy for one hidden in the scripts.
        var encryptionKey = KeyFromPicture(dataRoot, title1) ??
                            (!string.IsNullOrWhiteSpace(system.EncryptionKey) ? system.EncryptionKey!.Trim() : FindKeyInScripts(dir, dataRoot));

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
            Title1Path = title1,
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

    private static readonly string[] AnyPictureExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    private static readonly Regex PlistNameRegex = new(
        @"<key>CFBundleDisplayName</key>\s*<string>(?<name>[^<]+)</string>", RegexOptions.Compiled);

    /// <summary>
    /// Ren'Py: renpy\ (the engine) and game\ (scripts and archives) beside Name.exe, Name.py and Name.sh. The title is
    /// config.name from game\options.rpy, else the name Ren'Py wrote in log.txt or in the Mac app's Info.plist, else
    /// Name split into words.
    /// </summary>
    private static GameEntry? CreateRenPy(string dir, string scanRoot)
    {
        if (Paths.Folder(Path.Combine(dir, "renpy")) is null || Paths.Folder(Path.Combine(dir, "game")) is not { } game) return null;

        // The launcher is named after the game's .py; "Name-32.exe" is the old 32-bit one, used only when it's alone.
        string? name = null, exe = null;
        try
        {
            foreach (var script in Directory.EnumerateFiles(dir, "*.py", Paths.AnyCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                string stem = Path.GetFileNameWithoutExtension(script);
                name ??= stem;
                if (Paths.File(Path.Combine(dir, stem + ".exe")) is not { } launcher) continue;
                (name, exe) = (stem, launcher);
                break;
            }
            exe ??= Directory.EnumerateFiles(dir, "*.exe", Paths.AnyCase).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        catch
        {
            // unreadable folder
        }
        // A Linux or Mac download has no exe; on Linux its Name.sh starts it.
        if (exe is null && name is not null) exe = Paths.File(Path.Combine(dir, name + ".sh"));
        name ??= exe is null ? null : Path.GetFileNameWithoutExtension(exe);

        // Scripts and pictures may be loose or packed in the game's .rpa archives.
        var files = new RenPyFiles(game);
        string? title = RenPyName(files, dir, name);
        var (picture, overlay) = SafelyPair(files.MenuPicture);
        return new GameEntry
        {
            Key = KeyFor(dir, scanRoot),
            RootPath = dir,
            ExecutablePath = exe,
            DataRoot = game,
            Title = FirstNonEmpty(title, name is null ? null : SplitWords(name), Path.GetFileName(dir))!,
            Engine = "Ren'Py",
            Title1Path = picture,
            Title2Path = overlay,
            IconPath = Safely(files.Icon),
            FolderCreatedUtc = FolderCreatedUtc(dir)
        };
    }

    private static (string?, string?) SafelyPair(Func<(string?, string?)> find)
    {
        try
        {
            return find();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, null);
        }
    }

    /// <summary>A picture lookup's answer, or null when the game's files can't be read.</summary>
    private static string? Safely(Func<string?> find)
    {
        try
        {
            return find();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string? RenPyName(RenPyFiles files, string dir, string? name)
    {
        try
        {
            if (files.ConfigName() is { Length: > 0 } defined) return defined;

            // Newer Ren'Py versions log the game's name and version after their own version, then "Built at …".
            if (Paths.File(Path.Combine(dir, "log.txt")) is { } log)
            {
                var lines = File.ReadLines(log).Take(40).Select(line => line.Trim()).ToList();
                int version = lines.FindIndex(line => line.StartsWith("Ren'Py ", StringComparison.Ordinal));
                if (version >= 0)
                {
                    var after = lines.Skip(version + 1).SkipWhile(line => line.Length == 0).Take(4).ToList();
                    if (after.Count >= 3 && after.Skip(1).Take(3).Any(line => line.StartsWith("Built at ", StringComparison.Ordinal)))
                        return after[0];
                }
            }

            if (name is not null && Paths.File(Path.Combine(dir, name + ".app", "Contents", "Info.plist")) is { } plist &&
                PlistNameRegex.Match(File.ReadAllText(plist)) is { Success: true } bundle)
                return System.Net.WebUtility.HtmlDecode(bundle.Groups["name"].Value);
        }
        catch
        {
            // unreadable: the name then comes from the launcher's file name
        }
        return null;
    }

    /// <summary>"SummertimeSaga" or "hero_party-must_fall" as words: "Summertime Saga", "hero party must fall".</summary>
    private static string SplitWords(string name)
    {
        string spaced = Regex.Replace(name.Replace('_', ' ').Replace('-', ' '), @"(?<=\p{Ll})(?=\p{Lu})|(?<=\p{L})(?=\d)", " ");
        return Regex.Replace(spaced, @"\s+", " ").Trim();
    }

    private static readonly string[] UnityDataFiles = { "globalgamemanagers", "data.unity3d", "mainData", "level0", "resources.assets" };

    /// <summary>
    /// Unity: Name.exe (or a Linux Name.x86_64) beside Name_Data, which holds globalgamemanagers, data.unity3d,
    /// mainData or level0. The title is the product name in Name_Data\app.info (company, then product).
    /// </summary>
    private static GameEntry? CreateUnity(string dir, string scanRoot)
    {
        string[] dataFolders;
        try
        {
            dataFolders = Directory.GetDirectories(dir, "*_Data", Paths.AnyCase);
        }
        catch
        {
            return null;
        }
        string[] extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".x86_64", ".x86" } : new[] { ".x86_64", ".x86", ".exe" };
        foreach (var data in dataFolders.Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(data)[..^"_Data".Length];
            if (name.Length == 0 || !UnityDataFiles.Any(file => Paths.File(Path.Combine(data, file)) is not null)) continue;
            string? exe = extensions.Select(extension => Paths.File(Path.Combine(dir, name + extension))).FirstOrDefault(path => path is not null);
            if (exe is null) continue;

            string? product = null;
            try
            {
                if (Paths.File(Path.Combine(data, "app.info")) is { } info)
                    product = TextFiles.Decode(File.ReadAllBytes(info)).Split('\n').ElementAtOrDefault(1)?.Trim();
            }
            catch
            {
                // the exe names it then
            }
            return new GameEntry
            {
                Key = KeyFor(dir, scanRoot),
                RootPath = dir,
                ExecutablePath = exe,
                DataRoot = data,
                Title = FirstNonEmpty(product, name)!,
                Engine = "Unity",
                // Unity packs its pictures away; the exe's icon, when it's the game's own, stands in.
                IconPath = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && ExeIcon.HasOwn(exe) ? ExeIcon.PathFor(exe) : null,
                FolderCreatedUtc = FolderCreatedUtc(dir)
            };
        }
        return null;
    }

    /// <summary>
    /// TyranoScript and TyranoBuilder: an NW.js (or Electron) app of index.html, tyrano\ (the engine) and data\
    /// (scenario, system\Config.tjs, pictures). It is loose beside the exe, in package.nw or resources\app(.asar), or
    /// zipped onto the end of the exe. The title is System.title in Config.tjs, else the window title in package.json.
    /// </summary>
    private static GameEntry? CreateTyrano(string dir, string scanRoot)
    {
        var (files, exe) = FindTyranoApp(dir);
        if (files is null) return null;

        string? Setting(string file, Func<string, string?> read)
        {
            try
            {
                return files.Read(file) is { } bytes ? read(TextFiles.Decode(bytes)) : null;
            }
            catch
            {
                return null;
            }
        }
        string? Usable(string? title) => string.IsNullOrWhiteSpace(title) || GameTitles.IsGeneric(title) ? null : title;

        string? title = Usable(Setting("data/system/Config.tjs", text => TyranoSetting(text, "System.title"))) ??
                        Usable(Setting("package.json", PackageWindowTitle)) ??
                        Usable(exe is null ? null : Path.GetFileNameWithoutExtension(exe));
        return new GameEntry
        {
            Key = KeyFor(dir, scanRoot),
            RootPath = dir,
            ExecutablePath = exe,
            DataRoot = dir,
            Title = FirstNonEmpty(title, Path.GetFileName(dir))!,
            Engine = "TyranoScript",
            Title1Path = TyranoTitlePicture(files),
            FolderCreatedUtc = FolderCreatedUtc(dir)
        };
    }

    /// <summary>A Tyrano game's files and the exe that runs it, or nulls when the folder holds none.</summary>
    private static (AppFiles? Files, string? Exe) FindTyranoApp(string dir)
    {
        static bool IsTyrano(AppFiles files) =>
            files.HasFolder("tyrano") && (files.HasFile("data/system/Config.tjs") || files.HasFolder("data/scenario"));

        foreach (var folder in new[] { dir, Path.Combine(dir, "package.nw"), Path.Combine(dir, "resources", "app") })
        {
            if (Paths.Folder(folder) is { } found && AppFiles.InFolder(found) is var loose && IsTyrano(loose))
                return (loose, AppExecutable(dir));
        }
        foreach (var package in new[] { Path.Combine(dir, "package.nw"), Path.Combine(dir, "resources", "app.asar") })
        {
            if (Paths.File(package) is { } found && AppArchive.Open(found) is { } archive && AppFiles.InArchive(found, archive) is var packed &&
                IsTyrano(packed))
                return (packed, AppExecutable(dir));
        }

        // Zipped onto an NW.js exe: only folders with NW.js's own files are worth opening exes for.
        if (Paths.File(Path.Combine(dir, "nw.pak")) is null && Paths.File(Path.Combine(dir, "nw.dll")) is null) return (null, null);
        foreach (var exe in Executables(dir).OrderByDescending(path => new FileInfo(path).Length))
        {
            if (AppArchive.Open(exe) is { } archive && AppFiles.InArchive(exe, archive) is var inside && IsTyrano(inside))
                return (inside, exe);
        }
        return (null, null);
    }

    private static readonly string[] HelperExeWords =
        { "notification_helper", "crashpad", "nwjc", "chromedriver", "payload", "unins", "setup", "UnityCrashHandler" };

    private static IEnumerable<string> Executables(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.exe", Paths.AnyCase)
                .Where(path => !HelperExeWords.Any(word => Path.GetFileName(path).Contains(word, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The NW.js or Electron program of an HTML game: the biggest exe that isn't a helper.</summary>
    private static string? AppExecutable(string dir) =>
        Executables(dir).OrderByDescending(path => new FileInfo(path).Length).FirstOrDefault();

    /// <summary>A setting in Config.tjs, read as TyranoScript does: lines starting with ';' are "key = value".</summary>
    private static string? TyranoSetting(string text, string key)
    {
        string? value = null;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith(';')) continue;
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Replace(";", "").Replace("\"", "");
            int equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim() == key) value = line[(equals + 1)..].Trim();
        }
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? PackageWindowTitle(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("window", out var window) &&
               window.ValueKind == JsonValueKind.Object && window.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
            ? title.GetString()
            : null;
    }

    private static readonly Regex TyranoBgRegex = new(
        @"[\[@]\s*bg\s[^\]\r\n]*?storage\s*=\s*[""']?(?<file>[^""'\s\]]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The title screen's background: the first [bg storage=…] in the title scenario (title.ks, or another title*.ks),
    /// else a picture in data\bgimage named "title", else one with "title" in its name.
    /// </summary>
    private static string? TyranoTitlePicture(AppFiles files)
    {
        try
        {
            var scenarios = files.FilesIn("data/scenario")
                .Where(name => Path.GetFileName(name).StartsWith("title", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".ks", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => Path.GetFileName(name).Equals("title.ks", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase);
            foreach (var scenario in scenarios)
            {
                if (files.Read(scenario) is not { } bytes) continue;
                foreach (Match match in TyranoBgRegex.Matches(TextFiles.Decode(bytes)))
                {
                    string file = match.Groups["file"].Value.Replace('\\', '/').TrimStart('.', '/');
                    if (file.Contains(':') || file.Contains("&")) continue;
                    if (files.HasFile("data/bgimage/" + file)) return files.PathOf("data/bgimage/" + file);
                }
            }

            var pictures = files.FilesIn("data/bgimage")
                .Where(name => AnyPictureExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
            string? named = pictures.FirstOrDefault(name => Path.GetFileNameWithoutExtension(name).Equals("title", StringComparison.OrdinalIgnoreCase)) ??
                            pictures.FirstOrDefault(name => Path.GetFileName(name).Contains("title", StringComparison.OrdinalIgnoreCase));
            return named is null ? null : files.PathOf(named);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>An HTML game's files by '/'-separated name, loose in a folder or inside a package.</summary>
    private sealed class AppFiles
    {
        private readonly string _root;
        private readonly AppArchive? _archive;

        private AppFiles(string root, AppArchive? archive)
        {
            _root = root;
            _archive = archive;
        }

        public static AppFiles InFolder(string folder) => new(folder, null);

        public static AppFiles InArchive(string file, AppArchive archive) => new(file, archive);

        public bool HasFolder(string name) => _archive?.HasFolder(name) ?? Paths.Folder(Local(name)) is not null;

        public bool HasFile(string name) => _archive?.Contains(name) ?? Paths.File(Local(name)) is not null;

        public byte[]? Read(string name)
        {
            if (_archive is not null) return _archive.Read(name);
            return Paths.File(Local(name)) is { } path ? File.ReadAllBytes(path) : null;
        }

        /// <summary>The files directly inside a folder, by name.</summary>
        public IEnumerable<string> FilesIn(string folder)
        {
            if (_archive is not null)
                return _archive.Names.Where(name => name.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) &&
                                                    name.IndexOf('/', folder.Length + 1) < 0);
            if (Paths.Folder(Local(folder)) is not { } path) return Array.Empty<string>();
            return Directory.EnumerateFiles(path).Select(file => folder + "/" + Path.GetFileName(file)).ToList();
        }

        /// <summary>Where the launcher loads a file from: its path, or "package::name" inside a package.</summary>
        public string PathOf(string name) =>
            _archive is null ? Paths.File(Local(name)) ?? Local(name) : _root + RgssArchive.Separator + name;

        private string Local(string name) => Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));
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

    // The first 16 bytes of every PNG: its signature, then the length and name of its IHDR chunk.
    private static readonly byte[] PngStart =
        { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 };

    /// <summary>
    /// The key that decrypts an MV or MZ game's pictures, read back from one of them: after a 16-byte "RPGMV" header,
    /// an encrypted picture is a PNG whose first 16 bytes are XORed with the key. Null when no picture is encrypted.
    /// </summary>
    private static string? KeyFromPicture(string dataRoot, string? title1)
    {
        static bool Encrypted(string path) =>
            path.EndsWith(".rpgmvp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".png_", StringComparison.OrdinalIgnoreCase);

        var samples = new List<string>();
        if (title1 is not null && Encrypted(title1)) samples.Add(title1);
        try
        {
            if (Paths.Folder(Path.Combine(dataRoot, "img", "system")) is { } system)
                samples.AddRange(Directory.EnumerateFiles(system).Where(Encrypted).Order(StringComparer.OrdinalIgnoreCase).Take(3));
        }
        catch
        {
            // unreadable folder
        }

        foreach (var sample in samples)
        {
            try
            {
                var head = new byte[32];
                using (var stream = new FileStream(sample, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    stream.ReadExactly(head);
                if (head[0] != 'R' || head[1] != 'P' || head[2] != 'G' || head[3] != 'M' || head[4] != 'V') continue;
                var key = new byte[16];
                for (int i = 0; i < 16; i++) key[i] = (byte)(head[16 + i] ^ PngStart[i]);
                return Convert.ToHexString(key).ToLowerInvariant();
            }
            catch
            {
                // too short or unreadable: try the next
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
