using System.Diagnostics;
using Avalonia.Platform.Storage;

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
        VipServerGame? Remote = null);

    /// <summary>The download server: the R2 bucket's public address, where manifest.json is.</summary>
    private const string ServerAddress = "https://dl.viprpg-archive.com";

    private sealed record Command(string Label, string Hint, string HintEn, Action Run, VipListMode? Mode = null);

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

    /// <summary>The screen to show: the canvas with the search box over it.</summary>
    public Control Host => _host;

    private List<GameEntry> _games = new();
    private List<GameEntry> _strays = new();
    private List<string> _strayZips = new();
    private bool _extracting;
    private List<SideItem> _side = new();
    private List<Row> _rows = new();
    // The left window shows one of three pages: the list modes, a mode's matsuri/authors/filters, or the works.
    private Pane _focus = Pane.Commands;
    private int _command, _sideIndex, _sideTop, _rowIndex, _rowTop;
    private int _sideVisible = 1, _rowsVisible = 1;
    private bool _scanning, _started, _loadingCache;
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
            new("手持ち", "このPCにある作品だけ。", "Only the games on this PC.", () => SetList(VipListMode.Owned), VipListMode.Owned),
            new("全作品", "全部の作品を名前順に。お気に入りやグループで絞れるよ。", "Every game by title; narrow it to favorites or a group. Right-click a game for more.",
                () => SetList(VipListMode.All), VipListMode.All),
            new("祭り別", "祭りごとに作品を並べるよ。", "Works by matsuri.", () => SetList(VipListMode.Matsuri), VipListMode.Matsuri),
            new("作者別", "作者ごとに作品を並べるよ。", "Works by author.", () => SetList(VipListMode.Author), VipListMode.Author)
        };
        _tools = new List<Command>
        {
            new("再スキャン", "フォルダを調べなおすよ。(F5)", "Look through the folders again. (F5)", () => _ = ScanAsync()),
            new("フォルダ追加", "ゲームが入っているフォルダを選んでね。", "Add a folder that holds VIPRPG games.", () => _ = AddFolderAsync()),
            new("設定", "起動方法、EasyRPG、フォルダ、モード切り替え。(S)", "Launch options, EasyRPG, folders, and the switch back to HRPG mode. (S)", ShowSettings)
        };
        _command = Math.Max(0, _commands.FindIndex(c => c.Mode == Data.List));
        _host.Children.Add(this);
        _host.Children.Add(_picture);
        _host.Children.Add(_search);
        _search.HorizontalAlignment = HorizontalAlignment.Left;
        _search.VerticalAlignment = VerticalAlignment.Top;
        _search.Watermark = "検索 (Ctrl+F)";
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
                _focus = Pane.Rows;
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
            "Welcome! Pick a list, then a matsuri or author, then a game. Enter plays, Esc goes back.");
        RebuildSide();
    }

    private VipData Data => _store.Data.Vip;

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
            if (byWork.ContainsKey(key)) continue;
            byWork[key] = new VipServerGame
            {
                Source = "original", Url = link, Matsuri = work.Matsuri, No = work.No, Title = work.Title, Author = work.Author,
                Year = work.Event?.Year, Engine = work.Engine, Path = $"original/{work.Matsuri}/{work.No}"
            };
        }
        _remoteByWork = byWork;
        _remoteExtras = extras.OrderBy(g => g.DisplayTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
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
                    "No folders yet: use フォルダ追加 (Add folder) to show where your VIPRPG games are.");
            else
                Say($"{games.Count}本のゲームを見つけたぞ！ そのうち{owned}本が祭りの作品だ。",
                    $"Found {games.Count} games; {owned} of them are matsuri entries in the catalog.");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("VIPRPG scan failed: " + ex);
            Say("スキャンに失敗した…", "The scan failed: " + ex.Message);
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
        if (mode != Data.List) Data.Selected = null;
        Data.List = mode;
        _save();
        RebuildSide();
        _focus = Pane.Side;
    }

    private void RebuildSide()
    {
        var side = new List<SideItem>();
        switch (Data.List)
        {
            case VipListMode.Matsuri:
                foreach (var matsuri in _catalog.Matsuri)
                    side.Add(new SideItem(matsuri.ShortName, matsuri.Works.Count(w => w.Local is not null), matsuri.Works.Count, matsuri, null, false));
                if (_strays.Count + _strayZips.Count > 0)
                    side.Add(new SideItem("その他のゲーム", _strays.Count, _strays.Count + _strayZips.Count, null, null, true));
                // Downloadable games the catalog doesn't list, by the uploader area they came from.
                foreach (var area in _remoteExtras.GroupBy(g => g.Area ?? "*server"))
                {
                    var games = area.ToList();
                    side.Add(new SideItem(games[0].Area is null ? "サーバーの作品" : games[0].Thread ?? area.Key,
                        games.Count(g => _remoteLocal.ContainsKey(g.Path)), games.Count, null, null, false, Server: true, Area: area.Key));
                }
                break;
            case VipListMode.All:
            {
                var all = AllRows();
                side.Add(new SideItem("すべて", all.Count(r => r.Local is not null), all.Count, null, null, false, Filter: "*all"));
                side.Add(new SideItem("★ お気に入り", all.Count(r => r.Local is not null && IsFavorite(r)), all.Count(IsFavorite),
                    null, null, false, Filter: "*fav"));
                foreach (string group in Data.Groups)
                {
                    var members = all.Where(r => InGroup(r, group)).ToList();
                    side.Add(new SideItem("■ " + group, members.Count(r => r.Local is not null), members.Count, null, null, false,
                        Filter: "group:" + group));
                }
                break;
            }
            case VipListMode.Author:
                foreach (var (author, works) in _catalog.ByAuthor())
                    side.Add(new SideItem(author, works.Count(w => w.Local is not null), works.Count, null, author, false));
                break;
            default:
                side.AddRange(OwnedSideItems());
                break;
        }
        _side = side;
        int index = Data.List == VipListMode.Owned ? side.FindIndex(item => item.Filter == Data.OwnedSort)
            : Data.Selected is { } key ? side.FindIndex(item => SideKey(item) == key) : -1;
        _sideIndex = Math.Max(0, index);
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
        else if (item?.Filter is { } filter)
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
            foreach (var work in _catalog.Games.Where(w => string.Equals(w.Author?.Trim(), author, StringComparison.OrdinalIgnoreCase)))
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

    private static readonly (string Key, string Label)[] OwnedSorts =
    {
        ("sort:title", "タイトル順"), ("sort:recent", "最近遊んだ順"), ("sort:most", "よく遊んだ順"),
        ("sort:time", "プレイ時間順"), ("sort:added", "追加した順"), ("sort:matsuri", "祭り順")
    };

    /// <summary>手持ち's side list: the orders, then the filters (favorites, groups, engines) with their counts.</summary>
    private List<SideItem> OwnedSideItems()
    {
        var owned = AllRows().Where(r => r.Local is not null).ToList();
        var items = new List<SideItem> { new("並べ替え", 0, Heading, null, null, false) };
        items.AddRange(OwnedSorts.Select(sort => new SideItem(sort.Label, 0, NoCount, null, null, false, Filter: sort.Key)));
        items.Add(new SideItem("絞り込み", 0, Heading, null, null, false));
        items.Add(new SideItem("すべて", owned.Count, owned.Count, null, null, false, Filter: "show:all"));
        items.Add(new SideItem("★ お気に入り", owned.Count(IsFavorite), owned.Count(IsFavorite), null, null, false, Filter: "show:fav"));
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

    private void ChooseOwned(SideItem item)
    {
        if (item.Filter is not { } choice) return;
        if (choice.StartsWith("sort:", StringComparison.Ordinal)) Data.OwnedSort = choice;
        else Data.OwnedFilter = choice;
        _save();
        _rowIndex = _rowTop = 0;
        RebuildRows();
        string sort = OwnedSorts.FirstOrDefault(s => s.Key == Data.OwnedSort).Label ?? "タイトル順";
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
        row.Work is { } work ? $"{work.Matsuri}/{(string.IsNullOrWhiteSpace(work.No) ? work.Title : work.No)}"
        : row.Remote is { } remote ? "dl:" + remote.Path
        : row.Local is { } local ? "pc:" + Path.GetFullPath(local.RootPath)
        : "zip:" + row.Zip;

    private bool IsFavorite(Row row) => Data.Favorites.Contains(KeyOf(row), StringComparer.OrdinalIgnoreCase);

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
        var dialog = new TextPromptDialog("新しいグループ", "グループの名前 (group name):", "", "作る");
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
            items.Add(new("遊ぶ", () => Play(row)));
            if (EngineFamilies.HasTestMode(game.Family)) items.Add(new("テストプレイ", () => Play(row, testPlay: true)));
        }
        else if (row.Zip is { } zip) items.Add(new("展開", () => _ = ExtractAsync(zip)));
        else if (row.Remote is { } remote) items.Add(new("ダウンロード", () => _ = DownloadAsync(remote)));
        bool favorite = IsFavorite(row);
        items.Add(new(favorite ? "お気に入りから外す" : "お気に入りに追加", () => ToggleFavorite(row), Checked: favorite));
        if (Data.Groups.Count > 0) items.Add(new("グループ", null));
        foreach (string group in Data.Groups)
        {
            string name = group;
            items.Add(new("  " + name, () => ToggleGroup(row, name), Checked: InGroup(row, name)));
        }
        items.Add(new("新しいグループ…", () => _ = NewGroupAsync(row)));
        if (row.Local is { } local)
        {
            items.Add(new("説明書", () => OpenReadme(local)));
            items.Add(new("フォルダを開く", () => OpenFolder(local.RootPath)));
        }
        if (row.Work?.Downloads?.FirstOrDefault() is { } original) items.Add(new("配布元を開く", () => OpenUrl(original)));
        if (row.Work?.WikiUrl is { } wiki) items.Add(new("wiki", () => OpenUrl(wiki)));
        _overlay = new VipMenu(at, items, CloseOverlay);
        Redraw();
    }

    /// <summary>The right-click menu for a group in the side list.</summary>
    private void OpenGroupMenu(string group, Point at)
    {
        _overlay = new VipMenu(at, new List<VipMenu.Item>
        {
            new("新しいグループ…", () => _ = NewGroupAsync(null)),
            new($"「{group}」を消す", () => DeleteGroup(group))
        }, CloseOverlay);
        Redraw();
    }

    private Row RowFor(VipWork work, string? no) => new(work, work.Local, no ?? "", work.Title, work.Author ?? "", work.LocalZip,
        string.IsNullOrWhiteSpace(work.No) ? null : _remoteByWork.GetValueOrDefault(VipServer.Key(work.Matsuri, work.No!)));

    /// <summary>A server game the catalog doesn't list, such as one from a thread.</summary>
    private Row RowFor(VipServerGame remote) =>
        new(null, _remoteLocal.GetValueOrDefault(remote.Path), remote.Year?.ToString() ?? "", remote.DisplayTitle, remote.Author ?? "",
            Remote: remote);

    private static string Short(VipMatsuri? matsuri) => matsuri?.ShortName ?? "";

    private Row? SelectedRow => _rowIndex >= 0 && _rowIndex < _rows.Count ? _rows[_rowIndex] : null;

    // ---- actions ----

    /// <summary>Puts a line in the banner's message area.</summary>
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
        Redraw();
    }

    private void Play(Row row, bool testPlay = false)
    {
        if (row.Local is not { } game)
        {
            if (row.Zip is { } zip)
                Say("この作品はzipのままだ。「展開」で遊べるようになるよ。",
                    $"This one is still zipped ({Path.GetFileName(zip)}): press 展開 (Extract) to unpack it.");
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
                problem = "起動できるプログラムが見つからない。";
            }
        }
        catch (Exception ex)
        {
            process = null;
            problem = ex.Message;
        }

        if (process is null)
        {
            Say("起動できなかった…", (problem ?? "The game didn't start.").Replace('\n', ' '));
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
            Say("展開できなかった…", "Couldn't extract it: " + ex.Message);
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
        Say($"「{title}」をダウンロードしているよ…", $"Downloading {title}… press ダウンロード again to stop.");
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
                (resolved, string? name) = await VipOriginal.ResolveAsync(remote.Url!, cancel);
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
                (japanese, english) = ("配布元から直接は取れなかった。「配布元」で開いてみてね。", ex.Message + " Try 配布元 to open the page.");
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
            Say("ダウンロード先に書き込めない…", $"Can't write to {root}: {ex.Message}. Choose another folder under 設定 → ダウンロード先.");
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

    private void OpenReadme(GameEntry game)
    {
        var files = VipReadme.Find(game.RootPath);
        if (files.Count == 0)
        {
            Say("説明書は見つからなかった。", "No readme found in the game folder.");
            return;
        }
        _overlay = new VipReadme(files);
        Redraw();
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
            Title = "VIPRPGのゲームが入ったフォルダ",
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
        _sideAreas.Clear();
        double w = size.Width, h = size.Height;
        VipTheme theme = VipTheme.For(_sideIndex < _side.Count ? _side[_sideIndex].Matsuri : null);

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
        logo += RetroDraw.Logo(g, "ランチャー", rect.X + 20 + logo, rect.Y + 8, RetroDraw.Large, theme.LogoSecond, theme.LogoHighlight, theme.LogoOutline);

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
            RetroDraw.Text(g, RetroDraw.Fit(_say, width2), mx, rect.Y + 6, RetroDraw.TextColor);
            RetroDraw.Text(g, RetroDraw.Fit(_sayEn, width2), mx, rect.Y + 23, theme.Subtitle);
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
        PaintHeading(g, "一覧の種類", _listRect.Width - 16, back: false);
        for (int i = 0; i < _commands.Count; i++)
        {
            var command = _commands[i];
            var row = new Rect(_listRect.X + 6, _listRect.Y + 26 + i * 36, _listRect.Width - 12, 34);
            bool active = _command == i;
            if (active || (row.Contains(_mouse) && _overlay is null)) RetroDraw.Cursor(g, row, _tick, active);
            RetroDraw.Text(g, (command.Mode == Data.List ? "▶" : "  ") + command.Label, row.X + 4, row.Y + 3, RetroDraw.TextColor);
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
            VipListMode.Matsuri => "祭り別：祭りを選んでね",
            VipListMode.Author => "作者別：作者を選んでね",
            VipListMode.All => "全作品：絞り込みを選んでね",
            _ => "手持ち：並べ替えか絞り込みを選んでね"
        };
        PaintHeading(g, heading, _listRect.Width - 16, back: true);
        var list = new Rect(_listRect.X + 6, _listRect.Y + 22, _listRect.Width - 12, _listRect.Height - 30);
        _sideVisible = Math.Max(1, (int)(list.Height / 16));
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
                if (i == _sideIndex) RetroDraw.Cursor(g, row, _tick, _focus == Pane.Side);
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
                double labelWidth = RetroDraw.Text(g, (chosen ? "▶" : "") + label, row.X + 3, row.Y + 2,
                    chosen ? RetroDraw.Yellow : item.Owned > 0 || item.Total == NoCount ? RetroDraw.TextColor : RetroDraw.Disabled);
                if (isNew) RetroDraw.Text(g, "NEW", row.X + 6 + labelWidth, row.Y + 2, RetroDraw.Red);
                RetroDraw.Text(g, count, row.Right - countWidth - 3, row.Y + 2, item.Owned > 0 ? RetroDraw.Yellow : RetroDraw.Disabled);
                int index = i;
                _hot.Add(new VipHot(row, () =>
                {
                    SelectSide(index);
                    // Choosing opens its works.
                    _focus = Pane.Rows;
                }));
            }
        }
        if (_sideTop > 0) RetroDraw.MoreArrow(g, _listRect.Center.X, _listRect.Y + 16, _tick, down: false);
        if (_sideTop + _sideVisible < _side.Count) RetroDraw.MoreArrow(g, _listRect.Center.X, _listRect.Bottom - 7, _tick);
    }

    private void PaintRows(DrawingContext g, VipTheme theme)
    {
        RetroDraw.Window(g, _listRect, theme.WindowTop, theme.WindowBottom);
        var item = _sideIndex < _side.Count ? _side[_sideIndex] : null;
        string heading = Data.List == VipListMode.Owned
            ? $"手持ち（{OwnedSorts.FirstOrDefault(sort => sort.Key == Data.OwnedSort).Label ?? "タイトル順"}・{_side.FirstOrDefault(i => i.Filter == Data.OwnedFilter)?.Label.TrimStart('★', '■', ' ') ?? "すべて"}）"
            : item?.Filter is { } filter ? filter == "*fav" ? "お気に入り"
                : filter.StartsWith("group:", StringComparison.Ordinal) ? "グループ：" + filter["group:".Length..] : "全作品（名前順）"
            : item?.Matsuri is { } matsuri ? $"{matsuri.Name}　参加作品一覧"
            : item?.Author is { } author ? $"{author}さんの作品"
            : item?.Strays == true ? "その他のゲーム（wikiにない作品）"
            : item?.Server == true ? (item.Area == "*server" ? "サーバーの作品（スレ作品など）" : $"{item.Label}（アップローダー保存庫）")
            : $"手持ち（{OwnedSorts.FirstOrDefault(sort => sort.Key == Data.OwnedSort).Label ?? "タイトル順"}・{_side.FirstOrDefault(i => i.Filter == Data.OwnedFilter)?.Label.TrimStart('★', '■', ' ') ?? "すべて"}）";
        // The search box sits at the right of the heading.
        double searchWidth = Math.Min(200, Math.Floor(_listRect.Width * 0.42));
        _searchRect = new Rect(_listRect.Right - 8 - searchWidth, _listRect.Y + 5, searchWidth, 15);
        PaintHeading(g, heading, _listRect.Width - 24 - searchWidth, back: true);

        double x = _listRect.X + 6, width = _listRect.Width - 12;
        double noWidth = Data.List == VipListMode.Matsuri ? 32 : Math.Min(110, width * 0.22);
        double stateWidth = 22, authorWidth = Data.List == VipListMode.Author ? 0 : Math.Min(130, width * 0.28);
        double titleWidth = width - noWidth - stateWidth - authorWidth - 12;
        double headerY = _listRect.Y + 22;
        RetroDraw.Fill(g, new Rect(x, headerY + 15, width, 1), Color.FromArgb(120, 200, 210, 255));
        RetroDraw.Text(g, Data.List == VipListMode.Matsuri ? "No." : "祭り", x + 3, headerY + 1, RetroDraw.System);
        RetroDraw.Text(g, "タイトル", x + noWidth + stateWidth + 3, headerY + 1, RetroDraw.System);
        if (authorWidth > 0) RetroDraw.Text(g, "作者", x + width - authorWidth, headerY + 1, RetroDraw.System);

        var list = new Rect(x, headerY + 18, width, _listRect.Bottom - headerY - 26);
        _rowsVisible = Math.Max(1, (int)(list.Height / 16));
        _rowTop = Math.Clamp(_rowTop, 0, Math.Max(0, _rows.Count - _rowsVisible));
        if (_rowIndex < _rowTop) _rowTop = _rowIndex;
        if (_rowIndex >= _rowTop + _rowsVisible) _rowTop = _rowIndex - _rowsVisible + 1;

        if (_rows.Count == 0)
        {
            string empty = _scanning ? "調べています…" : "作品がありません";
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
                var color = owned ? RetroDraw.TextColor : RetroDraw.Disabled;
                RetroDraw.Text(g, RetroDraw.Fit(row.No, noWidth - 4), rect.X + 3, rect.Y + 2, owned ? RetroDraw.Yellow : RetroDraw.Disabled);
                bool fetching = row.Remote is not null && ReferenceEquals(row.Remote, _downloading);
                bool remote = !owned && row.Zip is null && row.Remote is not null;
                string state = running ? "●" : owned ? "▶" : row.Zip is not null ? "Z" : fetching ? (_tick / 4 % 2 == 0 ? "↓" : "") : remote ? "↓" : "・";
                RetroDraw.Text(g, state, rect.X + noWidth + 4, rect.Y + 2,
                    running ? RetroDraw.Red : owned ? RetroDraw.Green : row.Zip is not null ? RetroDraw.Yellow
                    : fetching ? RetroDraw.Yellow : remote ? RetroDraw.System : RetroDraw.Disabled);
                double titleX = rect.X + noWidth + stateWidth + 3;
                if (IsFavorite(row)) titleX += RetroDraw.Text(g, "★", titleX, rect.Y + 2, RetroDraw.Yellow);
                RetroDraw.Text(g, RetroDraw.Fit(row.Title, titleWidth - (titleX - rect.X - noWidth - stateWidth - 3)), titleX, rect.Y + 2, color);
                if (authorWidth > 0)
                    RetroDraw.Text(g, RetroDraw.Fit(row.Author, authorWidth - 4), rect.Right - authorWidth, rect.Y + 2,
                        owned ? RetroDraw.System : RetroDraw.Disabled);
                int index = i;
                _hot.Add(new VipHot(rect, () =>
                {
                    if (_focus == Pane.Rows && _rowIndex == index && row.Local is not null) Play(row);
                    _focus = Pane.Rows;
                    SelectRow(index);
                }));
            }
        }
        if (_rowTop > 0) RetroDraw.MoreArrow(g, _listRect.Center.X, headerY + 20, _tick, down: false);
        if (_rowTop + _rowsVisible < _rows.Count) RetroDraw.MoreArrow(g, _listRect.Center.X, _listRect.Bottom - 7, _tick);
    }

    private void PaintDetail(DrawingContext g, VipTheme theme)
    {
        RetroDraw.Window(g, _detailRect, theme.WindowTop, theme.WindowBottom);
        var row = SelectedRow;
        double x = _detailRect.X + 8, y = _detailRect.Y + 8, width = _detailRect.Width - 16;
        _pictureBitmap = null;
        if (row is null || _focus != Pane.Rows && _rows.Count == 0)
        {
            RetroDraw.Text(g, "作品を選んでね", x, y, RetroDraw.Disabled);
            return;
        }

        // Buttons first, so the picture can take the room the text and buttons leave.
        // Buttons, like a shop's command window.
        var buttons = new List<(string Label, Action Run, bool Enabled)>();
        if (row.Local is { } owned)
        {
            buttons.Add(("遊ぶ", () => Play(row), true));
            if (EngineFamilies.HasTestMode(owned.Family)) buttons.Add(("テスト", () => Play(row, testPlay: true), true));
            buttons.Add(("説明書", () => OpenReadme(owned), true));
            buttons.Add(("フォルダ", () => OpenFolder(owned.RootPath), true));
        }
        else if (row.Zip is { } zip)
        {
            buttons.Add(("展開", () => _ = ExtractAsync(zip), true));
            buttons.Add(("フォルダ", () => Shell.ShowInFileManager(zip), true));
        }
        else if (row.Remote is { } download)
        {
            buttons.Add((ReferenceEquals(download, _downloading) ? "中止" : "ダウンロード", () => _ = DownloadAsync(download), true));
        }
        if (row.Work?.Downloads?.FirstOrDefault() is { } original) buttons.Add(("配布元", () => OpenUrl(original), true));
        if (row.Work?.WikiUrl is { } wiki) buttons.Add(("wiki", () => OpenUrl(wiki), true));
        if (row.Work?.ForumUrl is { } forum) buttons.Add(("感想スレ", () => OpenUrl(forum), true));
        if (row.Work?.Event is { } eventInfo)
        {
            if (eventInfo.ArchiveUrl is { } archive) buttons.Add(("祭りページ", () => OpenUrl(archive), true));
            if (eventInfo.SiteUrl is { } site) buttons.Add(("公式サイト", () => OpenUrl(site), true));
            else if (eventInfo.WikiUrl is { } matsuriWiki) buttons.Add(("祭りwiki", () => OpenUrl(matsuriWiki), true));
        }

        // The picture: 4:3, as large as leaves room for six lines of text and the buttons.
        const int Columns = 4;
        int buttonRows = (buttons.Count + Columns - 1) / Columns;
        double room = _detailRect.Bottom - 8 - 18 * buttonRows - 4 - 6 - 16 - 15 * 5;
        double pictureWidth = Math.Floor(Math.Min(width, (room - y) * 4 / 3));
        var picture = new Rect(x + Math.Floor((width - pictureWidth) / 2), y, pictureWidth, Math.Floor(pictureWidth * 3 / 4));
        RetroDraw.Fill(g, picture.Inflate(1), RetroDraw.FrameOuter);
        RetroDraw.Fill(g, picture, Colors.Black);
        // The game's own title screen, else the picture from its matsuri's site.
        var bitmap = row.Local is { } game ? TitleFor(game) : null;
        if (bitmap is null && row.Work is { } pictured) bitmap = ThumbFor(pictured);
        _pictureBitmap = bitmap;
        _pictureRect = picture;
        if (bitmap is null)
        {
            string text = row.Local is not null ? "NO IMAGE" : "NO IMAGE（未所持）";
            RetroDraw.Text(g, text, picture.Center.X - RetroDraw.Width(text) / 2, picture.Center.Y - 6, RetroDraw.Disabled);
        }
        y = picture.Bottom + 6;

        // Lines that would run into the buttons are left out.
        double limit = _detailRect.Bottom - 8 - 18 * buttonRows - 4;
        double Add(string label, string value) => y + 16 > limit ? 0 : Line(g, label, value, x, y, width);
        string state = row.Local is not null ? "" : row.Zip is not null ? "  [zip]" : row.Remote is not null ? "  [未所持 ↓]" : "  [未所持]";
        double titleWidth = RetroDraw.Text(g, RetroDraw.Fit(row.Title, width - RetroDraw.Width(state)), x, y, RetroDraw.TextColor);
        RetroDraw.Text(g, state, x + titleWidth, y, row.Remote is not null && row.Local is null ? RetroDraw.System : RetroDraw.Disabled);
        y += 16;
        if (row.Work is { } work)
        {
            if (!string.IsNullOrWhiteSpace(work.Author)) y += Add("作者", work.Author!);
            if (work.Event is { } matsuri)
                y += Add("祭り", matsuri.Name + (string.IsNullOrWhiteSpace(work.No) ? "" : $"  No.{work.No}"));
            if (!string.IsNullOrWhiteSpace(work.Genre)) y += Add("ジャンル", work.Genre!);
        }
        else if (row.Remote is { } info)
        {
            if (!string.IsNullOrWhiteSpace(info.Author)) y += Add("作者", info.Author!);
            if (!string.IsNullOrWhiteSpace(info.Thread)) y += Add("スレ", info.Thread!);
            if (info.Year is { } year) y += Add("年", year.ToString());
            if (!string.IsNullOrWhiteSpace(info.Engine)) y += Add("ツール", info.Engine!);
            if (!string.IsNullOrWhiteSpace(info.Note)) y += Add("メモ", info.Note!);
        }
        if (row.Local is { } local)
        {
            y += Add("ツール", local.Engine);
            if (Data.Games.TryGetValue(local.RootPath, out var record) && record.PlayCount > 0)
                y += Add("プレイ", $"{record.PlayCount}回 ／ {Look.FormatDuration(Math.Max(1, record.PlaySeconds))}");
        }
        else if (row.Zip is null && row.Remote is { } server)
        {
            string size = ReferenceEquals(server, _downloading) && server.Size > 0
                ? $"{Look.FormatBytes(_downloaded)} / {Look.FormatBytes(server.Size)}"
                : server.Size > 0 ? Look.FormatBytes(server.Size) : "?";
            y += Add("サーバー", size);
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

    private static double Line(DrawingContext g, string label, string value, double x, double y, double width)
    {
        double labelWidth = RetroDraw.Text(g, label, x, y, RetroDraw.System) + 8;
        RetroDraw.Text(g, RetroDraw.Fit(value, width - Math.Max(labelWidth, 56)), x + Math.Max(labelWidth, 56), y, RetroDraw.TextColor);
        return 15;
    }

    /// <summary>A game's title screen, loaded in the background on first use.</summary>
    private Bitmap? TitleFor(GameEntry game) => Picture(game.RootPath, () => GameImageLoader.LoadSourcePicture(game));

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

    // A size not yet set is NaN, which no comparison would catch.
    private static bool Differs(double current, double wanted) => double.IsNaN(current) || Math.Abs(current - wanted) > 0.01;

    /// <summary>
    /// The picture over its box at the screen's resolution: pixel art (320×240 or smaller) by whole steps with
    /// hard edges, anything larger smoothly scaled to fit.
    /// </summary>
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
        double sourceWidth = bitmap.PixelSize.Width, sourceHeight = bitmap.PixelSize.Height;
        double fit = Math.Min(boxWidth / sourceWidth, boxHeight / sourceHeight);
        bool pixelArt = sourceWidth <= 320 && sourceHeight <= 240;
        if (pixelArt && fit >= 1) fit = Math.Floor(fit);
        double width = Math.Round(sourceWidth * fit) / scaling, height = Math.Round(sourceHeight * fit) / scaling;
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
        _mouse = ToCanvas(e.GetPosition(this));
        Redraw();
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
                if (row >= 0)
                {
                    _focus = Pane.Rows;
                    SelectRow(_rowTop + row);
                    if (SelectedRow is { } selected) OpenMenu(selected, point);
                    return;
                }
                int side = _sideAreas.FindIndex(area => area.Contains(point));
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

    /// <summary>After a wheel scroll, moves each cursor inside its window, as a list box does.</summary>
    private void KeepCursorsVisible()
    {
        _sideTop = Math.Clamp(_sideTop, 0, Math.Max(0, _side.Count - _sideVisible));
        _rowTop = Math.Clamp(_rowTop, 0, Math.Max(0, _rows.Count - _rowsVisible));
        if (_rowIndex < _rowTop) _rowIndex = _rowTop;
        if (_rowIndex >= _rowTop + _rowsVisible) _rowIndex = Math.Min(_rows.Count - 1, _rowTop + _rowsVisible - 1);
        if (_sideIndex < _sideTop || _sideIndex >= _sideTop + _sideVisible)
        {
            // The side cursor picks the list shown, so it stays put; scrolling just shows other entries.
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
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
                // The search box belongs to the works page.
                _focus = Pane.Rows;
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
            case Key.R when SelectedRow?.Local is { } local: OpenReadme(local); break;
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
                _focus = Pane.Rows;
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
        _focus = _focus == Pane.Rows ? Pane.Side : Pane.Commands;
        if (_focus == Pane.Commands) _command = Math.Max(0, _commands.FindIndex(c => c.Mode == Data.List));
        Redraw();
    }
}
