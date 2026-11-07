using System.Diagnostics;
using Avalonia.Platform.Storage;
using static RpgLauncher.VipText;

namespace RpgLauncher;

/// <summary>
/// VIPRPG mode: a matsuri-site styled archive of VIPRPG games, drawn like an RPG Maker 2000 screen. Lists every
/// work from the VIPRPG wiki by matsuri or author, marks the ones on this PC, and starts them. Nothing here talks
/// to DLsite.
/// </summary>
internal sealed class VipView : PixelCanvas
{
    private enum Pane { Commands, Side, Rows }

    private sealed record SideItem(string Label, int Owned, int Total, VipMatsuri? Matsuri, string? Author, bool Strays, bool Server = false,
        string? Area = null, string? Filter = null, int Wodicon = 0);

    private sealed record Row(VipWork? Work, GameEntry? Local, string No, string Title, string Author, string? Zip = null,
        VipServerGame? Remote = null, string? Missing = null, VipWolfEntry? Wolf = null)
    {
        /// <summary>A WOLF RPG contest entry no copy of was found: not on the server and not on this PC.</summary>
        public bool Lost => Wolf is not null && Remote is null && Local is null;
    }

    /// <summary>The download server: the Worker in front of the private R2 bucket (viprpg-host/worker), where manifest.json is.</summary>
    private const string ServerAddress = "https://dl.viprpg-archive.com";

    /// <summary>
    /// The index website (viprpg-host/site), which serves the same manifest.json as static files, free and without
    /// request limits: the launcher reads its list there, so the Worker only answers downloads.
    /// </summary>
    private const string ListAddress = "https://viprpg-archive.com";

    private sealed record Command(string LabelJa, string LabelEn, string HintJa, string HintEn, Action Run, VipListMode? Mode = null)
    {
        public string Label => T(LabelJa, LabelEn);
        public string Hint => T(HintJa, HintEn);
    }

    /// <summary>Asks the window to switch modes (the "Mode: VIPRPG" command).</summary>
    public event Action<LauncherMode>? ModeRequested;

    private readonly LauncherStore _store;
    private readonly string _cachePath;

    /// <summary>Where downloads go unless 設定 → ダウンロード先 says otherwise: a games folder beside the launcher.</summary>
    private readonly string _defaultDownloads;
    private readonly Action _save;
    private readonly Func<IEnumerable<string>> _runtimeSearch;
    private readonly VipCatalog _catalog = VipCatalog.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly List<VipHot> _hot = new();
    private readonly Dictionary<string, Bitmap?> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _titlesLoading = new(StringComparer.OrdinalIgnoreCase);

    // Sizes on disk of game folders and zips (-1 when unreadable), added up in the background the first time one is
    // shown; a rescan starts over.
    private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sizesLoading = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Command> _commands;

    // Rescan, add folder and settings: buttons at the top right, always there.
    private readonly List<Command> _tools;
    private readonly List<Rect> _rowAreas = new();
    private readonly List<Rect> _sideAreas = new();

    /// <summary>For each game line in <see cref="_rowAreas"/>, where its words were drawn.</summary>
    private readonly List<List<Rect>> _rowWords = new();

    /// <summary>For each line in <see cref="_sideAreas"/>, where its words were drawn.</summary>
    private readonly List<List<Rect>> _sideWords = new();

    // The search box over the works window: a real text box, so Japanese input works.
    private readonly TextBox _search = new();

