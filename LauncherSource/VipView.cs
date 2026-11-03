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
        string? Area = null, string? Filter = null);

    private sealed record Row(VipWork? Work, GameEntry? Local, string No, string Title, string Author, string? Zip = null,
        VipServerGame? Remote = null, string? Missing = null);

    /// <summary>The download server: the R2 bucket's public address, where manifest.json is.</summary>
    private const string ServerAddress = "https://dl.viprpg-archive.com";

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
    private readonly Dictionary<string, int> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Command> _commands;

    // Rescan, add folder and settings: buttons at the top right, always there.
    private readonly List<Command> _tools;
    private readonly List<Rect> _rowAreas = new();
    private readonly List<Rect> _sideAreas = new();

    /// <summary>For each game line in <see cref="_rowAreas"/>, where its words were drawn.</summary>
    private readonly List<List<Rect>> _rowWords = new();

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
    private int _tick;
    private string _say = "", _sayEn = "";
    private VipOverlay? _overlay;
    private Point _mouse = new(-100, -100);

    // The download server's list: catalog works by "matsuri/no", and the games the catalog doesn't list.
    private readonly string _serverCachePath;
    private Dictionary<string, VipServerGame> _remoteByWork = new(StringComparer.OrdinalIgnoreCase);
    private List<VipServerGame> _remoteExtras = new();
    private Dictionary<string, GameEntry> _remoteLocal = new(StringComparer.OrdinalIgnoreCase);
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
            new("保管庫", "Thread Storage", "スレやwikiの保管庫、PCだけにある作品。", "Games from the thread and wiki storage uploaders, and games only on this PC.",
                () => SetList(VipListMode.Storage), VipListMode.Storage),
            new("プレイリスト", "Playlists", "自分で作った作品リスト。コードで共有もできるよ。", "Your own lists of games; share them as a code.",
                () => SetList(VipListMode.Playlist), VipListMode.Playlist)
        };
        _tools = new List<Command>
        {
            new("再スキャン", "Rescan", "フォルダを調べなおすよ。(F5)", "Look through the folders again. (F5)", () => _ = ScanAsync()),
            new("フォルダ追加", "Add Folder", "ゲームが入っているフォルダを選んでね。", "Add a folder that holds VIPRPG games.", () => _ = AddFolderAsync()),
            new("設定", "Settings", "起動方法、EasyRPG、フォルダ、モード切り替え。(S)", "Launch options, EasyRPG, folders, and the switch back to HRPG mode. (S)", ShowSettings)
        };
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
        _loadingCache = true;
        try
        {
            // The uploader archive's games (built into the launcher), then the download server's cached list.
            _archiveGames = await Task.Run(() => VipUploader.Load(_catalog).ToGames(_catalog));
            if (await Task.Run(() => VipServer.LoadCached(_serverCachePath)) is { } manifest) SetManifest(manifest);
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
        ApplyDownloads();
        // Zips no work claimed and nobody extracted yet: listed with the other games.
        var claimed = new HashSet<string>(_catalog.Games.Select(w => w.LocalZip).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        _strayZips = zips.Where(zip => !claimed.Contains(zip) && !VipZip.LooksExtracted(zip)).ToList();
        RebuildSide();
    }

    /// <summary>
    /// Games downloaded from the server are matched by the record kept when they were downloaded, as their folders
    /// are named however the zip had them.
    /// </summary>
    private void ApplyDownloads()
    {
        _remoteLocal = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        if (Data.Downloaded.Count == 0) return;
        var works = new Dictionary<string, VipWork>(StringComparer.OrdinalIgnoreCase);
        foreach (var work in _catalog.Games)
        {
            if (!string.IsNullOrWhiteSpace(work.No)) works.TryAdd(VipServer.Key(work.Matsuri, work.No!), work);
        }
        foreach (var game in _games)
        {
            string? key = DownloadKey(game.RootPath);
            if (key is null) continue;
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
    }

    /// <summary>The download server's address; RPGLAUNCHER_VIP_SERVER points it elsewhere for testing.</summary>
    private static string? Server => VipServer.Normalize(Environment.GetEnvironmentVariable("RPGLAUNCHER_VIP_SERVER") ?? ServerAddress);

    private void SetManifest(VipManifest manifest)
    {
        _serverManifest = manifest;
        ApplyRemote();
    }

    /// <summary>Downloads by work and the rest, from the uploader archive first and the server after.</summary>
    private void ApplyRemote()
    {
        var byWork = new Dictionary<string, VipServerGame>(StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(_catalog.Games.Where(w => !string.IsNullOrWhiteSpace(w.No))
            .Select(w => VipServer.Key(w.Matsuri, w.No!)), StringComparer.OrdinalIgnoreCase);
        var extras = new List<VipServerGame>();
        foreach (var game in _archiveGames.Concat(_serverManifest?.Games ?? new List<VipServerGame>()))
        {
            if (game.WorkKey is { } key && known.Contains(key)) byWork.TryAdd(key, game);
            else extras.Add(game);
        }
        // Works neither the archive nor the server has, but whose matsuri link is still one the launcher can fetch.
        foreach (var work in _catalog.Games)
        {
            if (string.IsNullOrWhiteSpace(work.No) || work.Downloads?.FirstOrDefault(VipOriginal.CanFetch) is not { } link) continue;
            string key = VipServer.Key(work.Matsuri, work.No!);
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
        // The server's own games (no area) always stay.
        _remoteExtras = extras.GroupBy(g => g.Area)
            .Where(area => area.Key is null || area.Count() >= StorageMinimum)
            .SelectMany(area => area)
            .OrderBy(g => g.DisplayTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
        RebuildSide();
    }

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
            var manifest = await VipServer.FetchAsync(server, _serverCachePath, CancellationToken.None);
            SetManifest(manifest);
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
            var (games, zips) = await Task.Run(() => Scan(folders, known, Found));
            Show(games, zips);
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
    private static (List<GameEntry> Games, List<string> Zips) Scan(List<string> folders, Dictionary<string, GameEntry> known,
        Action found)
    {
        var games = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        GameEntry? Known(string dir)
        {
            try { return known.GetValueOrDefault(Path.GetFullPath(dir)); }
            catch { return null; }
        }
        foreach (string folder in folders)
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
                foreach (var matsuri in _catalog.Matsuri.OrderByDescending(m => m.Year ?? 0).ThenByDescending(m => m.Month))
                    side.Add(new SideItem(matsuri.ShortName, matsuri.Works.Count(w => w.Local is not null), matsuri.Works.Count, matsuri, null, false));
                break;
            case VipListMode.Storage:
                side.AddRange(StorageSideItems());
                break;
            case VipListMode.All:
            {
                var all = AllRows();
                side.Add(new SideItem(T("すべて", "All"), all.Count(r => r.Local is not null), all.Count, null, null, false, Filter: "*all"));
                side.Add(new SideItem(T("★ お気に入り", "★ Favorites"), all.Count(r => r.Local is not null && IsFavorite(r)), all.Count(IsFavorite),
                    null, null, false, Filter: "*fav"));
                foreach (string group in Data.Groups)
                {
                    var members = all.Where(r => InGroup(r, group)).ToList();
                    side.Add(new SideItem("■ " + group, members.Count(r => r.Local is not null), members.Count, null, null, false,
                        Filter: "group:" + group));
                }
                break;
            }
            case VipListMode.Playlist:
            {
                var all = AllRows();
                var keys = all.ToLookup(KeyOf, StringComparer.OrdinalIgnoreCase);
                foreach (var playlist in Data.Playlists)
                {
                    // Each entry once: owned of all, the ones the launcher can't find included.
                    var entries = playlist.Games.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    int owned = entries.Count(key => keys[key].FirstOrDefault()?.Local is not null);
                    side.Add(new SideItem("♪ " + playlist.Name, owned, entries.Count, null, null, false,
                        Filter: PlaylistFilter + playlist.Name));
                }
                side.Add(new SideItem(T("＋ 新しいプレイリスト…", "＋ New Playlist…"), 0, NoCount, null, null, false, Filter: NewPlaylistItem));
                side.Add(new SideItem(T("＋ コードから読み込む…", "＋ Import from a Code…"), 0, NoCount, null, null, false, Filter: ImportPlaylistItem));
                side.Add(new SideItem(T("＋ テキストから作る…", "＋ Make from a Text File…"), 0, NoCount, null, null, false, Filter: TextPlaylistItem));
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
                side.AddRange(OwnedSideItems());
                break;
        }
        if (Data.Pinned.Count > 0)
        {
            var pinned = side.Where(item => PinKey(item) is { } key && Data.Pinned.Contains(key))
                .OrderBy(item => Data.Pinned.IndexOf(PinKey(item)!)).ToList();
            side = pinned.Concat(side.Except(pinned)).ToList();
        }
        _side = side;
        int index = Data.List == VipListMode.Owned ? side.FindIndex(item => item.Filter == Data.OwnedSort)
            : Data.Selected is { } key ? side.FindIndex(item => SideKey(item) == key) : -1;
        // Never a heading: 保管庫 starts with one.
        if (index < 0) index = Math.Max(0, side.FindIndex(item => item.Total != Heading));
        _sideIndex = index;
        _sideTop = Math.Max(0, _sideIndex - _sideVisible / 2);
        RebuildRows();
    }

    private static string SideKey(SideItem item) =>
        item.Filter ?? item.Matsuri?.Id ?? item.Author ?? (item.Strays ? "*strays" : item.Server ? "*server:" + item.Area : "*owned");

    private void RebuildRows()
    {
        var rows = new List<Row>();
        var item = _sideIndex < _side.Count ? _side[_sideIndex] : null;
        if (Data.List == VipListMode.Owned)
        {
            rows.AddRange(OwnedRows());
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
        else if (item?.Filter is { } filter && !filter.StartsWith("*playlist", StringComparison.Ordinal))
        {
            var all = AllRows();
            rows.AddRange(filter == "*fav" ? all.Where(IsFavorite)
                : filter.StartsWith("group:", StringComparison.Ordinal) ? all.Where(r => InGroup(r, filter["group:".Length..]))
                : all);
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
        if (_query.Length > 0)
        {
            string query = Fold(_query);
            rows = rows.Where(r => Fold(r.Title).Contains(query) || Fold(r.Author).Contains(query) ||
                                   Fold(r.Work?.Event?.Name).Contains(query) || Fold(r.Remote?.Thread).Contains(query)).ToList();
        }
        _rows = rows;
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
        var areas = _remoteExtras.GroupBy(g => g.Area ?? "*server")
            .Select(group => group.ToList())
            .GroupBy(games => games[0].Area is null ? "*server"
                : Array.IndexOf(StorageSections, games[0].Section) >= 0 ? games[0].Section! : "*other")
            .OrderBy(section => section.Key == "*server" ? -1 : section.Key == "*other" ? StorageSections.Length : Array.IndexOf(StorageSections, section.Key));
        foreach (var section in areas)
        {
            if (section.Key != "*server") items.Add(new SideItem(StorageHeading(section.Key), 0, Heading, null, null, false));
            foreach (var games in section.OrderByDescending(games => games.Count))
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

    /// <summary>手持ち's side list: the orders, then the filters (favorites, groups, engines) with their counts.</summary>
    private List<SideItem> OwnedSideItems()
    {
        var owned = AllRows().Where(r => r.Local is not null).ToList();
        var items = new List<SideItem> { new(T("並べ替え", "Sort"), 0, Heading, null, null, false) };
        items.AddRange(OwnedSorts.Select(sort => new SideItem(sort.Label, 0, NoCount, null, null, false, Filter: sort.Key)));
        items.Add(new SideItem(T("絞り込み", "Show"), 0, Heading, null, null, false));
        items.Add(new SideItem(T("すべて", "All"), owned.Count, owned.Count, null, null, false, Filter: "show:all"));
        items.Add(new SideItem(T("★ お気に入り", "★ Favorites"), owned.Count(IsFavorite), owned.Count(IsFavorite), null, null, false, Filter: "show:fav"));
        foreach (string group in Data.Groups)
        {
            int count = owned.Count(r => InGroup(r, group));
            items.Add(new SideItem("■ " + group, count, count, null, null, false, Filter: "show:group:" + group));
        }
        foreach (var family in EngineFamilies.All)
        {
            int count = owned.Count(r => r.Local!.Family == family);
            if (count > 0)
                items.Add(new SideItem(EngineFamilies.Label(family), count, count, null, null, false, Filter: "show:engine:" + family));
        }
        return items;
    }

    /// <summary>The games on this PC, narrowed by the chosen filter and in the chosen order.</summary>
    private IEnumerable<Row> OwnedRows()
    {
        var rows = AllRows().Where(r => r.Local is not null);
        string filter = Data.OwnedFilter;
        if (filter == "show:fav") rows = rows.Where(IsFavorite);
        else if (filter.StartsWith("show:group:", StringComparison.Ordinal)) rows = rows.Where(r => InGroup(r, filter["show:group:".Length..]));
        else if (filter.StartsWith("show:engine:", StringComparison.Ordinal))
            rows = rows.Where(r => r.Local!.Family.ToString() == filter["show:engine:".Length..]);

        VipRecord? Record(Row r) => Data.Games.GetValueOrDefault(r.Local!.RootPath);
        return Data.OwnedSort switch
        {
            "sort:recent" => rows.OrderByDescending(r => Record(r)?.LastPlayedUtc ?? DateTime.MinValue),
            "sort:most" => rows.OrderByDescending(r => Record(r)?.PlayCount ?? 0),
            "sort:time" => rows.OrderByDescending(r => Record(r)?.PlaySeconds ?? 0),
            "sort:added" => rows.OrderByDescending(r => r.Local!.FolderCreatedUtc ?? DateTime.MinValue),
            "sort:matsuri" => rows.OrderByDescending(r => r.Work?.Event?.Year ?? 0)
                .ThenBy(r => r.Work?.Event?.Id, StringComparer.Ordinal)
                .ThenBy(r => int.TryParse(new string((r.Work?.No ?? "").Where(char.IsAsciiDigit).ToArray()), out int n) ? n : int.MaxValue),
            _ => rows // already by title
        };
    }

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
            bool chosen = Data.List == VipListMode.Owned
                ? item.Filter == Data.OwnedSort || item.Filter == Data.OwnedFilter
                : index == _sideIndex;
            string count = item.Total == NoCount ? "" : $"  ({item.Total})";
            items.Add(new("  " + item.Label + count, () =>
            {
                CloseOverlay();
                SelectSide(index);
            }, Checked: chosen));
        }
        _overlay = new VipMenu(at, items, CloseOverlay);
        Redraw();
    }

    private void ChooseOwned(SideItem item)
    {
        if (item.Filter is not { } choice) return;
        if (choice.StartsWith("sort:", StringComparison.Ordinal)) Data.OwnedSort = choice;
        else Data.OwnedFilter = choice;
        _save();
        _rowIndex = _rowTop = 0;
        RebuildRows();
        string sort = OwnedSorts.FirstOrDefault(s => s.Key == Data.OwnedSort).Label ?? T("タイトル順", "By Title");
        Say($"手持ちを{sort}で、{item.Label.TrimStart('★', '■', ' ')}を表示中。{_rows.Count}本あるよ。",
            $"Your games: {_rows.Count} shown, {sort}.");
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
        : row.Local is { } local ? "pc:" + Path.GetFullPath(local.RootPath)
        : "zip:" + row.Zip;

    private bool IsFavorite(Row row) => Data.Favorites.Contains(KeyOf(row), StringComparer.OrdinalIgnoreCase);

    private bool IsCompleted(Row row) => Data.Completed.Contains(KeyOf(row), StringComparer.OrdinalIgnoreCase);

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

    // ---- playlists ----

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
        Say(add ? $"「{row.Title}」をプレイリスト「{playlist.Name}」に入れたよ。({playlist.Games.Count}本)" : $"「{row.Title}」を「{playlist.Name}」から出したよ。",
            add ? $"Added {row.Title} to the playlist {playlist.Name} ({playlist.Games.Count} games)." : $"Took {row.Title} out of {playlist.Name}.");
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
            new(T($"「{RetroDraw.Fit(name, 140)}」を消す", $"Delete {RetroDraw.Fit(name, 140)}"), () =>
            {
                Data.Playlists.Remove(playlist);
                _save();
                CloseOverlay();
                RebuildList();
                Say($"プレイリスト「{name}」を消したよ。作品はそのまま。", $"Deleted the playlist {name}; its games are untouched.");
            })
        }, CloseOverlay);
        Redraw();
    }

    private async Task RenamePlaylistAsync(VipPlaylist playlist)
    {
        CloseOverlay();
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        var dialog = new TextPromptDialog(T("プレイリストの名前", "Rename Playlist"), T("新しい名前:", "New name:"), playlist.Name, T("変える", "Rename"));
        if (!await dialog.ShowAsync(window) || dialog.Value.Length == 0 || dialog.Value == playlist.Name) return;
        if (Data.Playlists.Any(p => p != playlist && p.Name == dialog.Value))
        {
            Say($"「{dialog.Value}」はもうあるよ。", $"There's already a playlist called {dialog.Value}.");
            return;
        }
        string old = playlist.Name;
        playlist.Name = dialog.Value;
        // Keep it open if it was.
        if (Data.Selected == PlaylistFilter + old) Data.Selected = PlaylistFilter + playlist.Name;
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
        string json = System.Text.Json.JsonSerializer.Serialize(new { n = playlist.Name, g = shared });
        using var buffer = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(buffer, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(System.Text.Encoding.UTF8.GetBytes(json));
        string code = PlaylistCodePrefix + Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(code);
        int left = playlist.Games.Count - shared.Count;
        Say($"「{playlist.Name}」のコードをコピーしたよ。({shared.Count}本{(left > 0 ? $"、PCだけの{left}本は除外" : "")})",
            $"Copied the code for {playlist.Name}: {shared.Count} games{(left > 0 ? $"; {left} only on this PC were left out" : "")}. Paste it to share.");
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
            string game = KeyOf(candidates[0]);
            // Two titles that land on the same game are one entry too.
            if (games.Contains(game, StringComparer.OrdinalIgnoreCase)) continue;
            games.Add(game);
            found++;
        }

        string name = Path.GetFileNameWithoutExtension(path), unique = name;
        for (int i = 2; Data.Playlists.Any(p => p.Name == unique); i++) unique = $"{name} ({i})";
        Data.Playlists.Add(new VipPlaylist { Name = unique, Games = games });
        _save();
        RebuildList();
        Say($"プレイリスト「{unique}」を作ったよ。{games.Count}本中{found}本が見つかった。見つからないのは赤で表示。",
            $"Made the playlist {unique}: {games.Count} games, {found} found; the rest are kept in red as !!!!.");
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
        bool completed = IsCompleted(row);
        items.Add(new(completed ? T("クリア済みを外す", "Unmark Completed") : T("クリア済みにする", "Mark Completed"), () => ToggleCompleted(row), Checked: completed));
        if (Data.Groups.Count > 0) items.Add(new(T("グループ", "Groups"), null));
        foreach (string group in Data.Groups)
        {
            string name = group;
            items.Add(new("  " + name, () => ToggleGroup(row, name), Checked: InGroup(row, name)));
        }
        items.Add(new(T("新しいグループ…", "New Group…"), () => _ = NewGroupAsync(row)));
        if (Data.Playlists.Count > 0) items.Add(new(T("プレイリスト", "Playlists"), null));
        foreach (var playlist in Data.Playlists)
        {
            var list = playlist;
            items.Add(new("  ♪ " + list.Name, () => TogglePlaylist(row, list), Checked: InPlaylist(row, list)));
        }
        items.Add(new(T("新しいプレイリスト…", "New Playlist…"), () => _ = NewPlaylistAsync(row)));
        if (row.Local is { } local) items.Add(new(T("フォルダを開く", "Open Folder"), () => OpenFolder(local.RootPath)));
        if (row.Work?.Downloads?.FirstOrDefault() is { } original) items.Add(new(T("配布元を開く", "Open Download Page"), () => OpenUrl(original)));
        if (row.Work?.WikiUrl is { } wiki) items.Add(new("wiki", () => OpenUrl(wiki)));
        _overlay = new VipMenu(at, items, CloseOverlay);
        Redraw();
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
        string.IsNullOrWhiteSpace(work.No) ? null : _remoteByWork.GetValueOrDefault(VipServer.Key(work.Matsuri, work.No!)));

    /// <summary>A server game the catalog doesn't list, such as one from a thread.</summary>
    private Row RowFor(VipServerGame remote) =>
        new(null, _remoteLocal.GetValueOrDefault(remote.Path), remote.Year?.ToString() ?? "", remote.DisplayTitle, remote.Author ?? "",
            Remote: remote);

    private static string Short(VipMatsuri? matsuri) => matsuri?.ShortName ?? "";

    private Row? SelectedRow => _rowIndex >= 0 && _rowIndex < _rows.Count ? _rows[_rowIndex] : null;

    // ---- actions ----

    /// <summary>Puts a line in the banner's message area; only the chosen language's is shown.</summary>
    private void Say(string japanese, string english)
    {
        _say = japanese;
        _sayEn = english;
        Redraw();
    }

    private void SelectSide(int index)
    {
        if (_side.Count == 0) return;
        _sideIndex = Math.Clamp(index, 0, _side.Count - 1);
        if (Data.List == VipListMode.Owned)
        {
            ChooseOwned(_side[_sideIndex]);
            return;
        }
        Data.Selected = SideKey(_side[_sideIndex]);
        _save();
        _rowIndex = _rowTop = 0;
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
                Say($"ダウンロードできた！ {extension}は自分で展開してからF5を押してね。",
                    $"Downloaded {title} as {extension}: extract or run it yourself, then press F5.");
                Shell.ShowInFileManager(zip);
                return;
            }
            Say("ダウンロードできた！ 展開しているよ…", $"Downloaded {title}. Unpacking…");
            string folder = await Task.Run(() => VipZip.Extract(zip));
            Data.Downloaded[Path.GetFullPath(folder)] = remote.WorkKey ?? remote.Path;
            _save();
            try { File.Delete(zip); }
            catch (Exception ex) { Diagnostics.Log($"Downloaded zip kept {zip}: {ex.Message}"); }
            await ScanAsync();
            Say($"「{title}」が手持ちに加わった！ Enterで遊べるよ。", $"{title} is ready: Enter plays it.");
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
        }
        finally
        {
            _downloading = null;
            _downloadCancel.Dispose();
            _downloadCancel = null;
            Redraw();
        }
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
            Say("ダウンロード先に書き込めない…", $"Can't write to {root}: {ex.Message}. Choose another folder under Settings → Download Folder.");
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
        for (int i = 0; i < _commands.Count; i++)
        {
            var command = _commands[i];
            var row = new Rect(_listRect.X + 6, _listRect.Y + 26 + i * 36, _listRect.Width - 12, 34);
            // Nothing looks chosen until it's pointed at: the keyboard cursor shows only after a key press.
            bool active = _command == i && _keys;
            if (active || (row.Contains(_mouse) && _overlay is null)) RetroDraw.Cursor(g, row, _tick, active);
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
            VipListMode.Playlist => T("プレイリスト：選ぶか作ってね", "Playlists: pick or make one"),
            VipListMode.All => T("全作品：絞り込みを選んでね", "All Games: pick what to show"),
            VipListMode.Storage => T("保管庫：保管庫を選んでね", "Thread Storage: pick an archive"),
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
                if (i == _sideIndex && _keys) RetroDraw.Cursor(g, row, _tick, _focus == Pane.Side);
                else if (row.Contains(_mouse) && _overlay is null) RetroDraw.Fill(g, row.Deflate(1), Color.FromArgb(40, 255, 255, 255));
                if (item.Total == Heading)
                {
                    RetroDraw.Text(g, item.Label, row.X + 3, row.Y + 2, RetroDraw.System);
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
                if (isNew) RetroDraw.Text(g, "NEW", row.X + 6 + labelWidth, row.Y + 2, RetroDraw.Red);
                RetroDraw.Text(g, count, row.Right - countWidth - 3, row.Y + 2, RetroDraw.TextColor);
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
        string heading = Data.List == VipListMode.Owned
            ? T("手持ち", "On This PC") + $"（{OwnedSorts.FirstOrDefault(sort => sort.Key == Data.OwnedSort).Label ?? T("タイトル順", "By Title")}・{_side.FirstOrDefault(i => i.Filter == Data.OwnedFilter)?.Label.TrimStart('★', '■', ' ') ?? T("すべて", "All")}）"
            : item?.Filter is { } shown && shown.StartsWith(PlaylistFilter, StringComparison.Ordinal) ? T("プレイリスト：", "Playlist: ") + shown[PlaylistFilter.Length..]
            : item?.Filter is { } filter ? filter == "*fav" ? T("お気に入り", "Favorites")
                : filter.StartsWith("group:", StringComparison.Ordinal) ? T("グループ：", "Group: ") + filter["group:".Length..] : T("全作品（名前順）", "All Games (by title)")
            : item?.Matsuri is { } matsuri ? matsuri.Name + T("　参加作品一覧", " — entries")
            : item?.Author is { } author ? T($"{author}さんの作品", $"Works by {author}")
            : item?.Strays == true ? T("その他のゲーム（wikiにない作品）", "Other Games (not on the wiki)")
            : item?.Server == true ? (item.Area == "*server" ? T("サーバーの作品（スレ作品など）", "Server Games (thread games and more)") : item.Label + T("（アップローダー保存庫）", " (Uploader Archive)"))
            : T("手持ち", "On This PC") + $"（{OwnedSorts.FirstOrDefault(sort => sort.Key == Data.OwnedSort).Label ?? T("タイトル順", "By Title")}・{_side.FirstOrDefault(i => i.Filter == Data.OwnedFilter)?.Label.TrimStart('★', '■', ' ') ?? T("すべて", "All")}）";
        // The search box sits at the right of the heading.
        double searchWidth = Math.Min(200, Math.Floor(_listRect.Width * 0.42));
        _searchRect = new Rect(_listRect.Right - 8 - searchWidth, _listRect.Y + 5, searchWidth, 15);
        double headingWidth = _listRect.Width - 24 - searchWidth;
        if (HasFilterMenu)
        {
            // The menu button, between the heading and the search box.
            string label = Data.List == VipListMode.Owned ? T("並べ替え・絞り込み▼", "Sort / Filter ▼") : T("絞り込み▼", "Show ▼");
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
        if (Data.List != VipListMode.Owned && (item?.Matsuri is not null || playlist) && _completedCount.Total > 0)
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
        double noWidth = storage ? 0 : Data.List == VipListMode.Matsuri ? 32 : Math.Min(110, width * 0.22);
        double stateWidth = 22, authorWidth = Data.List == VipListMode.Author || storage ? 0 : Math.Min(130, width * 0.28);
        double titleWidth = width - noWidth - stateWidth - authorWidth - 12;
        double headerY = _listRect.Y + 22;
        RetroDraw.Fill(g, new Rect(x, headerY + 15, width, 1), Color.FromArgb(120, 200, 210, 255));
        if (!storage) RetroDraw.Text(g, Data.List == VipListMode.Matsuri ? "No." : T("祭り", "Festival"), x + 3, headerY + 1, RetroDraw.System);
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
                if (i == _rowIndex) RetroDraw.Cursor(g, rect, _tick, _focus == Pane.Rows);
                else if (rect.Contains(_mouse) && _overlay is null) RetroDraw.Fill(g, rect.Deflate(1), Color.FromArgb(40, 255, 255, 255));

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
                string state = running ? "●" : owned ? "▶" : row.Zip is not null ? "Z" : fetching ? (_tick / 4 % 2 == 0 ? "↓" : "") : remote ? "↓"
                    : eternal ? T("エ", "E") : "・";
                double stateX = rect.X + noWidth + 4;
                RetroDraw.Text(g, state, stateX, rect.Y + 2,
                    running ? RetroDraw.Red : owned ? RetroDraw.Green : row.Zip is not null ? RetroDraw.Yellow
                    : fetching ? RetroDraw.Yellow : remote ? RetroDraw.System : eternal ? RetroDraw.Red : RetroDraw.Disabled);
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

        // The picture: 4:3, as large as leaves room for six lines of text and the buttons.
        const int Columns = 4;
        int buttonRows = (buttons.Count + Columns - 1) / Columns;
        double room = _detailRect.Bottom - 8 - 18 * buttonRows - 4 - 6 - 16 - 15 * 5;
        double pictureWidth = Math.Floor(Math.Min(width, (room - y) * 4 / 3));
        var picture = new Rect(x + Math.Floor((width - pictureWidth) / 2), y, pictureWidth, Math.Floor(pictureWidth * 3 / 4));
        // The game's own title screen, else the picture from its matsuri's site.
        // The built-in picture when there is one, else the game's own title screen once it's on the PC.
        var bitmap = row.Work is { } pictured && VipThumbs.Has(pictured) ? ThumbFor(pictured)
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
        double Add(string label, string value) => y + 16 > limit ? 0 : Line(g, label, value, x, y, width);
        string state = row.Local is not null ? (IsCompleted(row) ? T("  [クリア済み]", "  [completed]") : "") : row.Zip is not null ? "  [zip]" : row.Remote is not null ? T("  [未所持 ↓]", "  [not on this PC ↓]")
            : row.Work?.Eternal == true ? T("  [エターナル（未提出）]", "  [eternal (never submitted)]")
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
        else if (row.Remote is { } info)
        {
            if (!string.IsNullOrWhiteSpace(info.Author)) y += Add(T("作者", "Author"), info.Author!);
            if (!string.IsNullOrWhiteSpace(info.Thread)) y += Add(T("スレ", "Thread"), info.Thread!);
            if (info.Year is { } year) y += Add(T("年", "Year"), year.ToString());
            if (!string.IsNullOrWhiteSpace(info.Engine)) y += Add(T("ツール", "Engine"), info.Engine!);
            if (!string.IsNullOrWhiteSpace(info.Note)) y += Add(T("メモ", "Note"), info.Note!);
        }
        if (row.Local is { } local)
        {
            y += Add(T("ツール", "Engine"), local.Engine);
            if (Data.Games.TryGetValue(local.RootPath, out var record) && record.PlayCount > 0)
                y += Add(T("プレイ", "Played"), T($"{record.PlayCount}回", $"{record.PlayCount}×") + $" ／ {Look.FormatDuration(Math.Max(1, record.PlaySeconds))}");
        }
        else if (row.Zip is null && row.Remote is { } server)
        {
            string size = ReferenceEquals(server, _downloading) && server.Size > 0
                ? $"{Look.FormatBytes(_downloaded)} / {Look.FormatBytes(server.Size)}"
                : server.Size > 0 ? Look.FormatBytes(server.Size) : "?";
            y += Add(T("サーバー", "Server"), size);
        }


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

    /// <summary>A work's picture from the matsuri's site (built into the launcher), loaded on first use.</summary>
    private Bitmap? ThumbFor(VipWork work) =>
        VipThumbs.Has(work) ? Picture($"thumb:{work.Matsuri}/{work.No}", () => VipThumbs.Load(work)) : null;

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
        var mouse = ToCanvas(e.GetPosition(this));
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
            if (_overlay is null)
            {
                int row = _rowAreas.FindIndex(area => area.Contains(point));
                // Only over a game's words: the empty part of its line is background, and goes back.
                if (row >= 0 && row < _rowWords.Count && _rowWords[row].Any(words => words.Contains(point)))
                {
                    _focus = Pane.Rows;
                    SelectRow(_rowTop + row);
                    if (SelectedRow is { } selected) OpenMenu(selected, point);
                    return;
                }
                int side = _sideAreas.FindIndex(area => area.Contains(point));
                if (side >= 0 && _sideTop + side < _side.Count && _side[_sideTop + side].Author is { } author)
                {
                    OpenAuthorMenu(author, point);
                    return;
                }
                if (side >= 0 && _sideTop + side < _side.Count && _side[_sideTop + side].Matsuri is { } matsuri)
                {
                    _overlay = new VipMenu(point, new List<VipMenu.Item> { PinItem("matsuri:" + matsuri.Id) }, CloseOverlay);
                    Redraw();
                    return;
                }
                if (side >= 0 && _sideTop + side < _side.Count && _side[_sideTop + side].Filter is { } list &&
                    list.StartsWith(PlaylistFilter, StringComparison.Ordinal))
                {
                    OpenPlaylistMenu(list[PlaylistFilter.Length..], point);
                    return;
                }
                if (side >= 0 && _sideTop + side < _side.Count && _side[_sideTop + side].Filter is { } filter &&
                    filter.StartsWith("group:", StringComparison.Ordinal))
                {
                    OpenGroupMenu(filter["group:".Length..], point);
                    return;
                }
            }
            Back();
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        if (_overlay is null && PressScrollBar(point, e))
        {
            e.Handled = true;
            Redraw();
            return;
        }
        // Topmost first: the overlay's areas were added last.
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
        _keys = true;
        if (_overlay is not null)
        {
            if (!_overlay.OnKey(e.Key)) CloseOverlay();
            e.Handled = true;
            Redraw();
            return;
        }
        e.Handled = true;
        switch (e.Key)
        {
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
        // Up a page: works → their matsuri/author/filter → the list modes.
        _focus = _focus == Pane.Rows && !HasFilterMenu ? Pane.Side : Pane.Commands;
        if (_focus == Pane.Commands) _command = Math.Max(0, _commands.FindIndex(c => c.Mode == Data.List));
        Redraw();
    }
}