    // The selected work's picture, drawn over the canvas at the screen's own resolution rather than the canvas's.
    private readonly Image _picture = new()
    {
        Stretch = Stretch.Fill, IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private Rect _pictureRect;
    private Bitmap? _pictureBitmap;
    private readonly Panel _host = new();
    private Rect _searchRect;
    private string _query = "";

    // The author list's own search, sharing the one text box: which list the box is searching now, and a guard
    // while its text is swapped between the two.
    private string _sideQuery = "";
    private bool _searchSide, _syncingSearch;

    // The scroll bars beside the two lists, and the one being dragged by its thumb.
    private Rect _sideBar, _rowsBar;
    private Pane? _dragging;
    private double _dragOffset;

    // Each author's first game, shown while the pointer is over the author.
    private readonly Dictionary<string, Row?> _authorFirst = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The screen to show: the canvas with the search box over it.</summary>
    public Control Host => _host;

    private List<GameEntry> _games = new();
    private List<GameEntry> _strays = new();
    private List<string> _strayZips = new();
    private bool _extracting;
    private List<SideItem> _side = new();
    private List<Row> _rows = new();

    /// <summary>Completed games and all games in the list shown, for the "Completed: X/Y" in its heading.</summary>
    private (int Done, int Total) _completedCount;
    // The left window shows one of three pages: the list modes, a mode's matsuri/authors/filters, or the works.
    private Pane _focus = Pane.Commands;
    private int _command, _sideIndex, _sideTop, _rowIndex, _rowTop;
    private int _sideVisible = 1, _rowsVisible = 1;
    private bool _scanning, _started, _loadingCache;

    // True after a key press, false once the mouse moves: the matsuri list shows its cursor only to the keyboard.
    private bool _keys;

    // Games picked with Ctrl/Shift-click or Shift+↑↓, by key, for the right-click menu that changes them all at once;
    // the anchor is where a Shift range starts (null: the cursor's game).
    private readonly HashSet<string> _picked = new(StringComparer.OrdinalIgnoreCase);
    private int? _pickAnchor;

    // The modifier keys held for the click being handled, for the clicked areas that care (a game line).
    private KeyModifiers _clickModifiers;
    private int _tick;
    private string _say = "", _sayEn = "";
    private VipOverlay? _overlay;
    private Point _mouse = new(-100, -100);

    // The pointer in this control's own coordinates (not the canvas's), for placing dialogs clear of it.
    private Point _pointerView;

    // The download server's list: catalog works by "matsuri/no", and the games the catalog doesn't list.
    private readonly string _serverCachePath;
    private Dictionary<string, VipServerGame> _remoteByWork = new(StringComparer.OrdinalIgnoreCase);
    private List<VipServerGame> _remoteExtras = new();
    private Dictionary<string, GameEntry> _remoteLocal = new(StringComparer.OrdinalIgnoreCase);

    // Server games downloaded but still zipped inside their folder, by server path: the zip.
    private Dictionary<string, string> _remoteZip = new(StringComparer.OrdinalIgnoreCase);
    private bool _serverLoading;
    private List<VipServerGame> _archiveGames = new();
    private VipManifest? _serverManifest;
    private VipServerGame? _downloading;
    private long _downloaded;
    private CancellationTokenSource? _downloadCancel;

    // Download passwords given for getuploader uploaders this session, by uploader.
    private readonly Dictionary<string, string> _uploaderPasswords = new(StringComparer.OrdinalIgnoreCase);

    public VipView(LauncherStore store, string root, Action save, Func<IEnumerable<string>> runtimeSearch)
    {
        _store = store;
        _cachePath = Path.Combine(root, "launcher-vip-library.json");
        _defaultDownloads = Path.Combine(root, "games");
        _serverCachePath = Path.Combine(root, "launcher-vip-server.json");
        _save = save;
        _runtimeSearch = runtimeSearch;
        _commands = new List<Command>
        {
            new("手持ち", "On This PC", "このPCにある作品だけ。", "Only the games on this PC.", () => SetList(VipListMode.Owned), VipListMode.Owned),
            new("全作品", "All Games", "全部の作品を名前順に。お気に入りやグループで絞れるよ。", "Every game by title; narrow it to favorites or a group. Right-click a game for more.",
                () => SetList(VipListMode.All), VipListMode.All),
            new("祭り別", "By Festival", "祭りごとに作品を並べるよ。", "Works by festival.", () => SetList(VipListMode.Matsuri), VipListMode.Matsuri),
            new("作者別", "By Author", "作者ごとに作品を並べるよ。", "Works by author.", () => SetList(VipListMode.Author), VipListMode.Author),
            new("シリーズ別", "Series", "シリーズごとに作品を発表順に並べるよ。", "Series of works, each in release order.",
                () => SetList(VipListMode.Series), VipListMode.Series),
            new("保管庫", "Thread Storage", "スレやwikiの保管庫、PCだけにある作品。", "Games from the thread and wiki storage uploaders, and games only on this PC.",
                () => SetList(VipListMode.Storage), VipListMode.Storage),
            new("プレイリスト", "Playlists", "自分で作った作品リスト。コードで共有もできるよ。", "Your own lists of games; share them as a code.",
                () => SetList(VipListMode.Playlist), VipListMode.Playlist)
        };
        _tools = new List<Command>
        {
            new("再スキャン", "Rescan", "フォルダを調べなおすよ。(F5)", "Look through the folders again. (F5)", () => _ = ScanAsync()),
            new("フォルダ追加", "Add Folder", "ゲームが入っているフォルダを選んでね。", "Add a folder that holds VIPRPG games.", () => _ = AddFolderAsync()),
            new("設定", "Settings", "表示と操作、起動方法、フォルダ、モード切り替え。(S)", "Display and controls, launch options, folders, and the switch back to HRPG mode. (S)", ShowSettings)
        };
        // The WOLF RPG list was withdrawn: a launcher left on it opens on the festivals.
        if (Data.List == VipListMode.Wolf)
        {
            Data.List = VipListMode.Matsuri;
            Data.Selected = null;
        }
        _command = Math.Max(0, _commands.FindIndex(c => c.Mode == Data.List));
        _host.Children.Add(this);
        _host.Children.Add(_picture);
        _host.Children.Add(_search);
        _search.HorizontalAlignment = HorizontalAlignment.Left;
        _search.VerticalAlignment = VerticalAlignment.Top;
        _search.Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        _search.Foreground = Brushes.White;
        _search.BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255));
        _search.BorderThickness = new Thickness(1);
        _search.CornerRadius = new CornerRadius(0);
        _search.Padding = new Thickness(3, 0);
        _search.MinHeight = 0;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) =>
        {
            if (_syncingSearch) return;
            if (_searchSide)
            {
                _sideQuery = (_search.Text ?? "").Trim();
                RebuildSide();
                return;
            }
            _query = (_search.Text ?? "").Trim();
            _rowIndex = _rowTop = 0;
            RebuildRows();
        };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _search.Text = "";
                Focus();
                e.Handled = true;
            }
            else if (e.Key is Key.Enter or Key.Down)
            {
                if (!_searchSide) _focus = Pane.Rows;
                Focus();
                e.Handled = true;
            }
        };
        _timer.Tick += (_, _) =>
        {
            _tick++;
            Redraw();
        };
        Say("VIPRPGランチャーへようこそ！ 一覧の種類を選んで、作品を遊ぼう。",
            "Welcome! Pick a list, then a festival or author, then a game. Enter plays, Esc goes back.");
        ApplyLanguage();
    }

    private VipData Data => _store.Data.Vip;

    /// <summary>Shows the launcher's words in the language chosen in 設定 → 言語 (side-list labels are rebuilt).</summary>
    public void ApplyLanguage()
    {
        VipText.English = Data.English;
        _search.Watermark = T("検索 (Ctrl+F)", "Search (Ctrl+F)");
        RebuildSide();
    }

    /// <summary>Starts the animation and, the first time, the folder scan.</summary>
    public void Start()
    {
        _timer.Start();
        Focus();
        if (_started) return;
        _started = true;
        _ = LoadAsync();
    }

    /// <summary>Shows the last scan at once, then looks for changes.</summary>
    private async Task LoadAsync()
    {
        if (VipDefaults.Seed(Data) | VipMoved.Apply(Data)) _save();
        _loadingCache = true;
        try
        {
            // The uploader archive's games (built into the launcher), then the download server's list: the newer of the
            // copy saved from the server and the one built into the launcher, so the lists show even offline or
            // before the server has ever answered.
            _archiveGames = await Task.Run(() => VipUploader.Load(_catalog).ToGames(_catalog));
            var saved = await Task.Run(() => VipServer.LoadCached(_serverCachePath));
            var builtIn = await Task.Run(VipServer.LoadBuiltIn);
            var manifest = saved is not null && VipServer.Replaces(saved, builtIn) ? saved : builtIn ?? saved;
            if (manifest is not null) SetManifest(manifest);
            else ApplyRemote();
            var folders = Data.Directories.ToList();
            var cached = await Task.Run(() => LibraryCache.Load(_cachePath, "", folders));
            if (cached is { Count: > 0 })
            {
                var zips = await Task.Run(() => FindZips(folders));
                Show(cached, zips);
            }
        }
        finally
        {
            _loadingCache = false;
        }
        await ScanAsync();
        await RefreshServerAsync(quiet: true);
        // Development aid: RPGLAUNCHER_PLAYLIST_TEXT=<file> makes a playlist from a title list, as ＋ テキストから作る… does.
        if (Environment.GetEnvironmentVariable("RPGLAUNCHER_PLAYLIST_TEXT") is { Length: > 0 } text) await PlaylistFromTextAsync(text);
    }

    private void Show(List<GameEntry> games, List<string> zips)
    {
        _games = games;
        _strays = _catalog.Match(games, zips);
        ApplyDownloads(zips);
        // Zips no work or download claimed and nobody extracted yet: listed with the other games.
        var claimed = new HashSet<string>(_catalog.Games.Select(w => w.LocalZip).OfType<string>().Concat(_remoteZip.Values),
            StringComparer.OrdinalIgnoreCase);
        _strayZips = zips.Where(zip => !claimed.Contains(zip) && !VipZip.LooksExtracted(zip)).ToList();
        RebuildSide();
    }

    /// <summary>
    /// Games downloaded from the server are matched by the record kept when they were downloaded, as their folders
    /// are named however the zip had them.
    /// </summary>
    private void ApplyDownloads(List<string> zips)
    {
        _remoteLocal = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        _remoteZip = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Data.Downloaded.Count == 0) return;
        var works = new Dictionary<string, VipWork>(StringComparer.OrdinalIgnoreCase);
        foreach (var work in _catalog.Games)
        {
            if (VipServer.WorkKey(work.Matsuri, work.No, work.Title) is { } key) works.TryAdd(key, work);
        }
        // One game per download: the one nearest the top of its folder. A collection's other games, and a second
        // download of the same game, stay with the other games on this PC.
        var downloaded = _games.Select(game => (Game: game, Key: DownloadKey(game.RootPath)))
            .Where(entry => entry.Key is not null)
            .GroupBy(entry => entry.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Key: group.Key, Game: group
                .OrderBy(entry => entry.Game.RootPath.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(entry => entry.Game.RootPath.Length).First().Game));
        foreach (var (key, game) in downloaded)
        {
            if (works.TryGetValue(key, out var work))
            {
                if (ReferenceEquals(work.Local, game)) continue;
                // The download wins over a title match; whatever the work was matched to goes back to the others.
                if (work.Local is { } previous && !_strays.Contains(previous)) _strays.Add(previous);
                foreach (var other in _catalog.Games.Where(w => ReferenceEquals(w.Local, game))) other.Local = null;
                work.Local = game;
                work.LocalZip = null;
            }
            else
            {
                _remoteLocal[key] = game;
            }
            _strays.Remove(game);
        }
        // A zip inside a downloaded folder with no game unpacked from it yet (an upload zipped twice, from before the
        // launcher unpacked those) is that download's: its line shows Z, and 展開 unpacks it.
        foreach (string zip in zips)
        {
            if (Path.GetDirectoryName(zip) is not { } folder || DownloadKey(folder) is not { } key) continue;
            if (works.TryGetValue(key, out var work))
            {
                if (work.Local is null) work.LocalZip ??= zip;
            }
            else if (!_remoteLocal.ContainsKey(key)) _remoteZip.TryAdd(key, zip);
        }
    }

    /// <summary>The download server's address; RPGLAUNCHER_VIP_SERVER points it elsewhere for testing.</summary>
    private static string? Server => VipServer.Normalize(Environment.GetEnvironmentVariable("RPGLAUNCHER_VIP_SERVER") ?? ServerAddress);

    private void SetManifest(VipManifest manifest)
    {
        // The WOLF RPG Editor contest games stay in the bucket for preservation but aren't offered for download,
        // even when an older server list still names them.
        manifest.Games.RemoveAll(VipServer.Withheld);
        _serverManifest = manifest;
        ApplyRemote();
    }

    /// <summary>Downloads by work and the rest, from the server (the R2 bucket) first and the uploader archive after.</summary>
    private void ApplyRemote()
    {
        var byWork = new Dictionary<string, VipServerGame>(StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(_catalog.Games.Select(w => VipServer.WorkKey(w.Matsuri, w.No, w.Title)).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);
        var extras = new List<VipServerGame>();
        // The server's own copies of the uploaders' unique games (its その他 section) stand in for the uploader
        // archive's list: every game that list has is either there or a copy of a matsuri or 保管庫 game.
        bool hostedOthers = _serverManifest?.Games.Any(g => g.Section == OtherSection) == true;
        foreach (var game in (_serverManifest?.Games ?? new List<VipServerGame>()).Concat(_archiveGames))
        {
            if (game.WorkKey is { } key && known.Contains(key)) byWork.TryAdd(key, game);
            else if (!(hostedOthers && game.Source == "uploader")) extras.Add(game);
        }
        // Works neither the archive nor the server has, but whose matsuri link is still one the launcher can fetch.
        foreach (var work in _catalog.Games)
        {
            if (VipServer.WorkKey(work.Matsuri, work.No, work.Title) is not { } key ||
                work.Downloads?.FirstOrDefault(VipOriginal.CanFetch) is not { } link) continue;
            // The work's own link beats a file the archive couldn't keep, which may have been deleted since.
            if (byWork.TryGetValue(key, out var existing) && existing.Source != "live") continue;
            byWork[key] = new VipServerGame
            {
                Source = "original", Url = link, Matsuri = work.Matsuri, No = work.No, Title = work.Title, Author = work.Author,
                Year = work.Event?.Year, Engine = work.Engine, Path = $"original/{work.Matsuri}/{work.No}"
            };
        }
        _remoteByWork = byWork;
        // An uploader with only a stray file or two is patches and leftovers, not a collection: left out everywhere.
        // The server's own games (no area) and the hosted 保管庫 and contests, however few, always stay.
        _remoteExtras = extras.GroupBy(g => g.Area)
            .Where(area => area.Key is null || NumberedArea(area.Key) || area.Count() >= StorageMinimum)
            .SelectMany(area => area)
            .OrderBy(g => g.DisplayTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
        _wolfGames = new Dictionary<(int, int), VipServerGame>();
        foreach (var game in _remoteExtras)
        {
            if (VipWolf.EntryOf(game) is { } entry) _wolfGames.TryAdd(entry, game);
        }
        RebuildSide();
    }

    // The server's WOLF RPG contest games, by contest and entry number.
    private Dictionary<(int Contest, int No), VipServerGame> _wolfGames = new();

    /// <summary>Fetches the server's list; quiet keeps failures out of the message window when a cached list shows.</summary>
    private async Task RefreshServerAsync(bool quiet)
    {
        if (_serverLoading) return;
        if (Server is not { } server)
        {
            if (!quiet) Say("ダウンロードはまだ準備中だよ。", "Downloads aren't available in this version of the launcher.");
            return;
        }
        _serverLoading = true;
        try
        {
            VipManifest manifest;
            // A test server given by RPGLAUNCHER_VIP_SERVER has its own list; otherwise the index site's, then the Worker's.
            string? list = Environment.GetEnvironmentVariable("RPGLAUNCHER_VIP_SERVER") is null ? VipServer.Normalize(ListAddress) : null;
            try
            {
                manifest = await VipServer.FetchAsync(list ?? server, _serverCachePath, CancellationToken.None);
            }
            catch (Exception ex) when (list is not null && ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
            {
                Diagnostics.Log("VIPRPG list not fetched from the index site, trying the download server: " + ex.Message);
                manifest = await VipServer.FetchAsync(server, _serverCachePath, CancellationToken.None);
            }
            // An older list than the one showing (a launcher built after the last upload) leaves the lists as they are.
            if (VipServer.Replaces(manifest, _serverManifest)) SetManifest(manifest);
            if (!quiet)
                Say($"サーバーにつながった！ {manifest.Games.Count}本の作品をダウンロードできるよ。",
                    $"Connected: {manifest.Games.Count} games can be downloaded (marked ↓).");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("VIPRPG server list not fetched: " + ex);
            if (!quiet)
            {
                var (japanese, english) = VipServer.Explain(ex);
                Say(japanese, english);
            }
        }
        finally
        {
            _serverLoading = false;
        }
    }

    /// <summary>The download record for a game folder, or a folder inside a downloaded one.</summary>
    private string? DownloadKey(string root)
    {
        string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var (folder, key) in Data.Downloaded)
        {
            string top = folder.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, top, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return null;
    }

    public void Stop() => _timer.Stop();

    /// <summary>True while the folders are being scanned.</summary>
    public bool Busy => _scanning || _loadingCache;

    // ---- data ----

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        Say("フォルダを調べているよ…", "Looking through the folders…");
        var folders = Data.Directories.ToList();
        // Unchanged games are reused from the last scan without reading their files again.
        var known = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in _games) known.TryAdd(Path.GetFullPath(game.RootPath), game);
        try
        {
            // A running count, as the first scan of a big folder takes a while.
            int found = 0;
            void Found()
            {
                int count = Interlocked.Increment(ref found);
                if (count % 10 == 0)
                    Dispatcher.UIThread.Post(() => Say($"フォルダを調べているよ… {count}本", $"Looking through the folders… {count} games so far"));
            }
            var downloads = Data.Downloaded.Keys.ToList();
            var (games, zips) = await Task.Run(() => Scan(folders, downloads, known, Found));
            Show(games, zips);
            _sizes.Clear();
            _sizesLoading.Clear();
            string json = LibraryCache.Serialize("", folders, games);
            _ = Task.Run(() => LibraryCache.Write(_cachePath, json));
            int owned = _catalog.Games.Count(w => w.Local is not null);
            if (folders.Count == 0)
                Say("まだフォルダがないよ。「フォルダ追加」でゲームの場所を教えてね。",
                    "No folders yet: use Add Folder to show where your VIPRPG games are.");
            else
                Say($"{games.Count}本のゲームを見つけたぞ！ そのうち{owned}本が祭りの作品だ。",
                    $"Found {games.Count} games; {owned} of them are festival entries in the catalog.");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("VIPRPG scan failed: " + ex);
            Say("スキャンに失敗した… " + ex.Message, "The scan failed: " + ex.Message);
        }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>RPG Maker and Wolf RPG Editor games in the folders, and zips that may be unextracted games.</summary>
    /// <remarks>
    /// Each downloaded game's folder is looked through as well, from its own top: a game unpacked a folder or two
    /// further down than the zip's own folder is still found, however deep the download folder is.
    /// </remarks>
    private static (List<GameEntry> Games, List<string> Zips) Scan(List<string> folders, List<string> downloads,
        Dictionary<string, GameEntry> known, Action found)
    {
        var games = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        GameEntry? Known(string dir)
        {
            try { return known.GetValueOrDefault(Path.GetFullPath(dir)); }
            catch { return null; }
        }
        foreach (string folder in folders.Concat(downloads.Where(Directory.Exists)))
        {
            try
            {
                foreach (var game in GameScanner.Scan(folder, found, Known))
                {
                    if (game.Family is EngineFamily.RenPy or EngineFamily.Tyrano or EngineFamily.Unity) continue;
                    string full = Path.GetFullPath(game.RootPath);
                    if (!seen.Add(full)) continue;
                    game.Key = full;
                    games.Add(game);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"VIPRPG scan skipped {folder}: {ex.Message}");
            }
        }
        return (games, FindZips(folders));
    }

    /// <summary>Zips in the folders (three levels down): games not extracted yet.</summary>
    private static List<string> FindZips(List<string> folders)
    {
        var zips = new List<string>();
        foreach (string folder in folders)
        {
            try
            {
                zips.AddRange(Directory.EnumerateFiles(folder, "*.zip", new EnumerationOptions
                {
                    RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive
                }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Log($"VIPRPG zips skipped in {folder}: {ex.Message}");
            }
        }
        // Folders inside one another find the same zips twice.
        return zips.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void SetList(VipListMode mode)
    {
        // Coming back to the same list keeps the matsuri or author that was chosen.
        if (mode != Data.List)
        {
            Data.Selected = null;
            _sideQuery = "";
            if (_searchSide) SetSearchText("");
        }
        Data.List = mode;
        _save();
        ClearPicked();
        RebuildSide();
        _focus = HasFilterMenu ? Pane.Rows : Pane.Side;
    }

    private void RebuildSide()
    {
        var side = new List<SideItem>();
        switch (Data.List)
        {
            case VipListMode.Matsuri:
                // Festivals only: everything else is in 保管庫.
                // Newest first, and in order within a year (the catalog has a year's matsuri in name order).
                foreach (var matsuri in InMatsuriOrder(_catalog.Matsuri))
                    side.Add(new SideItem(matsuri.ShortName, matsuri.Works.Count(w => w.Local is not null), matsuri.Works.Count, matsuri, null, false));
                break;
            case VipListMode.Storage:
                side.AddRange(StorageSideItems());
                break;
            case VipListMode.Wolf:
                // Contest by contest, 第1回 first, like the 1-15代目 保管庫.
                foreach (var contest in VipWolf.Entries.GroupBy(e => e.Contest))
                {
                    int owned = contest.Count(e => _wolfGames.TryGetValue((e.Contest, e.No), out var game) && _remoteLocal.ContainsKey(game.Path));
                    side.Add(new SideItem(T($"第{contest.Key}回ウディコン", $"Contest {contest.Key} ({contest.First().Year})"), owned, contest.Count(),
                        null, null, false, Wodicon: contest.Key));
                }
                break;
            case VipListMode.All:
                // 全作品's older choice (お気に入り or a group, kept as the selected entry) becomes its filter, once.
                if (Data.Selected is "*fav" or "*all" || Data.Selected?.StartsWith("group:", StringComparison.Ordinal) == true)
                {
                    if (Data.Selected != "*all") Data.AllFilter = Data.Selected == "*fav" ? "show:fav" : "show:" + Data.Selected;
                    Data.Selected = null;
                }
                side.AddRange(SortFilterItems(all: true));
                break;
            case VipListMode.Playlist:
            {
                var all = AllRows();
                var keys = all.ToLookup(KeyOf, StringComparer.OrdinalIgnoreCase);
                foreach (var playlist in Data.Playlists)
                {
                    // Each entry once: owned of all, the ones the launcher can't find included.
                    var entries = playlist.Games.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    int owned = entries.Count(key => keys[key].FirstOrDefault()?.Local is not null);
                    side.Add(new SideItem("♪ " + VipDefaults.Label(playlist), owned, entries.Count, null, null, false,
                        Filter: PlaylistFilter + playlist.Name));
                }
                side.Add(new SideItem(T("＋ 新しいプレイリスト…", "＋ New Playlist…"), 0, NoCount, null, null, false, Filter: NewPlaylistItem));
                side.Add(new SideItem(T("＋ コードから読み込む…", "＋ Import from a Code…"), 0, NoCount, null, null, false, Filter: ImportPlaylistItem));
                side.Add(new SideItem(T("＋ テキストから作る…", "＋ Make from a Text File…"), 0, NoCount, null, null, false, Filter: TextPlaylistItem));
                break;
            }
            case VipListMode.Series:
            {
                var keys = AllRows().ToLookup(KeyOf, StringComparer.OrdinalIgnoreCase);
                // Biggest series first; the built-in sample and contest lists (no wiki page of their own) stay at the end.
                foreach (var series in VipSeries.List.OrderBy(s => s.Source.Length == 0).ThenByDescending(s => s.Source.Length == 0 ? 0 : s.Games.Count))
                {
                    int owned = series.Games.Count(g => keys[g.Key].FirstOrDefault()?.Local is not null);
                    side.Add(new SideItem(T(series.Name, series.NameEn.Length > 0 ? series.NameEn : series.Name), owned, series.Games.Count,
                        null, null, false, Filter: SeriesFilter + series.Name));
                }
                break;
            }
            case VipListMode.Author:
                foreach (var (author, works) in _catalog.ByAuthor()
                             .GroupBy(entry => AuthorLabel(entry.Author), StringComparer.OrdinalIgnoreCase)
                             .Select(group => (group.Key, group.SelectMany(entry => entry.Works).ToList()))
                             .OrderByDescending(entry => entry.Item2.Count)
                             .ThenBy(entry => entry.Key, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (_sideQuery.Length > 0 && !Fold(author).Contains(Fold(_sideQuery))) continue;
                    side.Add(new SideItem(author, works.Count(w => w.Local is not null), works.Count, null, author, false));
                }
                _authorFirst.Clear();
                break;
            default:
                side.AddRange(SortFilterItems(all: false));
                break;
        }
        if (Data.Pinned.Count > 0)
        {
            var pinned = side.Where(item => PinKey(item) is { } key && Data.Pinned.Contains(key))
                .OrderBy(item => Data.Pinned.IndexOf(PinKey(item)!)).ToList();
            side = pinned.Concat(side.Except(pinned)).ToList();
        }
        _side = side;
        int index = HasFilterMenu ? side.FindIndex(item => item.Filter == ListSort)
            : Data.Selected is { } key ? side.FindIndex(item => SideKey(item) == key) : -1;
        // Never a heading: 保管庫 starts with one.
        if (index < 0) index = Math.Max(0, side.FindIndex(item => item.Total != Heading));
        _sideIndex = index;
        _sideTop = Math.Max(0, _sideIndex - _sideVisible / 2);
        RebuildRows();
    }

    private static string SideKey(SideItem item) =>
        item.Wodicon > 0 ? "*wolf:" + item.Wodicon : item.Filter ?? item.Matsuri?.Id ?? item.Author ?? (item.Strays ? "*strays" : item.Server ? "*server:" + item.Area : "*owned");

    private void RebuildRows()
    {
        var rows = new List<Row>();
        var item = _sideIndex < _side.Count ? _side[_sideIndex] : null;
        if (HasFilterMenu)
        {
            rows.AddRange(SortedRows());
        }
        else if (item?.Filter is { } list && list.StartsWith(PlaylistFilter, StringComparison.Ordinal))
        {
            if (Data.Playlists.FirstOrDefault(p => p.Name == list[PlaylistFilter.Length..]) is { } playlist)
            {
                var keys = AllRows().ToLookup(KeyOf, StringComparer.OrdinalIgnoreCase);
                // Entries the launcher doesn't know stay in the list, marked, so they can be looked for.
                // Each game once, even if the list has it twice.
                rows.AddRange(playlist.Games.Distinct(StringComparer.OrdinalIgnoreCase).Select(key => keys[key].FirstOrDefault()
                    ?? new Row(null, null, "!!!!", key.StartsWith(TitleKeyPrefix, StringComparison.Ordinal) ? key[TitleKeyPrefix.Length..] : key, "",
                        Missing: key)));
            }
        }
        else if (item?.Filter is { } seriesFilter && seriesFilter.StartsWith(SeriesFilter, StringComparison.Ordinal))
        {
            if (VipSeries.List.FirstOrDefault(s => s.Name == seriesFilter[SeriesFilter.Length..]) is { } series)
            {
                var keys = AllRows().ToLookup(KeyOf, StringComparer.OrdinalIgnoreCase);
                // Release order, numbered from 1; a game no copy is known of stays in its place, marked.
                for (int i = 0; i < series.Games.Count; i++)
                {
                    var game = series.Games[i];
                    string number = (i + 1).ToString();
                    rows.Add(keys[game.Key].FirstOrDefault() is { } found
                        ? found with { No = number, Title = game.Via is { } via ? $"{game.Title}（{via}収録）" : found.Title }
                        : new Row(null, null, number, game.Title, "", Missing: game.Key));
                }
            }
        }
        else if (item?.Matsuri is { } matsuri)
        {
            foreach (var work in matsuri.Works) rows.Add(RowFor(work, work.No));
        }
        else if (item?.Author is { } author)
        {
            foreach (var work in _catalog.Games.Where(w => string.Equals(AuthorLabel(w.Author), author, StringComparison.OrdinalIgnoreCase)))
                rows.Add(RowFor(work, Short(work.Event)));
        }
        else if (item?.Strays == true)
        {
            foreach (var game in _strays.OrderBy(g => g.DisplayTitle, StringComparer.CurrentCultureIgnoreCase))
                rows.Add(new Row(null, game, "", game.DisplayTitle, ""));
            foreach (string zip in _strayZips.OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
                rows.Add(new Row(null, null, "zip", Path.GetFileNameWithoutExtension(zip), "", zip));
        }
        else if (item is { Wodicon: > 0 })
        {
            // Every entry in entry-number order: the server's copy where there is one, else marked × as not found.
            var listed = new HashSet<int>();
            foreach (var entry in VipWolf.Entries.Where(e => e.Contest == item.Wodicon))
            {
                listed.Add(entry.No);
                var remote = _wolfGames.GetValueOrDefault((entry.Contest, entry.No));
                rows.Add(new Row(null, remote is null ? null : _remoteLocal.GetValueOrDefault(remote.Path), entry.No.ToString(), entry.Title,
                    entry.Author, Remote: remote, Wolf: entry));
            }
            // A server game with a number the entry list doesn't have still shows, after the rest.
            foreach (var ((contest, no), remote) in _wolfGames.Where(pair => pair.Key.Contest == item.Wodicon && !listed.Contains(pair.Key.No))
                         .OrderBy(pair => pair.Key.No))
                rows.Add(RowFor(remote) with { No = no.ToString() });
        }
        else if (item?.Server == true)
        {
            foreach (var remote in _remoteExtras.Where(g => (g.Area ?? "*server") == item.Area)) rows.Add(RowFor(remote));
        }
        else if (item is not null)
        {
            var matched = _catalog.Games.Where(w => w.Local is not null).Select(w => RowFor(w, Short(w.Event)));
            var others = _strays.Select(g => new Row(null, g, "", g.DisplayTitle, ""));
            var downloaded = _remoteExtras.Where(g => _remoteLocal.ContainsKey(g.Path)).Select(g => RowFor(g));
            rows.AddRange(matched.Concat(others).Concat(downloaded).OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase));
        }
        // Counted before completed games are hidden or a search narrows the list: the whole matsuri or playlist.
        _completedCount = (rows.Count(IsCompleted), rows.Count);
        if (!Data.ShowCompleted && Data.Completed.Count > 0) rows = rows.Where(r => !IsCompleted(r)).ToList();
        // Hidden games show only under 絞り込み → 非表示の作品.
        if (Data.Hidden.Count > 0 && !(HasFilterMenu && ListFilter == HiddenFilter)) rows = rows.Where(r => !IsHidden(r)).ToList();
        if (_query.Length > 0)
        {
            string query = Fold(_query);
            rows = rows.Where(r => Fold(r.Title).Contains(query) || Fold(r.Author).Contains(query) ||
                                   Fold(r.Work?.Event?.Name).Contains(query) || Fold(r.Remote?.Thread).Contains(query) ||
                                   // The server game's note holds its original file name ("元: 0035：ゲーム名.zip").
                                   Fold(r.Remote?.Note).Contains(query)).ToList();
        }
        _rows = rows;
        // Picked games that left the list (hidden once completed, or narrowed away by a search) aren't picked any more.
        if (_picked.Count > 0) _picked.IntersectWith(rows.Select(KeyOf));
        _rowIndex = Math.Clamp(_rowIndex, 0, Math.Max(0, rows.Count - 1));
        _rowTop = Math.Clamp(_rowTop, 0, Math.Max(0, rows.Count - 1));
        Redraw();
    }

    /// <summary>Text folded for searching: full-width to half-width, lower case.</summary>
    private static string Fold(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();

    // Side-list rows that are a heading, or an order with no count.
    private const int Heading = -2, NoCount = -1;

    private static (string Key, string Label)[] OwnedSorts =>
    [
        ("sort:title", T("タイトル順", "By Title")), ("sort:recent", T("最近遊んだ順", "Recently Played")),
        ("sort:most", T("よく遊んだ順", "Most Played")), ("sort:time", T("プレイ時間順", "Play Time")),
        ("sort:added", T("追加した順", "Recently Added")), ("sort:matsuri", T("祭り順", "By Festival"))
    ];

    /// <summary>Uploaders with fewer games than this are left out of 保管庫: a stray patch or two, not a collection.</summary>
    private const int StorageMinimum = 3;

    // The archive's sections, in the order 保管庫 lists them; any other section goes under その他.
    private static readonly string[] StorageSections = { "保存库", "Wiki", "主帖用上传区", "モナーRPG", "アレックス総受け" };

    /// <summary>The server's section for unique games from the uploaders and the wiki, shown under その他 (Other).</summary>
    private const string OtherSection = "その他";

    /// <summary>Areas listed in their own order rather than biggest first: the 1-15代目 保管庫 (hokan01…) and the WOLF RPG Editor contests (wodicon01…).</summary>
    private static bool NumberedArea(string? area) =>
        area is not null && (area.StartsWith("hokan", StringComparison.Ordinal) || area.StartsWith("wodicon", StringComparison.Ordinal));

    private static string StorageHeading(string section) => section switch
    {
        "保存库" => T("保管庫", "Storage"),
        "Wiki" => T("wiki保管庫", "Wiki Storage"),
        "主帖用上传区" => T("スレのうｐろだ", "Thread Uploaders"),
        "*other" => T("その他", "Other"),
        "*pc" => T("PCだけにある作品", "Only on This PC"),
        _ => section
    };

    /// <summary>
    /// 保管庫's side list: the storage uploaders under their sections, biggest first, then the games on this PC that
    /// no festival lists.
    /// </summary>
    private List<SideItem> StorageSideItems()
    {
        var items = new List<SideItem>();
        // The WOLF RPG contests have their own list.
        var areas = _remoteExtras.Where(g => !VipWolf.IsContestArea(g.Area)).GroupBy(g => g.Area ?? "*server")
            .Select(group => group.ToList())
            .GroupBy(games => games[0].Area is null ? "*server"
                : Array.IndexOf(StorageSections, games[0].Section) >= 0 ? games[0].Section! : "*other")
            .OrderBy(section => section.Key == "*server" ? -1 : section.Key == "*other" ? StorageSections.Length : Array.IndexOf(StorageSections, section.Key));
        foreach (var section in areas)
        {
            if (section.Key != "*server") items.Add(new SideItem(StorageHeading(section.Key), 0, Heading, null, null, false));
            // The hosted 1-15代目 保管庫 and the contests in their own order, first; other uploaders biggest first.
            foreach (var games in section.OrderBy(games => NumberedArea(games[0].Area) ? 0 : 1)
                         .ThenBy(games => NumberedArea(games[0].Area) ? games[0].Area : "", StringComparer.Ordinal)
                         .ThenByDescending(games => games.Count))
            {
                string key = games[0].Area ?? "*server";
                items.Add(new SideItem(games[0].Area is null ? T("サーバーの作品", "Server Games") : games[0].Thread ?? key,
                    games.Count(g => _remoteLocal.ContainsKey(g.Path)), games.Count, null, null, false, Server: true, Area: key));
            }
        }
        if (_strays.Count + _strayZips.Count > 0)
        {
            items.Add(new SideItem(StorageHeading("*pc"), 0, Heading, null, null, false));
            items.Add(new SideItem(T("その他のゲーム", "Other Games"), _strays.Count, _strays.Count + _strayZips.Count, null, null, true));
        }
        return items;
    }

    // 手持ち's and 全作品's order and filter, each list keeping its own.
    private string ListSort
    {
        get => Data.List == VipListMode.All ? Data.AllSort : Data.OwnedSort;
        set
        {
            if (Data.List == VipListMode.All) Data.AllSort = value;
            else Data.OwnedSort = value;
        }
    }

    private string ListFilter
    {
        get => Data.List == VipListMode.All ? Data.AllFilter : Data.OwnedFilter;
        set
        {
            if (Data.List == VipListMode.All) Data.AllFilter = value;
            else Data.OwnedFilter = value;
        }
    }

    private string SortLabel => OwnedSorts.FirstOrDefault(sort => sort.Key == ListSort).Label ?? T("タイトル順", "By Title");

    private string FilterLabel => _side.FirstOrDefault(i => i.Filter == ListFilter)?.Label.TrimStart('★', '■', ' ') ?? T("すべて", "All");

    /// <summary>
    /// 手持ち's and 全作品's side list, shown as their 並べ替え・絞り込み menu: the orders, then the filters (favorites,
    /// groups, engines) with their counts.
    /// </summary>
    private List<SideItem> SortFilterItems(bool all)
    {
        var every = (all ? AllRows() : AllRows().Where(r => r.Local is not null)).ToList();
        var hidden = every.Where(IsHidden).ToList();
        var rows = every.Where(r => !IsHidden(r)).Select(r => (Row: r, Family: FamilyOf(r))).ToList();
        SideItem Filter(string label, Func<(Row Row, EngineFamily? Family), bool> match, string key)
        {
            var shown = rows.Where(match).ToList();
            return new SideItem(label, shown.Count(entry => entry.Row.Local is not null), shown.Count, null, null, false, Filter: key);
        }
        var items = new List<SideItem> { new(T("並べ替え", "Sort"), 0, Heading, null, null, false) };
        items.AddRange(OwnedSorts.Select(sort => new SideItem(sort.Label, 0, NoCount, null, null, false, Filter: sort.Key)));
        items.Add(new SideItem(T("絞り込み", "Show"), 0, Heading, null, null, false));
        items.Add(Filter(T("すべて", "All"), _ => true, "show:all"));
        items.Add(Filter(T("★ お気に入り", "★ Favorites"), entry => IsFavorite(entry.Row), "show:fav"));
        items.Add(new SideItem(T("非表示の作品", "Hidden Games"), hidden.Count(r => r.Local is not null), hidden.Count, null, null, false, Filter: HiddenFilter));
        foreach (string group in Data.Groups)
        {
            string name = group;
            items.Add(Filter("■ " + name, entry => InGroup(entry.Row, name), "show:group:" + name));
        }
        foreach (var family in EngineFamilies.All)
        {
            var item = Filter(EngineFamilies.Label(family), entry => entry.Family == family, "show:engine:" + family);
            if (item.Total > 0) items.Add(item);
        }
        // Most thread games in 全作品 have no engine on record.
        var unknown = Filter(T("エンジン不明", "Unknown Engine"), entry => entry.Family is null, UnknownEngine);
        if (unknown.Total > 0) items.Add(unknown);
        return items;
    }

    private const string UnknownEngine = "show:engine:none";

    // Engine names the families know, once full-width digits are folded ("２０００", "RPGツクールVXAce", "Steam2003"…).
    private static readonly System.Text.RegularExpressions.Regex KnownEngine =
        new(@"2000|2003|VX|XP|MV|MZ|Ren'?Py|Tyrano|Unity", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A game's engine: the PC's copy's, else what the catalog or the server says; null when nobody recorded one.</summary>
    private static EngineFamily? FamilyOf(Row row)
    {
        if (row.Local is { } local) return local.Family;
        if (row.Wolf is not null) return EngineFamily.Wolf;
        string engine = (row.Work?.Engine ?? row.Remote?.Engine ?? "").Normalize(System.Text.NormalizationForm.FormKC);
        if (engine.Contains("WOLF", StringComparison.OrdinalIgnoreCase) || engine.Contains("ウディタ", StringComparison.Ordinal)) return EngineFamily.Wolf;
        return KnownEngine.IsMatch(engine) ? EngineFamilies.Of(engine) : null;
    }

    /// <summary>手持ち's or 全作品's games, narrowed by the chosen filter and in the chosen order.</summary>
    private IEnumerable<Row> SortedRows()
    {
        IEnumerable<Row> rows = Data.List == VipListMode.All ? AllRows() : AllRows().Where(r => r.Local is not null);
        string filter = ListFilter;
        if (filter == "show:fav") rows = rows.Where(IsFavorite);
        else if (filter == HiddenFilter) rows = rows.Where(IsHidden);
        else if (filter.StartsWith("show:group:", StringComparison.Ordinal)) rows = rows.Where(r => InGroup(r, filter["show:group:".Length..]));
        else if (filter == UnknownEngine) rows = rows.Where(r => FamilyOf(r) is null);
        else if (filter.StartsWith("show:engine:", StringComparison.Ordinal))
            rows = rows.Where(r => FamilyOf(r)?.ToString() == filter["show:engine:".Length..]);

        // A game not on this PC has no play record or folder date: those come after, still by title.
        VipRecord? Record(Row r) => r.Local is { } local ? Data.Games.GetValueOrDefault(local.RootPath) : null;
        return ListSort switch
        {
            "sort:recent" => rows.OrderByDescending(r => Record(r)?.LastPlayedUtc ?? DateTime.MinValue),
            "sort:most" => rows.OrderByDescending(r => Record(r)?.PlayCount ?? 0),
            "sort:time" => rows.OrderByDescending(r => Record(r)?.PlaySeconds ?? 0),
            "sort:added" => rows.OrderByDescending(r => r.Local?.FolderCreatedUtc ?? DateTime.MinValue),
            // In 祭り別's order (newest first, each year's in the order they were held), then by entry number;
            // games no matsuri lists come last.
            "sort:matsuri" => rows.OrderBy(r => r.Work?.Event is { } matsuri ? MatsuriRank().GetValueOrDefault(matsuri, int.MaxValue) : int.MaxValue)
                .ThenBy(r => int.TryParse(new string((r.Work?.No ?? "").Where(char.IsAsciiDigit).ToArray()), out int n) ? n : int.MaxValue),
            _ => rows // already by title
        };
    }

    private Dictionary<VipMatsuri, int>? _matsuriRank;

    /// <summary>Each matsuri's place in 祭り別: newest first, and in order within a year.</summary>
    private Dictionary<VipMatsuri, int> MatsuriRank() => _matsuriRank ??= InMatsuriOrder(_catalog.Matsuri)
        .Select((matsuri, index) => (matsuri, index)).ToDictionary(entry => entry.matsuri, entry => entry.index);

    /// <summary>The matsuri as 祭り別 lists them: newest year first, and each year's in the order they were held.</summary>
    private static IEnumerable<VipMatsuri> InMatsuriOrder(IEnumerable<VipMatsuri> matsuri) =>
        matsuri.OrderByDescending(m => m.Year ?? 0).ThenByDescending(m => m.Month);

    /// <summary>手持ち and 全作品 have no page of matsuri or authors: their sorts and filters are a menu instead.</summary>
    private bool HasFilterMenu => Data.List is VipListMode.Owned or VipListMode.All;

    /// <summary>The 並べ替え・絞り込み menu: the side list's orders and filters, the current ones checked.</summary>
    private void OpenFilterMenu(Point at)
    {
        var items = new List<VipMenu.Item>();
        for (int i = 0; i < _side.Count; i++)
        {
            var item = _side[i];
            if (item.Total == Heading)
            {
                items.Add(new(item.Label, null));
                continue;
            }
            int index = i;
            bool chosen = HasFilterMenu ? item.Filter == ListSort || item.Filter == ListFilter : index == _sideIndex;
            string count = item.Total == NoCount ? "" : $"  ({item.Total})";
            string? group = item.Filter?.StartsWith("show:group:", StringComparison.Ordinal) == true ? item.Filter["show:group:".Length..] : null;
            items.Add(new("  " + item.Label + count, () =>
            {
                CloseOverlay();
                SelectSide(index);
            }, Checked: chosen, RightClick: group is null ? null : () => _ = DeleteGroupAsync(group)));
        }
        _overlay = new VipMenu(at, items, CloseOverlay);
        Redraw();
    }

    private void ChooseSortFilter(SideItem item)
    {
        if (item.Filter is not { } choice) return;
        if (choice.StartsWith("sort:", StringComparison.Ordinal)) ListSort = choice;
        else ListFilter = choice;
        _save();
        _rowIndex = _rowTop = 0;
        ClearPicked();
        RebuildRows();
        bool all = Data.List == VipListMode.All;
        Say($"{(all ? "全作品" : "手持ち")}を{SortLabel}で、{FilterLabel}を表示中。{_rows.Count}本あるよ。",
            $"{(all ? "All games" : "Your games")}: {_rows.Count} shown, {SortLabel}, {FilterLabel}.");
    }

    /// <summary>Every game: catalog works, games on this PC the catalog doesn't list, and archived uploads, by title.</summary>
    private List<Row> AllRows()
    {
        var rows = _catalog.Games.Select(w => RowFor(w, Short(w.Event))).ToList();
        rows.AddRange(_strays.Select(g => new Row(null, g, "", g.DisplayTitle, "")));
        rows.AddRange(_strayZips.Select(zip => new Row(null, null, "zip", Path.GetFileNameWithoutExtension(zip), "", zip)));
        rows.AddRange(_remoteExtras.Select(g => RowFor(g)));
        // By the title's letters, so names starting with "_" or "-" don't all come first; titles with none go last.
        return rows.Select(row => (Row: row, Key: VipCatalog.TitleKey(row.Title)))
            .OrderBy(entry => entry.Key.Length == 0)
            .ThenBy(entry => entry.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => entry.Row)
            .ToList();
    }

    // ---- favorites and groups ----

    /// <summary>A game's lasting key: its catalog work, archive path, folder or zip.</summary>
    private static string KeyOf(Row row) =>
        row.Missing is { } missing ? missing
        : row.Work is { } work ? $"{work.Matsuri}/{(string.IsNullOrWhiteSpace(work.No) ? work.Title : work.No)}"
        : row.Remote is { } remote ? "dl:" + remote.Path
        : row.Wolf is { } entry ? $"wolf:{entry.Contest}/{entry.No}"
        : row.Local is { } local ? "pc:" + Path.GetFullPath(local.RootPath)
        : "zip:" + row.Zip;

    private bool IsFavorite(Row row) => Data.Favorites.Contains(KeyOf(row), StringComparer.OrdinalIgnoreCase);

    private bool IsCompleted(Row row) => Data.Completed.Contains(KeyOf(row), StringComparer.OrdinalIgnoreCase);

    private const string HiddenFilter = "show:hidden";

    private bool IsHidden(Row row) => Data.Hidden.Count > 0 && Data.Hidden.Contains(KeyOf(row), StringComparer.OrdinalIgnoreCase);

    private void ToggleHidden(Row row)
    {
        string key = KeyOf(row);
        bool now = !IsHidden(row);
        if (now) Data.Hidden.Add(key);
        else Data.Hidden.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        _save();
        Say(now ? $"「{row.Title}」を非表示にしたよ。手持ちか全作品の 絞り込み → 非表示の作品 で戻せるよ。" : $"「{row.Title}」をまた表示するよ。",
            now ? $"Hid {row.Title}. Find it under Sort / Filter → Hidden Games in On This PC or All Games to unhide it." : $"{row.Title} is shown again.");
        RebuildList();
    }

    private void ToggleCompleted(Row row)
    {
        string key = KeyOf(row);
        bool now = !IsCompleted(row);
        if (now) Data.Completed.Add(key);
        else Data.Completed.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        _save();
        Say(now ? $"「{row.Title}」をクリア済みにしたよ。{(Data.ShowCompleted ? "" : "（設定で隠しているので一覧から消えるよ）")}" : $"「{row.Title}」のクリア済みを外したよ。",
            now ? $"Marked {row.Title} completed.{(Data.ShowCompleted ? "" : " Completed games are hidden (Settings), so it leaves the list.")}" : $"{row.Title} is no longer marked completed.");
        RebuildList();
    }

    /// <summary>Rebuilds the lists after a setting changes what they show.</summary>
    public void RefreshList() => RebuildList();

    private bool InGroup(Row row, string group) =>
        Data.GroupMembers.TryGetValue(KeyOf(row), out var groups) && groups.Contains(group, StringComparer.Ordinal);

    private void ToggleFavorite(Row row)
    {
        string key = KeyOf(row);
        bool now = !IsFavorite(row);
        if (now) Data.Favorites.Add(key);
        else Data.Favorites.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        _save();
        Say(now ? $"「{row.Title}」をお気に入りにしたよ。" : $"「{row.Title}」をお気に入りから外したよ。",
            now ? $"{row.Title} is a favorite now." : $"{row.Title} is no longer a favorite.");
        RebuildList();
    }

    private void ToggleGroup(Row row, string group)
    {
        string key = KeyOf(row);
        if (!Data.GroupMembers.TryGetValue(key, out var groups)) Data.GroupMembers[key] = groups = new List<string>();
        bool add = !groups.Contains(group, StringComparer.Ordinal);
        if (add) groups.Add(group);
        else groups.Remove(group);
        if (groups.Count == 0) Data.GroupMembers.Remove(key);
        _save();
        Say(add ? $"「{row.Title}」を「{group}」に入れたよ。" : $"「{row.Title}」を「{group}」から出したよ。",
            add ? $"Added {row.Title} to {group}." : $"Took {row.Title} out of {group}.");
        RebuildList();
    }

    private async Task NewGroupAsync(Row? row)
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        var dialog = new TextPromptDialog(T("新しいグループ", "New Group"), T("グループの名前:", "Group name:"), "", T("作る", "Create"));
        if (!await dialog.ShowAsync(window) || dialog.Value.Length == 0) return;
        string name = dialog.Value;
        if (!Data.Groups.Contains(name, StringComparer.Ordinal)) Data.Groups.Add(name);
        if (row is not null)
        {
            ToggleGroup(row, name);
            return;
        }
        _save();
        RebuildList();
    }

    /// <summary>Right-click a group in a menu: deletes it once confirmed; its games stay.</summary>
    private async Task DeleteGroupAsync(string group)
    {
        CloseOverlay();
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        int count = Data.GroupMembers.Values.Count(groups => groups.Contains(group, StringComparer.Ordinal));
        Diagnostics.Log($"Asked to delete the group {group} ({count} games)");
        bool yes = await MessageDialog.ShowAsync(window,
            T($"グループ「{group}」を消しますか？（{count}本）\n入っている作品は消えません。", $"Delete the group {group} ({count} games)?\nThe games in it stay."),
            T("グループを消す", "Delete Group"), MessageButtons.YesNo, defaultNo: true, avoid: this.PointToScreen(_pointerView));
        if (!yes) return;
        // A list showing only that group shows everything again.
        if (Data.OwnedFilter == "show:group:" + group) Data.OwnedFilter = "show:all";
        if (Data.AllFilter == "show:group:" + group) Data.AllFilter = "show:all";
        DeleteGroup(group);
    }

    private void DeleteGroup(string group)
    {
        Data.Groups.Remove(group);
        foreach (var key in Data.GroupMembers.Keys.ToList())
        {
            Data.GroupMembers[key].Remove(group);
            if (Data.GroupMembers[key].Count == 0) Data.GroupMembers.Remove(key);
        }
        _save();
        Say($"グループ「{group}」を消したよ。作品はそのまま。", $"Deleted the group {group}; its games are untouched.");
        RebuildList();
    }

    // ---- picking several games ----

    private void ClearPicked()
    {
        _pickAnchor = null;
        if (_picked.Count == 0) return;
        _picked.Clear();
        Redraw();
    }

    /// <summary>Ctrl-click: adds a game to the pick, or takes it out. The cursor's game joins the pick as it starts.</summary>
    private void TogglePick(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        if (_picked.Count == 0 && SelectedRow is { } current && _rowIndex != index) _picked.Add(KeyOf(current));
        string key = KeyOf(_rows[index]);
        if (!_picked.Remove(key)) _picked.Add(key);
        _pickAnchor = index;
        SelectRow(index);
        SayPicked();
    }

    /// <summary>Shift-click or Shift+↑↓: picks the games from the anchor (or the cursor's game) to this one.</summary>
    private void PickTo(int index)
    {
        if (_rows.Count == 0) return;
        index = Math.Clamp(index, 0, _rows.Count - 1);
        int anchor = Math.Clamp(_pickAnchor ?? _rowIndex, 0, _rows.Count - 1);
        _picked.Clear();
        for (int i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++) _picked.Add(KeyOf(_rows[i]));
        _pickAnchor = anchor;
        SelectRow(index);
        SayPicked();
    }

    private void PickAll()
    {
        _picked.UnionWith(_rows.Select(KeyOf));
        SayPicked();
    }

    private void SayPicked()
    {
        if (_picked.Count > 1)
            Say($"{_picked.Count}本を選んだよ。右クリックで、まとめてお気に入り・クリア済み・グループ・プレイリストに。",
                $"{_picked.Count} games selected: right-click them to favorite, mark completed, group or add to a playlist all at once.");
        Redraw();
    }

    /// <summary>The right-click menu for several picked games: favorites, completed, groups and playlists, for all of them.</summary>
    private void OpenPickedMenu(List<Row> rows, Point at)
    {
        var keys = rows.Select(KeyOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool AllIn(IEnumerable<string> list)
        {
            var have = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            return keys.All(have.Contains);
        }
        var items = new List<VipMenu.Item> { new(T($"{keys.Count}本を選択中", $"{keys.Count} Games Selected"), null) };
        bool favorite = AllIn(Data.Favorites);
        items.Add(new(favorite ? T("お気に入りから外す", "Remove from Favorites") : T("お気に入りに追加", "Add to Favorites"), () =>
            ChangePicked(keys, Data.Favorites, !favorite,
                ("{0}本をお気に入りにしたよ。", "{0} games are favorites now."), ("{0}本をお気に入りから外したよ。", "Took {0} games out of the favorites.")),
            Checked: favorite));
        bool completed = AllIn(Data.Completed);
        (string Ja, string En) hidden = Data.ShowCompleted ? ("", "") : ("（設定で隠しているので一覧から消えるよ）", " Completed games are hidden (Settings), so they leave the list.");
        items.Add(new(completed ? T("クリア済みを外す", "Unmark Completed") : T("クリア済みにする", "Mark Completed"), () =>
            ChangePicked(keys, Data.Completed, !completed,
                ("{0}本をクリア済みにしたよ。" + hidden.Ja, "Marked {0} games completed." + hidden.En),
                ("{0}本のクリア済みを外したよ。", "{0} games are no longer marked completed.")),
            Checked: completed));
        if (Data.Groups.Count > 0) items.Add(new(T("グループ", "Groups"), null));
        foreach (string group in Data.Groups)
        {
            string name = group;
            bool inGroup = keys.All(key => Data.GroupMembers.TryGetValue(key, out var groups) && groups.Contains(name, StringComparer.Ordinal));
            items.Add(new("  " + name, () => GroupPicked(keys, name, !inGroup), Checked: inGroup, RightClick: () => _ = DeleteGroupAsync(name)));
        }
        items.Add(new(T("新しいグループ…", "New Group…"), () => _ = NewGroupForPickedAsync(keys)));
        if (Data.Playlists.Count > 0) items.Add(new(T("プレイリスト", "Playlists"), null));
        foreach (var playlist in Data.Playlists)
        {
            var list = playlist;
            bool inList = AllIn(list.Games);
            items.Add(new("  ♪ " + VipDefaults.Label(list), () => PlaylistPicked(keys, list, !inList), Checked: inList));
        }
        items.Add(new(T("新しいプレイリスト…", "New Playlist…"), () => _ = NewPlaylistForPickedAsync(keys)));
        _overlay = new VipMenu(at, items, CloseOverlay);
        Redraw();
    }

    /// <summary>Adds the picked games to a list of keys (favorites, completed, a playlist) or takes them out.</summary>
    private void ChangePicked(List<string> keys, List<string> list, bool add, (string Ja, string En) added, (string Ja, string En) removed)
    {
        int changed = 0;
        foreach (string key in keys)
        {
            bool has = list.Contains(key, StringComparer.OrdinalIgnoreCase);
            if (add && !has)
            {
                list.Add(key);
                changed++;
            }
            else if (!add && has)
            {
                list.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                changed++;
            }
        }
        _save();
        var (ja, en) = add ? added : removed;
        Say(string.Format(ja, changed), string.Format(en, changed));
        RebuildList();
    }

    private void PlaylistPicked(List<string> keys, VipPlaylist playlist, bool add)
    {
        string label = VipDefaults.Label(playlist).Replace("{", "{{").Replace("}", "}}");
        ChangePicked(keys, playlist.Games, add,
            ("{0}本をプレイリスト「" + label + "」に入れたよ。", "Added {0} games to the playlist " + label + "."),
            ("{0}本を「" + label + "」から出したよ。", "Took {0} games out of " + label + "."));
    }

    private void GroupPicked(List<string> keys, string group, bool add)
    {
        int changed = 0;
        foreach (string key in keys)
        {
            if (!Data.GroupMembers.TryGetValue(key, out var groups)) Data.GroupMembers[key] = groups = new List<string>();
            if (add && !groups.Contains(group, StringComparer.Ordinal))
            {
                groups.Add(group);
                changed++;
            }
            else if (!add && groups.Remove(group)) changed++;
            if (groups.Count == 0) Data.GroupMembers.Remove(key);
        }
        _save();
        Say(add ? $"{changed}本を「{group}」に入れたよ。" : $"{changed}本を「{group}」から出したよ。",
            add ? $"Added {changed} games to {group}." : $"Took {changed} games out of {group}.");
        RebuildList();
    }

    private async Task NewGroupForPickedAsync(List<string> keys)
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        var dialog = new TextPromptDialog(T("新しいグループ", "New Group"), T("グループの名前:", "Group name:"), "", T("作る", "Create"));
        if (!await dialog.ShowAsync(window) || dialog.Value.Length == 0) return;
        if (!Data.Groups.Contains(dialog.Value, StringComparer.Ordinal)) Data.Groups.Add(dialog.Value);
        GroupPicked(keys, dialog.Value, add: true);
    }

    private async Task NewPlaylistForPickedAsync(List<string> keys)
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        var dialog = new TextPromptDialog(T("新しいプレイリスト", "New Playlist"), T("プレイリストの名前:", "Playlist name:"), "", T("作る", "Create"));
        if (!await dialog.ShowAsync(window) || dialog.Value.Length == 0) return;
        var playlist = Data.Playlists.FirstOrDefault(p => p.Name == dialog.Value);
        if (playlist is null) Data.Playlists.Add(playlist = new VipPlaylist { Name = dialog.Value });
        PlaylistPicked(keys, playlist, add: true);
    }

    // ---- playlists ----

    // A シリーズ別 side entry: this prefix and the series' name.
    private const string SeriesFilter = "*series:";

    private const string PlaylistFilter = "playlist:", NewPlaylistItem = "*playlist-new", ImportPlaylistItem = "*playlist-import",
        TextPlaylistItem = "*playlist-text";

    // A playlist entry made from a title the launcher couldn't find.
    private const string TitleKeyPrefix = "title:";

    // Share codes: this prefix, then the playlist as deflated JSON in URL-safe base64.
    private const string PlaylistCodePrefix = "VIPRPG-PL1:";

    /// <summary>Opens a side entry's works, or runs it when it's one of the playlist actions.</summary>
    private void OpenSide(int index)
    {
        if (index < 0 || index >= _side.Count) return;
        if (_side[index].Filter == NewPlaylistItem)
        {
            _ = NewPlaylistAsync(null);
            return;
        }
        if (_side[index].Filter == ImportPlaylistItem)
        {
            _ = ImportPlaylistAsync();
            return;
        }
        if (_side[index].Filter == TextPlaylistItem)
        {
            _ = PlaylistFromTextAsync();
            return;
        }
        SelectSide(index);
        // Choosing opens its works.
        _focus = Pane.Rows;
    }

    private bool InPlaylist(Row row, VipPlaylist playlist) => playlist.Games.Contains(KeyOf(row), StringComparer.OrdinalIgnoreCase);

    private void TogglePlaylist(Row row, VipPlaylist playlist)
    {
        string key = KeyOf(row);
        bool add = !InPlaylist(row, playlist);
        if (add) playlist.Games.Add(key);
        else playlist.Games.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        _save();
        string label = VipDefaults.Label(playlist);
        Say(add ? $"「{row.Title}」をプレイリスト「{label}」に入れたよ。({playlist.Games.Count}本)" : $"「{row.Title}」を「{label}」から出したよ。",
            add ? $"Added {row.Title} to the playlist {label} ({playlist.Games.Count} games)." : $"Took {row.Title} out of {label}.");
        RebuildList();
    }

    private async Task NewPlaylistAsync(Row? row)
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        var dialog = new TextPromptDialog(T("新しいプレイリスト", "New Playlist"), T("プレイリストの名前:", "Playlist name:"), "", T("作る", "Create"));
        if (!await dialog.ShowAsync(window) || dialog.Value.Length == 0) return;
        var playlist = Data.Playlists.FirstOrDefault(p => p.Name == dialog.Value);
        if (playlist is null) Data.Playlists.Add(playlist = new VipPlaylist { Name = dialog.Value });
        if (row is not null)
        {
            if (!InPlaylist(row, playlist)) TogglePlaylist(row, playlist);
            return;
        }
        _save();
        RebuildList();
        Say($"プレイリスト「{playlist.Name}」を作ったよ。作品を右クリックして入れてね。",
            $"Made the playlist {playlist.Name}. Right-click games to add them.");
    }

    /// <summary>The right-click menu for a playlist: copy its share code, or delete it.</summary>
    private void OpenPlaylistMenu(string name, Point at)
    {
        if (Data.Playlists.FirstOrDefault(p => p.Name == name) is not { } playlist) return;
        _overlay = new VipMenu(at, new List<VipMenu.Item>
        {
            new(T("コードをコピー", "Copy Code"), () => _ = CopyPlaylistCodeAsync(playlist)),
            new(T("名前を変える…", "Rename…"), () => _ = RenamePlaylistAsync(playlist)),
            new(T($"「{RetroDraw.Fit(name, 140)}」を消す", $"Delete {RetroDraw.Fit(name, 140)}"),
                () => _ = DeletePlaylistAsync(playlist))
        }, CloseOverlay);
        Redraw();
    }

    /// <summary>Asks first (No is the default), then deletes the playlist; its games stay.</summary>
    private async Task DeletePlaylistAsync(VipPlaylist playlist)
    {
        CloseOverlay();
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        string name = VipDefaults.Label(playlist);
        bool yes = await MessageDialog.ShowAsync(window,
            T($"プレイリスト「{name}」を消しますか？\n入っている作品は消えません。",
                $"Delete the playlist {name}?\nThe games in it stay."),
            T("プレイリストを消す", "Delete Playlist"), MessageButtons.YesNo, defaultNo: true,
            avoid: this.PointToScreen(_pointerView));
        if (!yes || !Data.Playlists.Remove(playlist)) return;
        _save();
        RebuildList();
        Say($"プレイリスト「{name}」を消したよ。作品はそのまま。", $"Deleted the playlist {name}; its games are untouched.");
    }

    private async Task RenamePlaylistAsync(VipPlaylist playlist)
    {
        CloseOverlay();
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        var dialog = new TextPromptDialog(T("プレイリストの名前", "Rename Playlist"), T("新しい名前:", "New name:"), VipDefaults.Label(playlist), T("変える", "Rename"));
        if (!await dialog.ShowAsync(window) || dialog.Value.Length == 0 || dialog.Value == VipDefaults.Label(playlist)) return;
        if (Data.Playlists.Any(p => p != playlist && p.Name == dialog.Value))
        {
            Say($"「{dialog.Value}」はもうあるよ。", $"There's already a playlist called {dialog.Value}.");
            return;
        }
        string old = VipDefaults.Label(playlist), oldName = playlist.Name;
        playlist.Name = dialog.Value;
        // Keep it open if it was.
        if (Data.Selected == PlaylistFilter + oldName) Data.Selected = PlaylistFilter + playlist.Name;
        _save();
        RebuildList();
        Say($"「{old}」を「{playlist.Name}」にしたよ。", $"Renamed {old} to {playlist.Name}.");
    }

    /// <summary>
    /// Copies a playlist as a code anyone can paste. Only games that mean the same on every PC go in: matsuri works and
    /// archive games, not folders or zips on this one.
    /// </summary>
    private async Task CopyPlaylistCodeAsync(VipPlaylist playlist)
    {
        CloseOverlay();
        var shared = playlist.Games.Where(key => !key.StartsWith("pc:", StringComparison.Ordinal) && !key.StartsWith("zip:", StringComparison.Ordinal)).ToList();
        string json = System.Text.Json.JsonSerializer.Serialize(new { n = VipDefaults.Label(playlist), g = shared });
        using var buffer = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(buffer, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(System.Text.Encoding.UTF8.GetBytes(json));
        string code = PlaylistCodePrefix + Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(code);
        int left = playlist.Games.Count - shared.Count;
        Say($"「{VipDefaults.Label(playlist)}」のコードをコピーしたよ。({shared.Count}本{(left > 0 ? $"、PCだけの{left}本は除外" : "")})",
            $"Copied the code for {VipDefaults.Label(playlist)}: {shared.Count} games{(left > 0 ? $"; {left} only on this PC were left out" : "")}. Paste it to share.");
    }

    /// <summary>
    /// Makes a playlist from a text file of titles, one per line (『』 and 「」 are fine), named after the file. Each title
    /// counts once, and a title several works share goes to the oldest; titles the launcher
    /// can't find stay in the list, marked.
    /// </summary>
    private async Task PlaylistFromTextAsync()
    {
        if (await PickAsync(folder: false, T("プレイリストにするテキスト (1行に1タイトル)", "Text File for the Playlist (one title per line)")) is not { } path) return;
        await PlaylistFromTextAsync(path);
    }

    private async Task PlaylistFromTextAsync(string path)
    {
        string[] lines;
        try
        {
            lines = TextFiles.Decode(await File.ReadAllBytesAsync(path)).Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say("ファイルが読めなかった… " + ex.Message, "Couldn't read the file: " + ex.Message);
            return;
        }
        var titles = lines.Select(line => line.Trim().Trim('『', '』', '「', '」', '"').Trim()).Where(line => line.Length > 0).ToList();

        // Candidates by title: matsuri works oldest first, then games outside the catalog.
        var rows = AllRows().Where(r => r.Title.Length > 0)
            .OrderBy(r => r.Work is null).ThenBy(r => r.Work?.Event?.Year ?? 9999).ThenBy(r => r.Work?.Matsuri, StringComparer.Ordinal)
            .ToList();
        var byTitle = rows.ToLookup(r => VipCatalog.TitleKey(r.Title));
        var games = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int found = 0;
        foreach (string title in titles)
        {
            string key = VipCatalog.TitleKey(title);
            // A title listed more than once is one entry.
            if (!seen.Add(key.Length > 0 ? key : title)) continue;
            var candidates = byTitle[key].ToList();
            // Else a title that's the start or part of a longer one ("キュアーⅠのデトックスラジオ" → "…2015").
            if (candidates.Count == 0 && key.Length >= 4)
                candidates = rows.Where(r => VipCatalog.TitleKey(r.Title) is { Length: >= 4 } other && (other.Contains(key) || key.Contains(other))).ToList();
            if (candidates.Count == 0)
            {
                games.Add(TitleKeyPrefix + title);
                continue;
            }
            // Every game the title fits (the same game in two matsuri, a remake, a thread copy), oldest first; a
            // loose match on part of a title stops at ten. Two titles that land on the same game are one entry.
            int added = 0;
            foreach (var game in candidates.Take(byTitle[key].Any() ? int.MaxValue : 10).Select(KeyOf))
            {
                if (games.Contains(game, StringComparer.OrdinalIgnoreCase)) continue;
                games.Add(game);
                added++;
            }
            if (added > 0) found++;
        }

        string name = Path.GetFileNameWithoutExtension(path), unique = name;
        for (int i = 2; Data.Playlists.Any(p => p.Name == unique); i++) unique = $"{name} ({i})";
        Data.Playlists.Add(new VipPlaylist { Name = unique, Games = games });
        _save();
        RebuildList();
        int missing = games.Count(g => g.StartsWith(TitleKeyPrefix, StringComparison.Ordinal));
        Say($"プレイリスト「{unique}」を作ったよ。{titles.Count}タイトル中{found}タイトルが見つかって、{games.Count - missing}本入れた。見つからないのは赤で表示。",
            $"Made the playlist {unique}: {found} of {titles.Count} titles found, {games.Count - missing} games added; the rest are kept in red as !!!!.");
    }

    /// <summary>Adds a playlist from a pasted code, under a new name if one of that name exists.</summary>
    private async Task ImportPlaylistAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        var dialog = new TextPromptDialog(T("プレイリストを読み込む", "Import a Playlist"), T("コードを貼り付けてね:", "Paste a playlist code:"), "", T("読み込む", "Import"));
        if (!await dialog.ShowAsync(window) || dialog.Value.Length == 0) return;
        try
        {
            string text = dialog.Value.Trim();
            if (!text.StartsWith(PlaylistCodePrefix, StringComparison.Ordinal)) throw new FormatException();
            string base64 = text[PlaylistCodePrefix.Length..].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            using var input = new System.IO.Compression.DeflateStream(new MemoryStream(Convert.FromBase64String(base64)),
                System.IO.Compression.CompressionMode.Decompress);
            using var json = System.Text.Json.JsonDocument.Parse(input);
            string name = json.RootElement.GetProperty("n").GetString() is { Length: > 0 } n ? n : T("プレイリスト", "Playlist");
            var games = json.RootElement.GetProperty("g").EnumerateArray().Select(e => e.GetString()).OfType<string>()
                .Where(key => key.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string unique = name;
            for (int i = 2; Data.Playlists.Any(p => p.Name == unique); i++) unique = $"{name} ({i})";
            Data.Playlists.Add(new VipPlaylist { Name = unique, Games = games });
            _save();
            RebuildList();
            Say($"プレイリスト「{unique}」を読み込んだよ。({games.Count}本)", $"Added the playlist {unique}: {games.Count} games.");
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            Say("そのコードは読めなかった…", "That isn't a playlist code (they start with " + PlaylistCodePrefix + ").");
        }
    }

    /// <summary>Rebuilds the side list and rows, keeping the selection.</summary>
    private void RebuildList()
    {
        int row = _rowIndex, top = _rowTop;
        RebuildSide();
        _rowIndex = Math.Clamp(row, 0, Math.Max(0, _rows.Count - 1));
        _rowTop = Math.Clamp(top, 0, Math.Max(0, _rows.Count - 1));
    }

    /// <summary>The right-click menu for a game: play or get it, favorite it, put it in groups, and its links.</summary>
    private void OpenMenu(Row row, Point at)
    {
        var items = new List<VipMenu.Item>();
        if (row.Local is { } game)
        {
            items.Add(new(T("遊ぶ", "Play"), () => Play(row)));
            if (EngineFamilies.HasTestMode(game.Family)) items.Add(new(T("テストプレイ", "Test Play"), () => Play(row, testPlay: true)));
        }
        else if (row.Zip is { } zip) items.Add(new(T("展開", "Extract"), () => _ = ExtractAsync(zip)));
        else if (row.Remote is { } remote) items.Add(new(T("ダウンロード", "Download"), () => _ = DownloadAsync(remote)));
        bool favorite = IsFavorite(row);
        items.Add(new(favorite ? T("お気に入りから外す", "Remove from Favorites") : T("お気に入りに追加", "Add to Favorites"), () => ToggleFavorite(row), Checked: favorite));
        bool hiddenGame = IsHidden(row);
        items.Add(new(hiddenGame ? T("非表示をやめる", "Unhide") : T("非表示にする", "Hide"), () => ToggleHidden(row), Checked: hiddenGame));
        bool completed = IsCompleted(row);
        items.Add(new(completed ? T("クリア済みを外す", "Unmark Completed") : T("クリア済みにする", "Mark Completed"), () => ToggleCompleted(row), Checked: completed));
        // The game's own commands, then its groups and playlists, which can run long.
        if (row.Title.Length > 0 && row.Missing is null) items.Add(new(T("タイトルをコピー", "Copy Title"), () => _ = CopyTitleAsync(row)));
        // Not while that author's own list is showing.
        if (CatalogAuthor(row) is { } author && !(Data.List == VipListMode.Author && Data.Selected == AuthorLabel(author)))
            items.Add(new(T($"{AuthorLabel(author)}の作品を見る", $"More by {AuthorLabel(author)}"), () => ShowAuthor(row)));
        if (row.Local is { } local) items.Add(new(T("フォルダを開く", "Open Folder"), () => OpenFolder(local.RootPath)));
        if (row.Work?.Downloads?.FirstOrDefault() is { } original) items.Add(new(T("配布元を開く", "Open Download Page"), () => OpenUrl(original)));
        if (row.Work?.WikiUrl is { } wiki) items.Add(new("wiki", () => OpenUrl(wiki)));
        if (Data.Groups.Count > 0) items.Add(new(T("グループ", "Groups"), null));
        foreach (string group in Data.Groups)
        {
            string name = group;
            items.Add(new("  " + name, () => ToggleGroup(row, name), Checked: InGroup(row, name), RightClick: () => _ = DeleteGroupAsync(name)));
        }
        items.Add(new(T("新しいグループ…", "New Group…"), () => _ = NewGroupAsync(row)));
        if (Data.Playlists.Count > 0) items.Add(new(T("プレイリスト", "Playlists"), null));
        foreach (var playlist in Data.Playlists)
        {
            var list = playlist;
            items.Add(new("  ♪ " + VipDefaults.Label(list), () => TogglePlaylist(row, list), Checked: InPlaylist(row, list)));
        }
        items.Add(new(T("新しいプレイリスト…", "New Playlist…"), () => _ = NewPlaylistAsync(row)));
        _overlay = new VipMenu(at, items, CloseOverlay);
        Redraw();
    }

    /// <summary>Copies a game's title to the clipboard.</summary>
    private async Task CopyTitleAsync(Row row)
    {
        CloseOverlay();
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(row.Title);
        Say($"「{row.Title}」をコピーしたよ。", $"Copied the title: {row.Title}");
    }

    /// <summary>A game's author as the 作者別 list has them, or null when that list doesn't (no author, not a catalog work).</summary>
    private string? CatalogAuthor(Row row)
    {
        string? author = (row.Work?.Author ?? row.Remote?.Author)?.Trim();
        if (string.IsNullOrEmpty(author)) return null;
        return row.Work is not null || _catalog.Games.Any(w => string.Equals(w.Author?.Trim(), author, StringComparison.OrdinalIgnoreCase))
            ? author : null;
    }

    /// <summary>Opens 作者別 at a game's author, with that game picked in the list.</summary>
    private void ShowAuthor(Row row)
    {
        CloseOverlay();
        if (CatalogAuthor(row) is not { } author) return;
        string key = KeyOf(row);
        Data.List = VipListMode.Author;
        Data.Selected = AuthorLabel(author);
        _sideQuery = "";
        if (_searchSide) SetSearchText("");
        _save();
        RebuildSide();
        int index = _rows.FindIndex(r => string.Equals(KeyOf(r), key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _rowIndex = index;
            _rowTop = Math.Max(0, index - 3);
        }
        _focus = Pane.Rows;
        Redraw();
        var item = _sideIndex < _side.Count ? _side[_sideIndex] : null;
        Say($"{AuthorLabel(author)}の作品は{item?.Total ?? _rows.Count}本だよ。", $"{AuthorLabel(author)}: {item?.Total ?? _rows.Count} works.");
    }

    // ---- linked authors ----

    // The author picked with ほかの作者とまとめる…, waiting for the one to link it with.
    private string? _linking;

    /// <summary>The linked authors an author belongs to, or null.</summary>
    private List<string>? LinkOf(string author) =>
        Data.AuthorLinks.FirstOrDefault(link => link.Contains(author, StringComparer.OrdinalIgnoreCase));

    /// <summary>An author as lists show it: "A + B" when linked with others, else the name itself.</summary>
    private string AuthorLabel(string? author)
    {
        string name = author?.Trim() ?? "";
        return name.Length > 0 && LinkOf(name) is { } link ? string.Join(" + ", link) : name;
    }

    /// <summary>The names behind an author list entry: the linked authors, or just the one.</summary>
    private List<string> AuthorsOf(string label) =>
        Data.AuthorLinks.FirstOrDefault(link => string.Equals(string.Join(" + ", link), label, StringComparison.OrdinalIgnoreCase))
        ?? new List<string> { label };

    /// <summary>The right-click menu for an author: link with another (two steps), or unlink.</summary>
    private void OpenAuthorMenu(string label, Point at)
    {
        var items = new List<VipMenu.Item>();
        if (_linking is { } pending && !string.Equals(pending, label, StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new(T($"「{RetroDraw.Fit(pending, 140)}」とまとめる", $"Link with {RetroDraw.Fit(pending, 140)}"), () => LinkAuthors(pending, label)));
            items.Add(new(T("まとめるのをやめる", "Cancel Linking"), () =>
            {
                _linking = null;
                Say("まとめるのをやめたよ。", "Linking cancelled.");
            }));
        }
        else
        {
            items.Add(new(T("ほかの作者とまとめる…", "Link with Another Author…"), () =>
            {
                _linking = label;
                Say($"「{label}」とまとめる作者を右クリックしてね。",
                    $"Now right-click the author to link with {label}: they'll show as one.");
            }));
        }
        if (AuthorsOf(label).Count > 1) items.Add(new(T("まとめを解除", "Unlink Authors"), () => UnlinkAuthors(label)));
        items.Add(PinItem("author:" + label));
        _overlay = new VipMenu(at, items, CloseOverlay);
        Redraw();
    }

    private void LinkAuthors(string first, string second)
    {
        var names = AuthorsOf(first).Concat(AuthorsOf(second)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Data.AuthorLinks.RemoveAll(link => link.Any(name => names.Contains(name, StringComparer.OrdinalIgnoreCase)));
        Data.AuthorLinks.Add(names);
        _linking = null;
        string label = string.Join(" + ", names);
        RenamePin(new[] { first, second }, label);
        Data.Selected = label;
        _save();
        RebuildSide();
        Say($"「{label}」としてまとめたよ。", $"Linked: {label} now show as one author.");
    }

    private void UnlinkAuthors(string label)
    {
        var names = AuthorsOf(label);
        Data.AuthorLinks.RemoveAll(link => string.Equals(string.Join(" + ", link), label, StringComparison.OrdinalIgnoreCase));
        RenamePin(new[] { label }, names[0]);
        if (string.Equals(Data.Selected, label, StringComparison.OrdinalIgnoreCase)) Data.Selected = names[0];
        _save();
        RebuildSide();
        Say($"「{label}」のまとめを解除したよ。", $"Unlinked: {string.Join(", ", names)} are separate again.");
    }

    /// <summary>Moves an author's pin to the entry it became (linked or unlinked), keeping its place.</summary>
    private void RenamePin(IEnumerable<string> from, string to)
    {
        var keys = from.Select(name => "author:" + name).ToList();
        int at = Data.Pinned.FindIndex(keys.Contains);
        if (at < 0) return;
        Data.Pinned.RemoveAll(keys.Contains);
        Data.Pinned.Insert(Math.Min(at, Data.Pinned.Count), "author:" + to);
    }

    /// <summary>The pin key of a side-list entry: its matsuri or author; null for anything else.</summary>
    private static string? PinKey(SideItem item) =>
        item.Matsuri is { } matsuri ? "matsuri:" + matsuri.Id : item.Author is { } author ? "author:" + author : null;

    /// <summary>上に固定 / 固定を外す for a matsuri or author.</summary>
    private VipMenu.Item PinItem(string key)
    {
        bool pinned = Data.Pinned.Contains(key);
        return new(pinned ? T("固定を外す", "Unpin") : T("上に固定", "Pin to Top"), () =>
        {
            if (pinned) Data.Pinned.Remove(key);
            else Data.Pinned.Add(key);
            _save();
            CloseOverlay();
            RebuildSide();
            Say(pinned ? "固定を外したよ。" : "一覧のいちばん上に固定したよ。", pinned ? "Unpinned." : "Pinned to the top of the list.");
        }, Checked: pinned);
    }

    /// <summary>Opens the right-click menu of a side-list line (author, matsuri, playlist or group); false when it has none.</summary>
    private bool OpenSideMenu(SideItem item, Point at)
    {
        if (item.Author is { } author)
            OpenAuthorMenu(author, at);
        else if (item.Matsuri is { } matsuri)
        {
            _overlay = new VipMenu(at, new List<VipMenu.Item> { PinItem("matsuri:" + matsuri.Id) }, CloseOverlay);
            Redraw();
        }
        else if (item.Filter is { } list && list.StartsWith(PlaylistFilter, StringComparison.Ordinal))
            OpenPlaylistMenu(list[PlaylistFilter.Length..], at);
        else if (item.Filter is { } filter && filter.StartsWith("group:", StringComparison.Ordinal))
            OpenGroupMenu(filter["group:".Length..], at);
        else
            return false;
        return true;
    }

    /// <summary>The right-click menu for a group in the side list.</summary>
    private void OpenGroupMenu(string group, Point at)
    {
        _overlay = new VipMenu(at, new List<VipMenu.Item>
        {
            new(T("新しいグループ…", "New Group…"), () => _ = NewGroupAsync(null)),
            new(T($"「{group}」を消す", $"Delete {group}"), () => DeleteGroup(group))
        }, CloseOverlay);
        Redraw();
    }

    private Row RowFor(VipWork work, string? no) => new(work, work.Local, no ?? "", work.Title, AuthorLabel(work.Author), work.LocalZip,
        VipServer.WorkKey(work.Matsuri, work.No, work.Title) is { } key ? _remoteByWork.GetValueOrDefault(key) : null);

    /// <summary>A server game the catalog doesn't list, such as one from a thread.</summary>
    private Row RowFor(VipServerGame remote) =>
        new(null, _remoteLocal.GetValueOrDefault(remote.Path), remote.Year?.ToString() ?? "", remote.DisplayTitle, remote.Author ?? "",
            _remoteLocal.ContainsKey(remote.Path) ? null : _remoteZip.GetValueOrDefault(remote.Path), Remote: remote);

    private static string Short(VipMatsuri? matsuri) => matsuri?.ShortName ?? "";

    private Row? SelectedRow => _rowIndex >= 0 && _rowIndex < _rows.Count ? _rows[_rowIndex] : null;

    // ---- actions ----

    /// <summary>Puts a line in the banner's message area; only the chosen language's is shown.</summary>
    private void Say(string japanese, string english)
    {
        _say = japanese;
        _sayEn = english;
        Diagnostics.Doing(english);
        Redraw();
    }

    private void SelectSide(int index)
    {
        if (_side.Count == 0) return;
        _sideIndex = Math.Clamp(index, 0, _side.Count - 1);
        if (HasFilterMenu)
        {
            ChooseSortFilter(_side[_sideIndex]);
            return;
        }
        Data.Selected = SideKey(_side[_sideIndex]);
        _save();
        _rowIndex = _rowTop = 0;
        ClearPicked();
        RebuildRows();
        var item = _side[_sideIndex];
        if (item.Matsuri is { } matsuri)
            Say($"{matsuri.Name}。参加作品は{item.Total}本、手持ちは{item.Owned}本だ。",
                $"{matsuri.Name}: {item.Total} works, {item.Owned} on this PC.");
        else if (item.Author is { } author)
            Say($"{author}さんの作品は{item.Total}本。", $"{author}: {item.Total} works.");
    }

    private void SelectRow(int index)
    {
        if (_rows.Count == 0) return;
        _rowIndex = Math.Clamp(index, 0, _rows.Count - 1);
        // The list follows the selection here, when it moves; the wheel scrolls without moving it.
        if (_rowIndex < _rowTop) _rowTop = _rowIndex;
        if (_rowIndex >= _rowTop + _rowsVisible) _rowTop = _rowIndex - _rowsVisible + 1;
        Redraw();
    }

    private void Play(Row row, bool testPlay = false)
    {
        if (row.Local is not { } game)
        {
            if (row.Zip is { } zip)
                Say("この作品はzipのままだ。「展開」で遊べるようになるよ。",
                    $"This one is still zipped ({Path.GetFileName(zip)}): press Extract to unpack it.");
            else if (row.Remote is { } remote)
                _ = DownloadAsync(remote);
            else if (row.Work?.WikiUrl is not null)
                Say("この作品は手持ちにないよ。wikiから探してみよう。",
                    "Not on this PC. Its wiki page may say where to get it.");
            else
                Say("この作品は手持ちにないよ。", "Not on this PC.");
            return;
        }

        Process? process;
        string? problem = null;
        try
        {
            if (game.Family == EngineFamily.Rm2k)
            {
                process = Rm2kLauncher.Start(game, _store.Data.Rm2k, testPlay, _runtimeSearch(), out problem);
            }
            else if (game.ExecutablePath is { } exe && File.Exists(exe))
            {
                string? arguments = testPlay ? game.Family switch { EngineFamily.Xp => "debug", EngineFamily.Vx => "test", _ => null } : null;
                if (game.Family == EngineFamily.Wolf) VipWolf.SetScreenshotKey(exe, Data.WolfScreenshotKey);
                process = Process.Start(Shell.GameStartInfo(exe, game.RootPath, arguments));
            }
            else
            {
                process = null;
                problem = T("起動できるプログラムが見つからない。", "No program to start the game was found.");
            }
        }
        catch (Exception ex)
        {
            process = null;
            problem = ex.Message;
        }

        if (process is null)
        {
            Say("起動できなかった… " + (problem ?? "").Replace('\n', ' '), (problem ?? "The game didn't start.").Replace('\n', ' '));
            return;
        }

        if (!Data.Games.TryGetValue(game.RootPath, out var record)) Data.Games[game.RootPath] = record = new VipRecord();
        record.PlayCount++;
        record.LastPlayedUtc = DateTime.UtcNow;
        _save();
        _running[game.RootPath] = _running.GetValueOrDefault(game.RootPath) + 1;
        Say($"「{row.Title}」を起動したぞ！{(testPlay ? " (テストプレイ)" : "")}",
            $"Starting {row.Title}{(testPlay ? " in test play (Ctrl walks through walls, F9 opens the debug menu)" : "")}.");

        var stopwatch = Stopwatch.StartNew();
        string folder = game.RootPath;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            record.PlaySeconds += (long)stopwatch.Elapsed.TotalSeconds;
            _running[folder] = Math.Max(0, _running.GetValueOrDefault(folder) - 1);
            _save();
            Say($"おかえり！ 「{row.Title}」を{Look.FormatDuration(Math.Max(1, (long)stopwatch.Elapsed.TotalSeconds))}遊んだよ。",
                $"Welcome back! You played {row.Title} for {Look.FormatDuration(Math.Max(1, (long)stopwatch.Elapsed.TotalSeconds))}.");
        });
    }

    private async Task ExtractAsync(string zip)
    {
        if (_extracting) return;
        _extracting = true;
        Say($"「{Path.GetFileName(zip)}」を展開しているよ…", $"Extracting {Path.GetFileName(zip)}…");
        try
        {
            string folder = await Task.Run(() => VipZip.Extract(zip));
            await Task.Run(() => VipZip.ExtractInner(folder));
            Say("展開できた！ フォルダを調べなおすよ。", $"Extracted to {folder}. Rescanning…");
            await ScanAsync();
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Zip not extracted {zip}: {ex}");
            Say("展開できなかった… " + ex.Message, "Couldn't extract it: " + ex.Message);
        }
        finally
        {
            _extracting = false;
        }
    }

    /// <summary>
    /// Downloads a game from the server into the download folder, unpacks it there and rescans. Pressing the
    /// button again while it runs stops it.
    /// </summary>
    private async Task DownloadAsync(VipServerGame remote)
    {
        if (_downloading is not null)
        {
            if (ReferenceEquals(_downloading, remote)) _downloadCancel?.Cancel();
            else Say("いまは別の作品をダウンロード中だよ。", $"Already downloading {_downloading.DisplayTitle}; one at a time.");
            return;
        }
        string? server = Server;
        if (remote.Url is null && server is null)
        {
            Say("ダウンロードはまだ準備中だよ。", "Downloads aren't available in this version of the launcher.");
            return;
        }
        bool counted = !remote.Original && remote.Url is null; // the server's own file, under its limits
        if (counted && VipServer.BlockedUntil is { } until)
        {
            Say($"サーバーが一時停止中だよ。{until:HH:mm}まで待ってね。",
                $"The download server paused this connection for too many requests; try again at {until:HH:mm}.");
            return;
        }
        if (DownloadRoot() is not { } root) return;

        // Matsuri works go in a folder per matsuri, named so the matcher would also know them: 2005-kouhaku\01.zip.
        // Other uploads go in a folder per uploader area, under their own names.
        string extension = remote.Original ? ".download" : Path.GetExtension(remote.Path).ToLowerInvariant();
        string zip = remote.Matsuri is { Length: > 0 } matsuri && remote.No is { Length: > 0 } no
            ? Path.Combine(root, SafeName(matsuri), SafeName(no) + extension)
            : remote.Area is { Length: > 0 } area
                ? Path.Combine(root, SafeName(area), SafeName(remote.DisplayTitle) + extension)
                : Path.Combine(root, "thread", SafeName(Path.GetFileName(remote.Path)));
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            // The zip and its unpacked copy, with some room to spare.
            if (remote.Size > 0 && drive.IsReady && drive.AvailableFreeSpace < remote.Size * 3)
            {
                Say("ディスクの空きが足りないよ。", $"Not enough free space on {drive.Name} for {Look.FormatBytes(remote.Size)} (and unpacking it).");
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // a drive that can't be asked about is tried anyway
        }

        _downloading = remote;
        _downloaded = 0;
        _downloadCancel = new CancellationTokenSource();
        var cancel = _downloadCancel.Token;
        string title = remote.DisplayTitle;
        Say($"「{title}」をダウンロードしているよ…", $"Downloading {title}… press Stop to stop.");
        var shown = Stopwatch.StartNew();
        void Progress(long received)
        {
            if (shown.ElapsedMilliseconds < 250) return;
            shown.Restart();
            Dispatcher.UIThread.Post(() =>
            {
                _downloaded = received;
                string amount = remote.Size > 0 ? $"{received * 100 / remote.Size}%" : Look.FormatBytes(received);
                Say($"「{title}」をダウンロードしているよ… {amount}",
                    $"Downloading {title}… {Look.FormatBytes(received)}{(remote.Size > 0 ? " of " + Look.FormatBytes(remote.Size) : "")}");
            });
        }

        try
        {
            string? resolved = null;
            if (remote.Original)
            {
                Say($"「{title}」の配布元に問い合わせているよ…", $"Asking {new Uri(remote.Url!).Host} for {title}…");
                string? name = null;
                // An uploader's password is asked for, and remembered for its other files until the launcher closes.
                string? password = remote.Area is { } uploader ? _uploaderPasswords.GetValueOrDefault(uploader) : null;
                while (resolved is null)
                {
                    try
                    {
                        (resolved, name) = await VipOriginal.ResolveAsync(remote.Url!, cancel, password);
                    }
                    catch (PasswordRequiredException needed)
                    {
                        if (TopLevel.GetTopLevel(this) is not Window window) throw;
                        var dialog = new TextPromptDialog(T("ダウンロードパスワード", "Download Password"),
                            needed.Wrong ? T("パスワードが違うみたい。もう一度:", "Wrong password; try again:")
                                : T("このファイルにはパスワードが要るよ。祭りのページやスレに書いてあることが多い。", "This file needs a password. It is usually on the festival page or in the thread."),
                            "", T("ダウンロード", "Download"));
                        if (!await dialog.ShowAsync(window) || dialog.Value.Length == 0)
                        {
                            Say("パスワードがないとダウンロードできない…", $"Not downloaded: {title} needs its download password.");
                            return;
                        }
                        password = dialog.Value;
                    }
                }
                if (password is not null && remote.Area is { } known) _uploaderPasswords[known] = password;
                Diagnostics.Log($"VIPRPG original {remote.Url} -> {name ?? resolved}");
            }
            await Task.Run(() => VipServer.DownloadAsync(server ?? "", remote, zip, Progress, cancel, resolved), cancel);
            if (remote.Original)
            {
                // Original links don't say what they hold until it's here: named by the file's own signature.
                extension = VipOriginal.SniffExtension(zip);
                string named = Path.ChangeExtension(zip, extension);
                File.Move(zip, named, overwrite: true);
                zip = named;
            }
            if (extension != ".zip")
            {
                // .rar, .7z, .lzh and .exe downloads are left for the user to open.
                Say($"ダウンロードできた！ {extension}は自分で展開してからF5を押してね。" + QuotaNote(counted, japanese: true),
                    $"Downloaded {title} as {extension}: extract or run it yourself, then press F5." + QuotaNote(counted, japanese: false));
                Shell.ShowInFileManager(zip);
                if (counted) await WarnQuotaAsync();
                return;
            }
            Say("ダウンロードできた！ 展開しているよ…", $"Downloaded {title}. Unpacking…");
            string folder = await Task.Run(() => VipZip.Extract(zip));
            Data.Downloaded[Path.GetFullPath(folder)] = remote.WorkKey ?? remote.Path;
            // Some uploads hold the game zipped again, beside a readme.
            await Task.Run(() => VipZip.ExtractInner(folder));
            _save();
            try { File.Delete(zip); }
            catch (Exception ex) { Diagnostics.Log($"Downloaded zip kept {zip}: {ex.Message}"); }
            await ScanAsync();
            Say($"「{title}」が手持ちに加わった！ Enterで遊べるよ。" + QuotaNote(counted, japanese: true),
                $"{title} is ready: Enter plays it." + QuotaNote(counted, japanese: false));
            if (counted) await WarnQuotaAsync();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Say("ダウンロードをやめたよ。", $"Stopped downloading {title}.");
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"VIPRPG download failed {remote.Url ?? remote.Path}: {ex}");
            var (japanese, english) = VipServer.Explain(ex);
            if (remote.Original && ex is NotSupportedException or InvalidDataException)
                (japanese, english) = ("配布元から直接は取れなかった。「配布元」で開いてみてね。", ex.Message + " Try Source to open the page.");
            Say(japanese, english);
            if (ex is VipLimitException limit) await WarnLimitAsync(limit, japanese, english);
        }
        finally
        {
            _downloading = null;
            _downloadCancel.Dispose();
            _downloadCancel = null;
            Redraw();
        }
    }

    /// <summary>Downloads left today, from this many down, are mentioned after each download.</summary>
    private const int QuotaNoteBelow = 50;

    // The popups already shown, by server day and what they warned of, so each shows once.
    private readonly HashSet<string> _quotaWarned = new();

    /// <summary>" (12 of today's 500 downloads left)" once few are left, else "".</summary>
    private static string QuotaNote(bool counted, bool japanese)
    {
        if (!counted || VipServer.Quota is not { } quota || quota.Left > QuotaNoteBelow) return "";
        if (quota.Left == 0)
            return japanese ? $"（今日のダウンロードはこれで最後。{quota.Reset.ToLocalTime():HH:mm}にリセット）"
                : $" That was today's last download ({quota.Limit:N0} a day); more after {quota.Reset.ToLocalTime():HH:mm}.";
        return japanese ? $"（今日はあと{quota.Left}本ダウンロードできるよ）"
            : $" ({quota.Left} of today's {quota.Limit:N0} downloads left; resets at {quota.Reset.ToLocalTime():HH:mm}.)";
    }

    /// <summary>A message box the first time today's downloads run low, and again when they run out.</summary>
    private async Task WarnQuotaAsync()
    {
        if (VipServer.Quota is not { } quota || quota.Left > QuotaNoteBelow) return;
        string day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        bool last = quota.Left == 0;
        if (!_quotaWarned.Add(day + (last ? "/out" : "/low"))) return;
        string resets = quota.Reset.ToLocalTime().ToString("HH:mm");
        string text = last
            ? T($"今日のダウンロード上限（{quota.Limit:N0}本）に達したよ。{resets}（UTCの0時）にリセットされるまで、新しい作品はダウンロードできない。今日もうダウンロードした作品はもう一度ダウンロードできるよ。",
                $"That was the last of today's {quota.Limit:N0} downloads from the VIPRPG server. New games can be downloaded again after {resets} " +
                "(midnight UTC). Games already downloaded today can still be downloaded again.")
            : T($"今日のダウンロードはあと{quota.Left}本（1日{quota.Limit:N0}本まで）。{resets}にリセットされるよ。",
                $"Only {quota.Left} of today's {quota.Limit:N0} downloads from the VIPRPG server are left. " +
                $"The count resets at {resets} (midnight UTC); downloading a game you already downloaded today doesn't count again.");
        await MessageDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, text, T("ダウンロード上限", "Download Limit"));
    }

    /// <summary>A message box when the server refuses a download over a limit (the daily one once a day).</summary>
    private async Task WarnLimitAsync(VipLimitException limit, string japanese, string english)
    {
        if (limit.Daily && !_quotaWarned.Add(DateTime.UtcNow.ToString("yyyy-MM-dd") + "/out")) return;
        await MessageDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window, T(japanese, english), T("ダウンロード上限", "Download Limit"));
    }

    /// <summary>The download folder, made if needed and added to the scanned folders; null if it can't be written.</summary>
    private string? DownloadRoot()
    {
        string root = Data.DownloadFolder ?? _defaultDownloads;
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say("ダウンロード先に書き込めない…", $"Can't write to {root}: {ex.Message}. Choose another folder under Settings → Folders → Download Folder.");
            return null;
        }
        // Downloads have to be somewhere the scan looks, or they'd never show as owned.
        string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        bool scanned = Data.Directories.Any(dir =>
        {
            string top = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(full, top, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
        if (!scanned) Data.Directories.Add(root);
        _save();
        return root;
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        safe = safe.Trim().TrimEnd('.');
        return safe is "" or "." or ".." ? "_" : safe;
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return;
        try
        {
            Shell.OpenUrl(uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Link not opened: " + ex.Message);
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Shell.OpenFolder(path);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Folder not opened: " + ex.Message);
        }
    }

    /// <summary>Lets the settings overlay add a game folder.</summary>
    public void AddFolder() => _ = AddFolderAsync();

    private async Task AddFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("VIPRPGのゲームが入ったフォルダ", "Folder with VIPRPG Games"),
            AllowMultiple = false
        });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
        if (Data.Directories.Any(existing => string.Equals(Path.GetFullPath(existing), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
        {
            Say("そのフォルダはもう追加済みだよ。", "That folder is already added.");
            return;
        }
        Data.Directories.Add(path);
        _save();
        await ScanAsync();
    }

    private void ShowSettings()
    {
        _overlay = new VipSettings(_store.Data.Rm2k, Data, _save, () => _ = ScanAsync(), this, () =>
        {
            CloseOverlay();
            ModeRequested?.Invoke(LauncherMode.Hrpg);
        });
        Redraw();
    }

    /// <summary>Lets the settings overlay pick a folder or file without knowing about the window.</summary>
    public async Task<string?> PickAsync(bool folder, string title)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        if (folder)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public void CloseOverlay()
    {
        _overlay = null;
        Redraw();
    }

    // ---- painting ----

    private Rect _listRect, _detailRect, _bannerRect;

    protected override void Paint(DrawingContext g, Size size)
    {
        _hot.Clear();
        _rowAreas.Clear();
        _rowWords.Clear();
        _sideAreas.Clear();
        _sideWords.Clear();
        double w = size.Width, h = size.Height;
        // Colours follow the matsuri opened, not the cursor passing over the list.
        VipTheme theme = VipTheme.For(_side.FirstOrDefault(item => Data.Selected is { } key && SideKey(item) == key)?.Matsuri);

        // The page behind everything: the dark tiled background of an old fan site.
        g.FillRectangle(theme.PageBrush, new Rect(0, 0, w, h));

        _bannerRect = new Rect(0, 0, w, 44);
        PaintBanner(g, _bannerRect, theme);

        // Half the screen for the list, half for the selected work.
        double top = _bannerRect.Bottom + 4, bottom = h - 4, half = Math.Floor(w / 2);
        _listRect = new Rect(4, top, half - 6, bottom - top);
        _detailRect = new Rect(half + 2, top, w - half - 6, bottom - top);
        _searchRect = default;

        switch (_focus)
        {
            case Pane.Commands: PaintCommands(g, theme); break;
            case Pane.Side: PaintSide(g, theme); break;
            default: PaintRows(g, theme); break;
        }
        PaintDetail(g, theme);

        _overlay?.Paint(g, size, _tick, _hot, _mouse);
    }

    private void PaintBanner(DrawingContext g, Rect rect, VipTheme theme)
    {
        theme.PaintBanner(g, rect, _tick);
        double logo = RetroDraw.Logo(g, "VIPRPG", rect.X + 12, rect.Y + 8, RetroDraw.Large, theme.Title, theme.LogoHighlight, theme.LogoOutline);
        logo += RetroDraw.Logo(g, T("ランチャー", "Launcher"), rect.X + 20 + logo, rect.Y + 8, RetroDraw.Large, theme.LogoSecond, theme.LogoHighlight, theme.LogoOutline);

        // Tools at the right, as buttons.
        double bx = rect.Right - 8;
        for (int i = _tools.Count - 1; i >= 0; i--)
        {
            var tool = _tools[i];
            double width = RetroDraw.Width(tool.Label) + 16;
            bx -= width;
            var button = new Rect(bx, rect.Y + 13, width, 18);
            bool hover = button.Contains(_mouse) && _overlay is null;
            RetroDraw.Fill(g, button, hover ? Color.FromArgb(110, 255, 255, 255) : Color.FromArgb(130, 0, 0, 0));
            RetroDraw.Outline(g, button, hover ? Colors.White : Color.FromArgb(170, 255, 255, 255));
            RetroDraw.Text(g, tool.Label, button.X + 8, button.Y + 3, RetroDraw.TextColor);
            _hot.Add(new VipHot(button, tool.Run, tool.HintEn));
            bx -= 4;
        }

        // The latest message between the title and the buttons.
        double mx = rect.X + 32 + logo, width2 = bx - mx - 8;
        if (width2 > 40)
        {
            RetroDraw.Text(g, RetroDraw.Fit(T(_say, _sayEn), width2), mx, rect.Y + 15, RetroDraw.TextColor);
        }
    }

    /// <summary>A page's heading, with a back arrow when there's a page to go back to.</summary>
    private void PaintHeading(DrawingContext g, string text, double width, bool back)
    {
        double x = _listRect.X + 8, y = _listRect.Y + 6;
        if (back)
        {
            var arrow = new Rect(x - 2, y - 2, 18, 16);
            bool hover = arrow.Contains(_mouse) && _overlay is null;
            if (hover) RetroDraw.Fill(g, arrow, Color.FromArgb(70, 255, 255, 255));
            RetroDraw.Text(g, "◀", x, y, hover ? Colors.White : RetroDraw.Yellow);
            _hot.Add(new VipHot(arrow, Back));
            x += 18;
            width -= 18;
        }
        RetroDraw.Text(g, RetroDraw.Fit(text, width), x, y, RetroDraw.System);
    }

    private void PaintCommands(DrawingContext g, VipTheme theme)
    {
        RetroDraw.Window(g, _listRect, theme.WindowTop, theme.WindowBottom);
        PaintHeading(g, T("一覧の種類", "Lists"), _listRect.Width - 16, back: false);
        Rect RowOf(int i) => new(_listRect.X + 6, _listRect.Y + 26 + i * 36, _listRect.Width - 12, 34);
        // One cursor, moved by the mouse and the keys alike: pointing at a list type selects it, as ↑↓ do. Nothing
        // looks chosen until something is pointed at or a key is pressed.
        int hovered = _overlay is null ? Enumerable.Range(0, _commands.Count).FirstOrDefault(i => RowOf(i).Contains(_mouse), -1) : -1;
        if (hovered >= 0 && !_keys) _command = hovered;
        bool shown = _keys || hovered >= 0;
        for (int i = 0; i < _commands.Count; i++)
        {
            var command = _commands[i];
            var row = RowOf(i);
            if (shown && i == _command) RetroDraw.Cursor(g, row, _tick);
            RetroDraw.Text(g, "  " + command.Label, row.X + 4, row.Y + 3, RetroDraw.TextColor);
            RetroDraw.Text(g, RetroDraw.Fit(command.Hint, row.Width - 24), row.X + 20, row.Y + 18, RetroDraw.System);
            int index = i;
            _hot.Add(new VipHot(row, () =>
            {
                _command = index;
                command.Run();
            }, command.HintEn));
        }
    }

    private void PaintSide(DrawingContext g, VipTheme theme)
    {
        RetroDraw.Window(g, _listRect, theme.WindowTop, theme.WindowBottom);
        string heading = Data.List switch
        {
            VipListMode.Matsuri => T("祭り別：祭りを選んでね", "By Festival: pick a festival"),
            VipListMode.Author => T("作者別：作者を選んでね", "By Author: pick an author"),
            VipListMode.Series => T("シリーズ別：シリーズを選んでね（発表順）", "Series: pick a series (in release order)"),
            VipListMode.Playlist => T("プレイリスト：選ぶか作ってね", "Playlists: pick or make one"),
            VipListMode.All => T("全作品：絞り込みを選んでね", "All Games: pick what to show"),
            VipListMode.Storage => T("保管庫：保管庫を選んでね", "Thread Storage: pick an archive"),
            VipListMode.Wolf => T("WOLF RPG：ウディコンを選んでね", "WOLF RPG: pick a contest"),
            _ => T("手持ち：並べ替えか絞り込みを選んでね", "On This PC: pick a sort or filter")
        };
        double headingWidth = _listRect.Width - 16;
        if (Data.List == VipListMode.Author)
        {
            // The author list has its own search box, at the right of the heading like the works page's.
            double searchWidth = Math.Min(200, Math.Floor(_listRect.Width * 0.42));
            _searchRect = new Rect(_listRect.Right - 8 - searchWidth, _listRect.Y + 5, searchWidth, 15);
            headingWidth -= searchWidth + 8;
        }
        PaintHeading(g, heading, headingWidth, back: true);
        var list = new Rect(_listRect.X + 6, _listRect.Y + 22, _listRect.Width - 12, _listRect.Height - 30);
        _sideVisible = Math.Max(1, (int)(list.Height / 16));
        // A scroll bar at the right when the list is longer than the window.
        _sideBar = default;
        if (_side.Count > _sideVisible)
        {
            _sideBar = new Rect(list.Right - ScrollBarWidth, list.Y, ScrollBarWidth, _sideVisible * 16);
            list = list.WithWidth(list.Width - ScrollBarWidth - 2);
        }
        _sideTop = Math.Clamp(_sideTop, 0, Math.Max(0, _side.Count - _sideVisible));
        if (_sideIndex < _sideTop) _sideTop = _sideIndex;
        if (_sideIndex >= _sideTop + _sideVisible) _sideTop = _sideIndex - _sideVisible + 1;

        using (g.PushClip(list))
        {
            for (int i = _sideTop; i < Math.Min(_side.Count, _sideTop + _sideVisible); i++)
            {
                var item = _side[i];
                var row = new Rect(list.X, list.Y + (i - _sideTop) * 16, list.Width, 16);
                _sideAreas.Add(row);
                // Where this line's words are: like a game line, a right-click anywhere else on it goes back
                // (unless 設定 → 空いた所を右クリック says to open its menu).
                var words = new List<Rect>();
                _sideWords.Add(words);
                void Words(double from, double width)
                {
                    if (width > 0) words.Add(new Rect(from - 2, row.Y, width + 4, row.Height));
                }
                if (i == _sideIndex && _keys) RetroDraw.Cursor(g, row, _tick, _focus == Pane.Side);
                else if (row.Contains(_mouse) && _overlay is null) RetroDraw.Fill(g, row.Deflate(1), Color.FromArgb(40, 255, 255, 255));
                if (item.Total == Heading)
                {
                    Words(row.X + 3, RetroDraw.Text(g, item.Label, row.X + 3, row.Y + 2, RetroDraw.System));
                    continue;
                }
                bool chosen = item.Filter is { } choice && (choice == Data.OwnedSort || choice == Data.OwnedFilter);
                string count = item.Total == NoCount ? "" : item.Strays || Data.List == VipListMode.Owned ? $"{item.Total}" : $"{item.Owned}/{item.Total}";
                double countWidth = RetroDraw.Width(count);
                bool isNew = item.Matsuri?.Year >= DateTime.Now.Year - 1 && _tick / 8 % 2 == 0;
                string label = RetroDraw.Fit(item.Label, row.Width - countWidth - (isNew ? 30 : 10));
                if (PinKey(item) is { } pin && Data.Pinned.Contains(pin)) label = "◎" + label;
                double labelWidth = RetroDraw.Text(g, (chosen ? "▶" : "") + label, row.X + 3, row.Y + 2,
                    RetroDraw.TextColor);
                if (isNew) labelWidth += 3 + RetroDraw.Text(g, "NEW", row.X + 6 + labelWidth, row.Y + 2, RetroDraw.Red);
                Words(row.X + 3, labelWidth);
                Words(row.Right - countWidth - 3, RetroDraw.Text(g, count, row.Right - countWidth - 3, row.Y + 2, RetroDraw.TextColor));
                int index = i;
                _hot.Add(new VipHot(row, () => OpenSide(index)));
            }
        }
        if (_side.Count == 0 && _sideQuery.Length > 0)
            RetroDraw.Text(g, T("見つからない", "No matches"), list.X + 8, list.Y + 4, RetroDraw.Disabled);
        if (_sideTop > 0) RetroDraw.MoreArrow(g, _listRect.Center.X, _listRect.Y + 16, _tick, down: false);
        if (_sideTop + _sideVisible < _side.Count) RetroDraw.MoreArrow(g, _listRect.Center.X, _listRect.Bottom - 7, _tick);
        PaintScrollBar(g, _sideBar, _sideTop, _sideVisible, _side.Count, Pane.Side);
    }

    private const double ScrollBarWidth = 6;

    /// <summary>Where a scroll bar's thumb is: its size is the share of the list in view.</summary>
    private static Rect ThumbOf(Rect bar, int top, int visible, int count)
    {
        double height = Math.Max(10, Math.Floor(bar.Height * visible / Math.Max(1, count)));
        int range = Math.Max(1, count - visible);
        return new Rect(bar.X, bar.Y + Math.Round((bar.Height - height) * Math.Clamp(top, 0, range) / range), bar.Width, height);
    }

    private void PaintScrollBar(DrawingContext g, Rect bar, int top, int visible, int count, Pane pane)
    {
        if (bar.Width <= 0) return;
        RetroDraw.Fill(g, bar, Color.FromArgb(90, 0, 0, 0));
        var thumb = ThumbOf(bar, top, visible, count);
        bool hot = _dragging == pane || (bar.Contains(_mouse) && _overlay is null);
        RetroDraw.Fill(g, thumb, hot ? Color.FromArgb(230, 255, 255, 255) : Color.FromArgb(150, 200, 210, 255));
    }

    /// <summary>The list's first line for a thumb dragged to this height.</summary>
    private static int TopFor(Rect bar, double thumbY, int visible, int count)
    {
        var thumb = ThumbOf(bar, 0, visible, count);
        double travel = bar.Height - thumb.Height;
        int range = Math.Max(0, count - visible);
        return travel <= 0 ? 0 : (int)Math.Round(Math.Clamp((thumbY - bar.Y) / travel, 0, 1) * range);
    }

    private void PaintRows(DrawingContext g, VipTheme theme)
    {
        RetroDraw.Window(g, _listRect, theme.WindowTop, theme.WindowBottom);
        var item = _sideIndex < _side.Count ? _side[_sideIndex] : null;
        string heading = HasFilterMenu
            ? (Data.List == VipListMode.All ? T("全作品", "All Games") : T("手持ち", "On This PC")) + $"（{SortLabel}・{FilterLabel}）"
            : item?.Filter is { } shown && shown.StartsWith(PlaylistFilter, StringComparison.Ordinal) ? T("プレイリスト：", "Playlist: ") + shown[PlaylistFilter.Length..]
            : item?.Filter is { } series && series.StartsWith(SeriesFilter, StringComparison.Ordinal) ? T("シリーズ：", "Series: ") + item.Label + T("（発表順）", " (release order)")
            : item?.Matsuri is { } matsuri ? matsuri.Name + T("　参加作品一覧", " — entries")
            : item is { Wodicon: > 0 } ? T($"第{item.Wodicon}回ウディコン", $"WOLF RPG Contest {item.Wodicon}")
            : item?.Author is { } author ? T($"{author}さんの作品", $"Works by {author}")
            : item?.Strays == true ? T("その他のゲーム（wikiにない作品）", "Other Games (not on the wiki)")
            : item?.Server == true ? (item.Area == "*server" ? T("サーバーの作品（スレ作品など）", "Server Games (thread games and more)")
                : item.Label + T("（アップローダー保存庫）", " (Uploader Archive)"))
            : T("手持ち", "On This PC") + $"（{OwnedSorts.FirstOrDefault(sort => sort.Key == Data.OwnedSort).Label ?? T("タイトル順", "By Title")}・{_side.FirstOrDefault(i => i.Filter == Data.OwnedFilter)?.Label.TrimStart('★', '■', ' ') ?? T("すべて", "All")}）";
        // The search box sits at the right of the heading.
        double searchWidth = Math.Min(200, Math.Floor(_listRect.Width * 0.42));
        _searchRect = new Rect(_listRect.Right - 8 - searchWidth, _listRect.Y + 5, searchWidth, 15);
        double headingWidth = _listRect.Width - 24 - searchWidth;
        if (HasFilterMenu)
        {
            // The menu button, between the heading and the search box.
            string label = T("並べ替え・絞り込み▼", "Sort / Filter ▼");
            double buttonWidth = RetroDraw.Width(label) + 10;
            var button = new Rect(_searchRect.X - buttonWidth - 6, _listRect.Y + 4, buttonWidth, 17);
            bool hover = button.Contains(_mouse) && _overlay is null;
            RetroDraw.Fill(g, button, hover ? Color.FromArgb(110, 255, 255, 255) : Color.FromArgb(110, 0, 0, 0));
            RetroDraw.Outline(g, button, hover ? Colors.White : Color.FromArgb(160, 255, 255, 255));
            RetroDraw.Text(g, label, button.X + 5, button.Y + 2, RetroDraw.Yellow);
            _hot.Add(new VipHot(button, () => OpenFilterMenu(new Point(button.X, button.Bottom + 2))));
            headingWidth -= buttonWidth + 6;
        }
        // A matsuri or playlist shows how much of it is completed, at the right of the heading.
        bool playlist = item?.Filter?.StartsWith(PlaylistFilter, StringComparison.Ordinal) == true;
        if (Data.List != VipListMode.Owned && (item?.Matsuri is not null || item?.Wodicon > 0 || playlist) && _completedCount.Total > 0)
        {
            string done = T("クリア済み", "Completed:") + $" {_completedCount.Done}/{_completedCount.Total}";
            double doneWidth = RetroDraw.Width(done);
            RetroDraw.Text(g, done, _searchRect.X - doneWidth - 8, _listRect.Y + 6, RetroDraw.System);
            headingWidth -= doneWidth + 8;
        }
        PaintHeading(g, heading, headingWidth, back: true);

        double x = _listRect.X + 6, width = _listRect.Width - 12;
        // 保管庫's games have no festival or author to show: the title gets the whole line.
        bool storage = Data.List == VipListMode.Storage;
        // シリーズ別 numbers its games in release order: #1 is the series' first game.
        bool numbered = Data.List is VipListMode.Matsuri or VipListMode.Wolf or VipListMode.Series;
        double noWidth = storage ? 0 : numbered ? 32 : Math.Min(110, width * 0.22);
        double stateWidth = 22, authorWidth = Data.List == VipListMode.Author || storage ? 0 : Math.Min(130, width * 0.28);
        double titleWidth = width - noWidth - stateWidth - authorWidth - 12;
        double headerY = _listRect.Y + 22;
        RetroDraw.Fill(g, new Rect(x, headerY + 15, width, 1), Color.FromArgb(120, 200, 210, 255));
        if (!storage) RetroDraw.Text(g, numbered ? "No." : T("祭り", "Festival"), x + 3, headerY + 1, RetroDraw.System);
        RetroDraw.Text(g, T("タイトル", "Title"), x + noWidth + stateWidth + 3, headerY + 1, RetroDraw.System);
        if (authorWidth > 0) RetroDraw.Text(g, T("作者", "Author"), x + width - authorWidth, headerY + 1, RetroDraw.System);

        var list = new Rect(x, headerY + 18, width, _listRect.Bottom - headerY - 26);
        _rowsVisible = Math.Max(1, (int)(list.Height / 16));
        _rowsBar = default;
        if (_rows.Count > _rowsVisible)
        {
            _rowsBar = new Rect(list.Right - ScrollBarWidth, list.Y, ScrollBarWidth, _rowsVisible * 16);
            list = list.WithWidth(list.Width - ScrollBarWidth - 2);
        }
        _rowTop = Math.Clamp(_rowTop, 0, Math.Max(0, _rows.Count - _rowsVisible));

        if (_rows.Count == 0)
        {
            string empty = _scanning ? T("調べています…", "Scanning…") : T("作品がありません", "No games");
            RetroDraw.Text(g, empty, list.X + 8, list.Y + 4, RetroDraw.Disabled);
        }

        using (g.PushClip(list))
        {
            for (int i = _rowTop; i < Math.Min(_rows.Count, _rowTop + _rowsVisible); i++)
            {
                var row = _rows[i];
                var rect = new Rect(list.X, list.Y + (i - _rowTop) * 16, list.Width, 16);
                _rowAreas.Add(rect);
                bool picked = _picked.Count > 0 && _picked.Contains(KeyOf(row));
                if (i == _rowIndex) RetroDraw.Cursor(g, rect, _tick, _focus == Pane.Rows);
                // A picked game is tinted inside the cursor too, so the cursor never hides that it is picked.
                if (picked) RetroDraw.Fill(g, rect.Deflate(i == _rowIndex ? 2 : 1), Color.FromArgb(90, 255, 224, 64));
                else if (i != _rowIndex && rect.Contains(_mouse) && _overlay is null) RetroDraw.Fill(g, rect.Deflate(1), Color.FromArgb(40, 255, 255, 255));

                bool owned = row.Local is not null;
                bool running = owned && _running.GetValueOrDefault(row.Local!.RootPath) > 0;
                // One colour whether the game is here or not (the state mark says which); red only for playlist
                // entries the launcher can't find at all.
                var color = row.Missing is not null ? RetroDraw.Red : RetroDraw.TextColor;
                // Where this row's words are: a right-click anywhere else on it goes back instead of opening its menu.
                var words = new List<Rect>();
                _rowWords.Add(words);
                void Words(double from, double width)
                {
                    if (width > 0) words.Add(new Rect(from - 2, rect.Y, width + 4, rect.Height));
                }
                if (noWidth > 0)
                    Words(rect.X + 3, RetroDraw.Text(g, RetroDraw.Fit(row.No, noWidth - 4), rect.X + 3, rect.Y + 2, color));
                bool fetching = row.Remote is not null && ReferenceEquals(row.Remote, _downloading);
                bool remote = !owned && row.Zip is null && row.Remote is not null;
                // エ: never submitted, so no download exists anywhere.
                bool eternal = !owned && row.Zip is null && !remote && row.Work?.Eternal == true;
                // ×: a WOLF RPG contest entry no copy of was found.
                string state = running ? "●" : owned ? "▶" : row.Zip is not null ? "Z" : fetching ? (_tick / 4 % 2 == 0 ? "↓" : "") : remote ? "↓"
                    : eternal ? T("エ", "E") : row.Lost ? "×" : "・";
                double stateX = rect.X + noWidth + 4;
                RetroDraw.Text(g, state, stateX, rect.Y + 2,
                    running ? RetroDraw.Red : owned ? RetroDraw.Green : row.Zip is not null ? RetroDraw.Yellow
                    : fetching ? RetroDraw.Yellow : remote ? RetroDraw.System : eternal || row.Lost ? RetroDraw.Red : RetroDraw.Disabled);
                double titleX = rect.X + noWidth + stateWidth + 3;
                if (IsFavorite(row)) titleX += RetroDraw.Text(g, "★", titleX, rect.Y + 2, RetroDraw.Yellow);
                if (IsCompleted(row)) titleX += RetroDraw.Text(g, T("（済）", "(✓)"), titleX, rect.Y + 2, RetroDraw.Green);
                double titleEnd = titleX + RetroDraw.Text(g, RetroDraw.Fit(row.Title, titleWidth - (titleX - rect.X - noWidth - stateWidth - 3)), titleX, rect.Y + 2, color);
                // The state mark through the end of the title counts as one stretch of words.
                Words(stateX, titleEnd - stateX);
                if (authorWidth > 0)
                    Words(rect.Right - authorWidth, RetroDraw.Text(g, RetroDraw.Fit(row.Author, authorWidth - 4), rect.Right - authorWidth, rect.Y + 2,
                        RetroDraw.TextColor));
                int index = i;
                _hot.Add(new VipHot(rect, () =>
                {
                    // Ctrl adds or takes out one game, Shift picks the games from the last one to this one.
                    if (_clickModifiers.HasFlag(KeyModifiers.Control))
                    {
                        TogglePick(index);
                        return;
                    }
                    if (_clickModifiers.HasFlag(KeyModifiers.Shift))
                    {
                        PickTo(index);
                        return;
                    }
                    // A plain click ends a multiple pick, and only moves the cursor.
                    if (_picked.Count > 0)
                    {
                        ClearPicked();
                        SelectRow(index);
                        return;
                    }
                    _pickAnchor = null;
                    // Clicking the chosen game again plays it, or starts downloading it when it isn't here yet.
                    if (_focus == Pane.Rows && _rowIndex == index)
                    {
                        if (row.Local is not null) Play(row);
                        else if (row.Zip is null && row.Remote is { } download && !ReferenceEquals(download, _downloading))
                            _ = DownloadAsync(download);
                    }
                    _focus = Pane.Rows;
                    SelectRow(index);
                }));
            }
        }
        // In the header line, between the column names, so it never covers the first game.
        if (_rowTop > 0) RetroDraw.MoreArrow(g, _listRect.Center.X, headerY + 8, _tick, down: false);
        if (_rowTop + _rowsVisible < _rows.Count) RetroDraw.MoreArrow(g, _listRect.Center.X, _listRect.Bottom - 7, _tick);
        PaintScrollBar(g, _rowsBar, _rowTop, _rowsVisible, _rows.Count, Pane.Rows);
    }

    private void PaintDetail(DrawingContext g, VipTheme theme)
    {
        RetroDraw.Window(g, _detailRect, theme.WindowTop, theme.WindowBottom);
        double x = _detailRect.X + 8, y = _detailRect.Y + 8, width = _detailRect.Width - 16;
        _pictureBitmap = null;
        // On the author list, the author under the pointer (or the keyboard cursor) previews their first game.
        bool preview = _focus == Pane.Side;
        var row = preview ? AuthorPreview() : _focus == Pane.Rows ? SelectedRow : null;
        if (row is null) return;

        // Buttons first, so the picture can take the room the text and buttons leave.
        // Buttons, like a shop's command window.
        var buttons = new List<(string Label, Action Run, bool Enabled)>();
        if (row.Local is { } owned)
        {
            buttons.Add((T("遊ぶ", "Play"), () => Play(row), true));
            if (EngineFamilies.HasTestMode(owned.Family)) buttons.Add((T("テスト", "Test"), () => Play(row, testPlay: true), true));
            buttons.Add((T("フォルダ", "Folder"), () => OpenFolder(owned.RootPath), true));
        }
        else if (row.Zip is { } zip)
        {
            buttons.Add((T("展開", "Extract"), () => _ = ExtractAsync(zip), true));
            buttons.Add((T("フォルダ", "Folder"), () => Shell.ShowInFileManager(zip), true));
        }
        else if (row.Remote is { } download)
        {
            buttons.Add((ReferenceEquals(download, _downloading) ? T("中止", "Stop") : T("ダウンロード", "Download"), () => _ = DownloadAsync(download), true));
        }
        if (row.Work?.Downloads?.FirstOrDefault() is { } original) buttons.Add((T("配布元", "Source"), () => OpenUrl(original), true));
        // The link the contest's entry list gave: maybe still up, for an entry the server has no copy of.
        if (row.Lost && row.Wolf?.Link is { } entryLink) buttons.Add((T("配布元", "Source"), () => OpenUrl(entryLink), true));
        if (row.Work?.WikiUrl is { } wiki) buttons.Add(("wiki", () => OpenUrl(wiki), true));
        if (row.Work?.ForumUrl is { } forum) buttons.Add((T("感想スレ", "Thread"), () => OpenUrl(forum), true));
        if (row.Work?.Event is { } eventInfo)
        {
            if (eventInfo.ArchiveUrl is { } archive) buttons.Add((T("祭りページ", "Festival Page"), () => OpenUrl(archive), true));
            if (eventInfo.SiteUrl is { } site) buttons.Add((T("公式サイト", "Website"), () => OpenUrl(site), true));
            else if (eventInfo.WikiUrl is { } matsuriWiki) buttons.Add((T("祭りwiki", "Festival Wiki"), () => OpenUrl(matsuriWiki), true));
        }

        // A preview is only to look at.
        if (preview) buttons.Clear();

        // The picture: 4:3, as large as leaves room for the title, six lines of text and the buttons.
        const int Columns = 4;
        int buttonRows = (buttons.Count + Columns - 1) / Columns;
        double room = _detailRect.Bottom - 8 - 18 * buttonRows - 4 - 6 - 16 - 15 * 6;
        double pictureWidth = Math.Floor(Math.Min(width, (room - y) * 4 / 3));
        var picture = new Rect(x + Math.Floor((width - pictureWidth) / 2), y, pictureWidth, Math.Floor(pictureWidth * 3 / 4));
        // The game's own title screen, else the picture from its matsuri's site.
        // The built-in picture when there is one, else the game's own title screen once it's on the PC.
        string? thumb = row.Work is { } pictured ? VipThumbs.KeyFor(pictured) : row.Wolf is { } entry ? VipThumbs.KeyFor(entry)
            : row.Remote is { } remote ? VipThumbs.KeyFor(remote) : null;
        var bitmap = VipThumbs.Has(thumb) ? ThumbFor(thumb!)
            : row.Local is { } game ? TitleFor(game) : null;
        _pictureBitmap = bitmap;
        _pictureRect = picture;
        if (bitmap is not null)
        {
            // Just a frame around the picture as it will be shown (see PlacePicture), with no box behind it.
            var (shownWidth, shownHeight) = PictureSize(bitmap, picture.Width * Zoom, picture.Height * Zoom);
            var shown = new Rect(picture.Center.X - shownWidth / Zoom / 2, picture.Center.Y - shownHeight / Zoom / 2,
                shownWidth / Zoom, shownHeight / Zoom);
            RetroDraw.Fill(g, RetroDraw.Snap(shown).Inflate(1), RetroDraw.FrameOuter);
        }
        else
        {
            RetroDraw.Fill(g, picture.Inflate(1), RetroDraw.FrameOuter);
            RetroDraw.Fill(g, picture, Colors.Black);
            string text = row.Local is not null ? "NO IMAGE" : T("NO IMAGE（未所持）", "NO IMAGE (not on this PC)");
            RetroDraw.Text(g, text, picture.Center.X - RetroDraw.Width(text) / 2, picture.Center.Y - 6, RetroDraw.Disabled);
        }
        y = picture.Bottom + 6;

        // Lines that would run into the buttons are left out.
        double limit = _detailRect.Bottom - 8 - 18 * buttonRows - 4;
        double Add(string label, string value) => y + 15 > limit ? 0 : Line(g, label, value, x, y, width);
        string state = row.Local is not null ? (IsCompleted(row) ? T("  [クリア済み]", "  [completed]") : "") : row.Zip is not null ? "  [zip]" : row.Remote is not null ? T("  [未所持 ↓]", "  [not on this PC ↓]")
            : row.Work?.Eternal == true ? T("  [エターナル（未提出）]", "  [eternal (never submitted)]")
            : row.Lost ? T("  [× 見つからなかった作品]", "  [× couldn't be found]")
            : row.Missing is not null ? T("  [ランチャーに見つからない]", "  [not found by the launcher]") : T("  [未所持]", "  [not on this PC]");
        double titleWidth = RetroDraw.Text(g, RetroDraw.Fit(row.Title, width - RetroDraw.Width(state)), x, y, RetroDraw.TextColor);
        RetroDraw.Text(g, state, x + titleWidth, y, row.Remote is not null && row.Local is null ? RetroDraw.System : RetroDraw.Disabled);
        y += 16;
        if (row.Work is { } work)
        {
            if (!string.IsNullOrWhiteSpace(work.Author)) y += Add(T("作者", "Author"), work.Author!);
            if (work.Event is { } matsuri)
                y += Add(T("祭り", "Festival"), matsuri.Name + (string.IsNullOrWhiteSpace(work.No) ? "" : $"  No.{work.No}"));
            if (!string.IsNullOrWhiteSpace(work.Genre)) y += Add(T("ジャンル", "Genre"), work.Genre!);
        }
        else if (row.Wolf is { } wolf)
        {
            if (!string.IsNullOrWhiteSpace(wolf.Author)) y += Add(T("作者", "Author"), wolf.Author);
            y += Add(T("コンテスト", "Contest"), T($"第{wolf.Contest}回ウディコン（{wolf.Year}）  No.{wolf.No}", $"WOLF RPG Editor Contest {wolf.Contest} ({wolf.Year})  No.{wolf.No}"));
            // A game on this PC gets its engine line from the scan, below.
            if (row.Local is null) y += Add(T("ツール", "Engine"), "WOLF RPGエディター");
        }
        else if (row.Remote is { } info)
        {
            if (!string.IsNullOrWhiteSpace(info.Author)) y += Add(T("作者", "Author"), info.Author!);
            if (!string.IsNullOrWhiteSpace(info.Thread)) y += Add(T("スレ", "Thread"), info.Thread!);
            if (info.Year is { } year) y += Add(T("年", "Year"), year.ToString());
            // A copy on this PC gets its engine line from the scan, below.
            if (!string.IsNullOrWhiteSpace(info.Engine) && row.Local is null) y += Add(T("ツール", "Engine"), info.Engine!);
            if (!string.IsNullOrWhiteSpace(info.Note)) y += Add(T("メモ", "Note"), info.Note!);
        }
        if (row.Local is { } local)
        {
            y += Add(T("ツール", "Engine"), local.Engine);
            if (Data.Games.TryGetValue(local.RootPath, out var record) && record.PlayCount > 0)
                y += Add(T("プレイ", "Played"), T($"{record.PlayCount}回", $"{record.PlayCount}×") + $" ／ {Look.FormatDuration(Math.Max(1, record.PlaySeconds))}");
        }
        // サイズ: on disk for a game on this PC or a zip, else the download's (what the server or archive lists).
        string size = row.Local is { } onDisk ? SizeText(SizeOf(onDisk.RootPath, folder: true))
            : row.Zip is { } zipFile ? SizeText(SizeOf(zipFile, folder: false))
            : row.Remote is { } server ? ReferenceEquals(server, _downloading) && server.Size > 0
                ? $"{Look.FormatBytes(_downloaded)} / {Look.FormatBytes(server.Size)}"
                : server.Size > 0 ? Look.FormatBytes(server.Size) : T("ダウンロードするまで分からない", "Unknown until downloaded")
            : "—";
        y += Add(T("サイズ", "Size"), size);


        double bx = x, by = Math.Max(y + 4, _detailRect.Bottom - 8 - 18 * buttonRows);
        double buttonWidth = Math.Floor((width - 4 * (Columns - 1)) / Columns);
        for (int i = 0; i < buttons.Count; i++)
        {
            var rect = new Rect(bx + i % Columns * (buttonWidth + 4), by + i / Columns * 18, buttonWidth, 16);
            bool hover = rect.Contains(_mouse) && _overlay is null;
            RetroDraw.Fill(g, rect, hover ? Color.FromArgb(80, 255, 255, 255) : Color.FromArgb(90, 0, 0, 0));
            RetroDraw.Outline(g, rect, hover ? Colors.White : Color.FromArgb(150, 255, 255, 255));
            var (label, run, _) = buttons[i];
            bool main = i == 0 && (row.Local is not null || row.Zip is null && row.Remote is not null);
            RetroDraw.Text(g, label, rect.Center.X - RetroDraw.Width(label) / 2, rect.Y + 2, main ? RetroDraw.Yellow : RetroDraw.TextColor);
            _hot.Add(new VipHot(rect, run));
        }
    }

    /// <summary>The first game of the author under the pointer, or under the keyboard cursor; null off the author list.</summary>
    private Row? AuthorPreview()
    {
        if (Data.List != VipListMode.Author || _overlay is not null) return null;
        int hovered = _sideAreas.FindIndex(area => area.Contains(_mouse));
        int index = hovered >= 0 ? _sideTop + hovered : _keys ? _sideIndex : -1;
        if (index < 0 || index >= _side.Count || _side[index].Author is not { } author) return null;
        if (!_authorFirst.TryGetValue(author, out var first))
        {
            // The same first game the author's own page lists.
            first = _catalog.Games.FirstOrDefault(w => string.Equals(AuthorLabel(w.Author), author, StringComparison.OrdinalIgnoreCase))
                is { } work ? RowFor(work, Short(work.Event)) : null;
            _authorFirst[author] = first;
        }
        return first;
    }

    private static string SizeText(long? bytes) => bytes is not { } known ? T("計算中…", "Adding up…") : known < 0 ? "?" : Look.FormatBytes(known);

    /// <summary>A game folder's or a file's size on disk; null while it's being added up in the background.</summary>
    private long? SizeOf(string path, bool folder)
    {
        if (_sizes.TryGetValue(path, out long known)) return known;
        if (_sizesLoading.Add(path))
        {
            Task.Run(() =>
            {
                long total = 0;
                try
                {
                    if (!folder) total = new FileInfo(path).Length;
                    else
                    {
                        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options)) total += file.Length;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diagnostics.Log($"Size not read {path}: {ex.Message}");
                    total = -1;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    _sizes[path] = total;
                    _sizesLoading.Remove(path);
                    Redraw();
                });
            });
        }
        return null;
    }

    private static double Line(DrawingContext g, string label, string value, double x, double y, double width)
    {
        double labelWidth = RetroDraw.Text(g, label, x, y, RetroDraw.System) + 8;
        RetroDraw.Text(g, RetroDraw.Fit(value, width - Math.Max(labelWidth, 56)), x + Math.Max(labelWidth, 56), y, RetroDraw.TextColor);
        return 15;
    }

    /// <summary>A game's title screen, loaded in the background on first use.</summary>
    private Bitmap? TitleFor(GameEntry game) => Picture(game.RootPath, () =>
    {
        using var image = GameImageLoader.LoadSourceImage(game);
        // The RTP's stock skies and books say nothing about the game.
        if (image is null || game.Family == EngineFamily.Rm2k && Rm2kStockTitles.IsStock(image)) return null;
        return ImageKit.ToAvalonia(image);
    });

    /// <summary>A picture from a matsuri's or contest's site (built into the launcher), loaded on first use.</summary>
    private Bitmap? ThumbFor(string key) =>
        VipThumbs.Has(key) ? Picture("thumb:" + key, () => VipThumbs.Load(key)) : null;

    private Bitmap? Picture(string id, Func<Bitmap?> load)
    {
        if (_titles.TryGetValue(id, out var cached)) return cached;
        if (_titlesLoading.Add(id))
        {
            Task.Run(() =>
            {
                Bitmap? bitmap = null;
                try { bitmap = load(); }
                catch (Exception ex) { Diagnostics.Log("VIPRPG picture not loaded: " + ex.Message); }
                Dispatcher.UIThread.Post(() =>
                {
                    _titles[id] = bitmap;
                    if (_titles.Count > 200)
                    {
                        foreach (var key in _titles.Keys.Where(k => k != id).Take(50).ToList())
                        {
                            _titles[key]?.Dispose();
                            _titles.Remove(key);
                            _titlesLoading.Remove(key);
                        }
                    }
                    Redraw();
                });
            });
        }
        return null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // Place the search box and the picture over their spots on the canvas once this frame is drawn.
        Dispatcher.UIThread.Post(PlaceControls, DispatcherPriority.Background);
    }

    private void PlaceControls()
    {
        _search.IsVisible = _searchRect.Width > 0;
        if (_searchRect.Width > 0)
        {
            bool side = _focus == Pane.Side;
            if (side != _searchSide)
            {
                _searchSide = side;
                SetSearchText(side ? _sideQuery : _query);
                _search.Watermark = side ? T("作者を検索 (Ctrl+F)", "Search Authors (Ctrl+F)") : T("検索 (Ctrl+F)", "Search (Ctrl+F)");
            }
            var rect = FromCanvas(_searchRect);
            var margin = new Thickness(Math.Round(rect.X), Math.Round(rect.Y), 0, 0);
            if (_search.Margin != margin) _search.Margin = margin;
            if (Differs(_search.Width, rect.Width)) _search.Width = rect.Width;
            if (Differs(_search.Height, rect.Height)) _search.Height = rect.Height;
            double font = Math.Max(9, rect.Height * 0.72);
            if (Math.Abs(_search.FontSize - font) > 0.5) _search.FontSize = font;
        }
        PlacePicture();
    }

    /// <summary>Sets the search box's text without searching again.</summary>
    private void SetSearchText(string text)
    {
        _syncingSearch = true;
        _search.Text = text;
        _syncingSearch = false;
    }

    // A size not yet set is NaN, which no comparison would catch.
    private static bool Differs(double current, double wanted) => double.IsNaN(current) || Math.Abs(current - wanted) > 0.01;

    /// <summary>
    /// The picture over its box at the screen's resolution: pixel art (320×240 or smaller) by whole steps with
    /// hard edges, anything larger smoothly scaled to fit.
    /// </summary>
    private static bool IsPixelArt(Bitmap bitmap) => bitmap.PixelSize.Width <= 320 && bitmap.PixelSize.Height <= 240;

    /// <summary>A picture's size in device pixels inside a box: pixel art by whole steps, anything else to fit.</summary>
    private static (double Width, double Height) PictureSize(Bitmap bitmap, double boxWidth, double boxHeight)
    {
        double sourceWidth = bitmap.PixelSize.Width, sourceHeight = bitmap.PixelSize.Height;
        double fit = Math.Min(boxWidth / sourceWidth, boxHeight / sourceHeight);
        if (IsPixelArt(bitmap) && fit >= 1) fit = Math.Floor(fit);
        return (Math.Round(sourceWidth * fit), Math.Round(sourceHeight * fit));
    }

    private void PlacePicture()
    {
        if (_pictureBitmap is not { } bitmap || _overlay is not null || _pictureRect.Width <= 0)
        {
            _picture.IsVisible = false;
            return;
        }
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var box = FromCanvas(_pictureRect);
        double boxWidth = box.Width * scaling, boxHeight = box.Height * scaling;
        var (deviceWidth, deviceHeight) = PictureSize(bitmap, boxWidth, boxHeight);
        bool pixelArt = IsPixelArt(bitmap);
        double width = deviceWidth / scaling, height = deviceHeight / scaling;
        var margin = new Thickness(Math.Round((box.X + (box.Width - width) / 2) * scaling) / scaling,
            Math.Round((box.Y + (box.Height - height) / 2) * scaling) / scaling, 0, 0);
        if (!ReferenceEquals(_picture.Source, bitmap)) _picture.Source = bitmap;
        RenderOptions.SetBitmapInterpolationMode(_picture, pixelArt ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        if (_picture.Margin != margin) _picture.Margin = margin;
        if (Differs(_picture.Width, width)) _picture.Width = width;
        if (Differs(_picture.Height, height)) _picture.Height = height;
        _picture.IsVisible = true;
    }

    // ---- input ----

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointerView = e.GetPosition(this);
        var mouse = ToCanvas(_pointerView);
        if (mouse != _mouse) _keys = false;
        _mouse = mouse;
        if (_dragging is { } pane) DragTo(pane, mouse.Y);
        Redraw();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging is null) return;
        _dragging = null;
        e.Pointer.Capture(null);
        Redraw();
    }

    /// <summary>Scrolls a list to where its thumb is dragged, without changing what's selected.</summary>
    private void DragTo(Pane pane, double y)
    {
        if (pane == Pane.Side) _sideTop = TopFor(_sideBar, y - _dragOffset, _sideVisible, _side.Count);
        else _rowTop = TopFor(_rowsBar, y - _dragOffset, _rowsVisible, _rows.Count);
        KeepCursorsVisible();
    }

    /// <summary>A press on a scroll bar: grabs the thumb, or pages toward the press.</summary>
    private bool PressScrollBar(Point point, PointerPressedEventArgs e)
    {
        foreach (var (pane, bar) in new[] { (Pane.Side, _sideBar), (Pane.Rows, _rowsBar) })
        {
            if (bar.Width <= 0 || pane != _focus || !bar.Inflate(new Thickness(2, 0)).Contains(point)) continue;
            bool side = pane == Pane.Side;
            int top = side ? _sideTop : _rowTop, visible = side ? _sideVisible : _rowsVisible, count = side ? _side.Count : _rows.Count;
            var thumb = ThumbOf(bar, top, visible, count);
            if (point.Y >= thumb.Y && point.Y < thumb.Bottom)
            {
                _dragging = pane;
                _dragOffset = point.Y - thumb.Y;
                e.Pointer.Capture(this);
            }
            else
            {
                top += point.Y < thumb.Y ? -visible : visible;
                if (side) _sideTop = Math.Max(0, top);
                else _rowTop = Math.Max(0, top);
                KeepCursorsVisible();
            }
            return true;
        }
        return false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _mouse = new Point(-100, -100);
        Redraw();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = ToCanvas(e.GetPosition(this));
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            e.Handled = true;
            if (_overlay is VipMenu menu && menu.RightClickAt(point)) return;
            if (_overlay is null)
            {
                // The same on every list: a line's words open its menu; the blank part of the line goes back, or
                // opens the menu too when 設定 → 空いた所を右クリック says so.
                bool OnLine(List<List<Rect>> words, int index) =>
                    Data.RightClickBlankOpensMenu || (index < words.Count && words[index].Any(rect => rect.Contains(point)));
                int row = _rowAreas.FindIndex(area => area.Contains(point));
                if (row >= 0 && OnLine(_rowWords, row))
                {
                    _focus = Pane.Rows;
                    // On one of several picked games: the menu for all of them. Anywhere else the pick ends.
                    if (_picked.Count > 1 && _rowTop + row < _rows.Count && _picked.Contains(KeyOf(_rows[_rowTop + row])))
                    {
                        OpenPickedMenu(_rows.Where(r => _picked.Contains(KeyOf(r))).ToList(), point);
                        return;
                    }
                    ClearPicked();
                    SelectRow(_rowTop + row);
                    if (SelectedRow is { } selected) OpenMenu(selected, point);
                    return;
                }
                int side = _sideAreas.FindIndex(area => area.Contains(point));
                if (side >= 0 && _sideTop + side < _side.Count && OnLine(_sideWords, side) &&
                    OpenSideMenu(_side[_sideTop + side], point))
                    return;
            }
            Back();
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        // A click outside an open menu only closes it; nothing under it is clicked.
        if (_overlay is not null && !_overlay.Contains(point))
        {
            CloseOverlay();
            e.Handled = true;
            return;
        }
        if (_overlay is null && PressScrollBar(point, e))
        {
            e.Handled = true;
            Redraw();
            return;
        }
        // Topmost first: the overlay's areas were added last.
        _clickModifiers = _overlay is null ? e.KeyModifiers : KeyModifiers.None;
        for (int i = _hot.Count - 1; i >= 0; i--)
        {
            if (!_hot[i].Area.Contains(point)) continue;
            _hot[i].Click();
            e.Handled = true;
            Redraw();
            return;
        }
        if (_overlay is not null && !_overlay.Contains(point)) CloseOverlay();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = ToCanvas(e.GetPosition(this));
        int steps = e.Delta.Y > 0 ? -3 : 3;
        if (_overlay is not null) _overlay.Scroll(steps);
        else if (_listRect.Contains(point) && _focus == Pane.Side) _sideTop = Math.Max(0, _sideTop + steps);
        else if (_listRect.Contains(point) && _focus == Pane.Rows) _rowTop = Math.Max(0, _rowTop + steps);
        else return;
        KeepCursorsVisible();
        e.Handled = true;
        Redraw();
    }

    /// <summary>After a wheel scroll: keeps the lists in range, and the matsuri list's keyboard cursor in view.</summary>
    private void KeepCursorsVisible()
    {
        _sideTop = Math.Clamp(_sideTop, 0, Math.Max(0, _side.Count - _sideVisible));
        _rowTop = Math.Clamp(_rowTop, 0, Math.Max(0, _rows.Count - _rowsVisible));
        // The matsuri list's cursor moves along without opening anything, so the list doesn't snap back to it.
        if (_side.Count > 0 && (_sideIndex < _sideTop || _sideIndex >= _sideTop + _sideVisible))
        {
            int index = Math.Clamp(_sideIndex, _sideTop, Math.Min(_side.Count, _sideTop + _sideVisible) - 1);
            while (index < _side.Count - 1 && _side[index].Total == Heading) index++;
            _sideIndex = index;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Shift, Ctrl and Alt alone move nothing; held down they repeat, which would flicker the keyboard cursor
        // against the mouse's.
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
            return;
        _keys = true;
        if (_overlay is not null)
        {
            if (!_overlay.OnKey(e.Key)) CloseOverlay();
            e.Handled = true;
            Redraw();
            return;
        }
        e.Handled = true;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            // Shift+↑↓ picks games from the cursor's on; Ctrl+A picks the whole list.
            case Key.Up when shift && _focus == Pane.Rows: PickTo(_rowIndex - 1); break;
            case Key.Down when shift && _focus == Pane.Rows: PickTo(_rowIndex + 1); break;
            case Key.A when control && _focus == Pane.Rows: PickAll(); break;
            case Key.Up: Move(-1); break;
            case Key.Down: Move(1); break;
            case Key.PageUp: Move(-Visible()); break;
            case Key.PageDown: Move(Visible()); break;
            case Key.Home: Move(-100000); break;
            case Key.End: Move(100000); break;
            case Key.Left: Back(); break;
            case Key.Right or Key.Tab when _focus != Pane.Rows: Confirm(); break;
            case Key.Enter or Key.Space or Key.Z: Confirm(); break;
            case Key.Escape or Key.X or Key.Back: Back(); break;
            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                // The search box belongs to the works page, and to the author list.
                if (!(_focus == Pane.Side && Data.List == VipListMode.Author)) _focus = Pane.Rows;
                Redraw();
                Dispatcher.UIThread.Post(() => _search.Focus(), DispatcherPriority.Background);
                break;
            case Key.S: ShowSettings(); break;
            case Key.F5:
                _ = ScanAsync();
                _ = RefreshServerAsync(quiet: true);
                break;
            case Key.D when SelectedRow is { Local: null, Zip: null, Remote: { } remote }: _ = DownloadAsync(remote); break;
            // Shortcuts for the selected work.
            case Key.T when SelectedRow is { Local: { } game } row && EngineFamilies.HasTestMode(game.Family): Play(row, testPlay: true); break;
            case Key.F when SelectedRow?.Local is { } local: OpenFolder(local.RootPath); break;
            case Key.W when SelectedRow?.Work?.WikiUrl is { } wiki: OpenUrl(wiki); break;
            default: e.Handled = false; return;
        }
        Redraw();
    }

    private int Visible() => _focus switch { Pane.Side => _sideVisible, Pane.Rows => _rowsVisible, _ => 1 };

    private void Move(int delta)
    {
        switch (_focus)
        {
            case Pane.Commands:
                _command = Math.Clamp(_command + delta, 0, _commands.Count - 1);
                break;
            case Pane.Side:
            {
                // Headings can't be chosen; the cursor steps over them.
                int target = Math.Clamp(_sideIndex + delta, 0, Math.Max(0, _side.Count - 1));
                int step = delta < 0 ? -1 : 1;
                while (target > 0 && target < _side.Count - 1 && _side[target].Total == Heading) target += step;
                if (target < _side.Count && _side[target].Total != Heading) SelectSide(target);
                break;
            }
            default:
                ClearPicked();
                SelectRow(_rowIndex + delta);
                break;
        }
    }

    private void Confirm()
    {
        switch (_focus)
        {
            case Pane.Commands:
                _commands[_command].Run();
                break;
            case Pane.Side:
                OpenSide(_sideIndex);
                break;
            default:
                if (SelectedRow is { } row) Play(row);
                break;
        }
    }

    private void Back()
    {
        if (_overlay is not null)
        {
            CloseOverlay();
            return;
        }
        // A multiple pick is let go of before going back a page.
        if (_picked.Count > 0 && _focus == Pane.Rows)
        {
            ClearPicked();
            Say("選ぶのをやめたよ。", "Selection cleared.");
            return;
        }
        // Up a page: works → their matsuri/author/filter → the list modes.
        _focus = _focus == Pane.Rows && !HasFilterMenu ? Pane.Side : Pane.Commands;
        if (_focus == Pane.Commands) _command = Math.Max(0, _commands.FindIndex(c => c.Mode == Data.List));
        Redraw();
    }
}
