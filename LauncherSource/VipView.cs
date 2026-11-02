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

    private sealed record SideItem(string Label, int Owned, int Total, VipMatsuri? Matsuri, string? Author, bool Strays);

    private sealed record Row(VipWork? Work, GameEntry? Local, string No, string Title, string Author, string? Zip = null);

    private sealed record Command(string Label, string Hint, string HintEn, Action Run);

    /// <summary>Asks the window to switch modes (the "Mode: VIPRPG" command).</summary>
    public event Action<LauncherMode>? ModeRequested;

    private readonly LauncherStore _store;
    private readonly string _cachePath;
    private readonly Action _save;
    private readonly Func<IEnumerable<string>> _runtimeSearch;
    private readonly VipCatalog _catalog = VipCatalog.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly List<VipHot> _hot = new();
    private readonly Dictionary<string, Bitmap?> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _titlesLoading = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Command> _commands;
    private readonly VipParty _party = new();

    private List<GameEntry> _games = new();
    private List<GameEntry> _strays = new();
    private List<string> _strayZips = new();
    private bool _extracting;
    private List<SideItem> _side = new();
    private List<Row> _rows = new();
    private Pane _focus = Pane.Side;
    private int _command, _sideIndex, _sideTop, _rowIndex, _rowTop;
    private int _sideVisible = 1, _rowsVisible = 1;
    private bool _scanning, _started, _loadingCache;
    private int _tick;
    private VipParty.Member _speaker = VipParty.Leader;
    private string _say = "", _sayEn = "";
    private VipOverlay? _overlay;
    private Point _mouse = new(-100, -100);

    public VipView(LauncherStore store, string root, Action save, Func<IEnumerable<string>> runtimeSearch)
    {
        _store = store;
        _cachePath = Path.Combine(root, "launcher-vip-library.json");
        _save = save;
        _runtimeSearch = runtimeSearch;
        _commands = new List<Command>
        {
            new("祭り別", "祭りごとに作品を並べるよ。", "Works by matsuri.", () => SetList(VipListMode.Matsuri)),
            new("作者別", "作者ごとに作品を並べるよ。", "Works by author.", () => SetList(VipListMode.Author)),
            new("手持ち", "このPCにある作品だけ。", "Only the games on this PC.", () => SetList(VipListMode.Owned)),
            new("再スキャン", "フォルダを調べなおすよ。(F5)", "Look through the folders again. (F5)", () => _ = ScanAsync()),
            new("フォルダ追加", "ゲームが入っているフォルダを選んでね。", "Add a folder that holds VIPRPG games.", () => _ = AddFolderAsync()),
            new("設定", "ツクール2000/2003の起動方法やフォルダ。", "RPG Maker 2000/2003 options and folders.", ShowSettings),
            new("Mode: VIPRPG", "HRPGモードに戻るよ。", "Back to HRPG mode.", () => ModeRequested?.Invoke(LauncherMode.Hrpg))
        };
        _timer.Tick += (_, _) =>
        {
            _tick++;
            _party.Step(CanvasSize.Width);
            Redraw();
        };
        Say("VIPRPG保管庫へようこそ！ 祭りを選んで、作品を遊ぼう。",
            "Welcome to the VIPRPG archive! Pick a matsuri, then a game. Enter plays, Esc goes back.");
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
        Data.Visitors++;
        _save();
        _ = LoadAsync();
    }

    /// <summary>Shows the last scan at once, then looks for changes.</summary>
    private async Task LoadAsync()
    {
        _loadingCache = true;
        try
        {
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
    }

    private void Show(List<GameEntry> games, List<string> zips)
    {
        _games = games;
        _strays = _catalog.Match(games, zips);
        // Zips no work claimed and nobody extracted yet: listed with the other games.
        var claimed = new HashSet<string>(_catalog.Games.Select(w => w.LocalZip).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        _strayZips = zips.Where(zip => !claimed.Contains(zip) && !VipZip.LooksExtracted(zip)).ToList();
        RebuildSide();
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
        Data.List = mode;
        Data.Selected = null;
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
                break;
            case VipListMode.Author:
                foreach (var (author, works) in _catalog.ByAuthor())
                    side.Add(new SideItem(author, works.Count(w => w.Local is not null), works.Count, null, author, false));
                break;
            default:
                side.Add(new SideItem("手持ちの作品", _games.Count, _games.Count, null, null, false));
                break;
        }
        _side = side;
        int index = Data.Selected is { } key ? side.FindIndex(item => SideKey(item) == key) : -1;
        _sideIndex = Math.Max(0, index);
        _sideTop = Math.Max(0, _sideIndex - _sideVisible / 2);
        RebuildRows();
    }

    private static string SideKey(SideItem item) => item.Matsuri?.Id ?? item.Author ?? (item.Strays ? "*strays" : "*owned");

    private void RebuildRows()
    {
        var rows = new List<Row>();
        var item = _sideIndex < _side.Count ? _side[_sideIndex] : null;
        if (item?.Matsuri is { } matsuri)
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
        else if (item is not null)
        {
            var matched = _catalog.Games.Where(w => w.Local is not null).Select(w => RowFor(w, Short(w.Event)));
            var others = _strays.Select(g => new Row(null, g, "", g.DisplayTitle, ""));
            rows.AddRange(matched.Concat(others).OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase));
        }
        _rows = rows;
        _rowIndex = Math.Clamp(_rowIndex, 0, Math.Max(0, rows.Count - 1));
        _rowTop = Math.Clamp(_rowTop, 0, Math.Max(0, rows.Count - 1));
        Redraw();
    }

    private static Row RowFor(VipWork work, string? no) => new(work, work.Local, no ?? "", work.Title, work.Author ?? "", work.LocalZip);

    private static string Short(VipMatsuri? matsuri) => matsuri?.ShortName ?? "";

    private Row? SelectedRow => _rowIndex >= 0 && _rowIndex < _rows.Count ? _rows[_rowIndex] : null;

    // ---- actions ----

    /// <summary>Puts a line in the message window, said by Alex unless someone else is given.</summary>
    private void Say(string japanese, string english, VipParty.Member? speaker = null)
    {
        _speaker = speaker ?? VipParty.Leader;
        _say = japanese;
        _sayEn = english;
        Redraw();
    }

    private void SelectSide(int index)
    {
        if (_side.Count == 0) return;
        _sideIndex = Math.Clamp(index, 0, _side.Count - 1);
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
        _overlay = new VipSettings(_store.Data.Rm2k, Data, _save, () => _ = ScanAsync(), this);
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

    private Rect _sideRect, _rowsRect, _detailRect, _commandRect, _messageRect, _bannerRect;

    protected override void Paint(DrawingContext g, Size size)
    {
        _hot.Clear();
        double w = size.Width, h = size.Height;
        VipTheme theme = VipTheme.For(_sideIndex < _side.Count ? _side[_sideIndex].Matsuri : null);

        // The page behind everything: the dark tiled background of an old fan site.
        g.FillRectangle(theme.PageBrush, new Rect(0, 0, w, h));

        PaintMarquee(g, new Rect(0, 0, w, 14), theme);
        _bannerRect = new Rect(0, 14, w, 52);
        PaintBanner(g, _bannerRect, theme);
        PaintGround(g, new Rect(0, 66, w, 40), theme);

        double top = 110, bottom = h - 70;
        double commandWidth = Math.Clamp(w * 0.2, 150, 220);
        double commandHeight = _commands.Count * 16 + 16;
        _commandRect = new Rect(4, top, commandWidth, commandHeight);
        _sideRect = new Rect(4, top + commandHeight + 4, commandWidth, Math.Max(40, bottom - top - commandHeight - 4));
        double detailWidth = w >= 900 ? 336 : Math.Clamp(w * 0.3, 176, 336);
        _detailRect = new Rect(w - detailWidth - 4, top, detailWidth, bottom - top);
        _rowsRect = new Rect(_commandRect.Right + 4, top, _detailRect.X - _commandRect.Right - 8, bottom - top);
        _messageRect = new Rect(4, h - 66, w - 8, 62);

        PaintCommands(g, theme);
        PaintSide(g, theme);
        PaintRows(g, theme);
        PaintDetail(g, theme);
        PaintMessage(g, theme);
        _party.Paint(g, 66, _tick);

        _overlay?.Paint(g, size, _tick, _hot, _mouse);
    }

    private void PaintMarquee(DrawingContext g, Rect rect, VipTheme theme)
    {
        RetroDraw.Fill(g, rect, Colors.Black);
        int owned = _catalog.Games.Count(work => work.Local is not null);
        string text = $"★ VIPRPG保管庫へようこそ ★ 収録祭り {_catalog.Matsuri.Count} 件 ／ 作品 {_catalog.Games.Count} 本 ／ 手持ち {owned} 本 " +
                      "★ Enterで遊ぶ・Escで戻る・F5で再スキャン・R説明書・Tテスト・Fフォルダ・Wwiki ★ 作品データ：VIPRPG全祭り補完サイト・VIPRPG wiki ★ キャラ：RPGツクール2000 RTP ★ ";
        double width = RetroDraw.Width(text);
        double offset = width <= 0 ? 0 : (_tick * 2) % width;
        using (g.PushClip(rect))
        {
            for (double x = rect.X - offset; x < rect.Right; x += width)
                RetroDraw.Text(g, text, x, rect.Y + 1, theme.Marquee, shadow: false);
        }
    }

    private void PaintBanner(DrawingContext g, Rect rect, VipTheme theme)
    {
        theme.PaintBanner(g, rect, _tick);
        double logo = RetroDraw.Logo(g, "VIPRPG", rect.X + 12, rect.Y + 4, RetroDraw.Large, theme.Title, theme.LogoHighlight, theme.LogoOutline);
        RetroDraw.Logo(g, "保管庫", rect.X + 20 + logo, rect.Y + 4, RetroDraw.Large, theme.LogoSecond, theme.LogoHighlight, theme.LogoOutline);
        RetroDraw.Text(g, "～ みんなのツクール祭り作品を遊ぶランチャー ～", rect.X + 12, rect.Y + 34, theme.Subtitle);

        // The access counter every matsuri site had.
        string digits = Data.Visitors.ToString("D7");
        string label = "あなたは";
        string after = "人目の勇者です";
        double cell = 9, x = rect.Right - 10 - RetroDraw.Width(after) - digits.Length * cell - RetroDraw.Width(label) - 8;
        double y = rect.Y + 34;
        if (x < rect.X + 360) return;
        x += RetroDraw.Text(g, label, x, y, theme.Subtitle) + 4;
        foreach (char digit in digits)
        {
            RetroDraw.Fill(g, new Rect(x, y - 1, cell - 1, 14), Colors.Black);
            RetroDraw.Text(g, digit.ToString(), x + 1, y, RetroDraw.Green, shadow: false);
            x += cell;
        }
        RetroDraw.Text(g, after, x + 4, y, theme.Subtitle);
    }

    private static void PaintGround(DrawingContext g, Rect rect, VipTheme theme)
    {
        // Sky above the path, then a strip of grass with darker tufts.
        RetroDraw.Fill(g, rect, theme.Sky);
        var grass = new Rect(rect.X, rect.Bottom - 10, rect.Width, 10);
        RetroDraw.Fill(g, grass, Color.FromRgb(56, 152, 48));
        RetroDraw.Fill(g, new Rect(rect.X, grass.Y, rect.Width, 1), Color.FromRgb(120, 208, 88));
        for (double x = rect.X + 3; x < rect.Right; x += 13)
        {
            RetroDraw.Fill(g, new Rect(x, grass.Y + 3 + x % 3, 1, 2), Color.FromRgb(32, 104, 32));
            RetroDraw.Fill(g, new Rect(x + 2, grass.Y + 2 + x % 3, 1, 3), Color.FromRgb(32, 104, 32));
        }
    }

    private void PaintCommands(DrawingContext g, VipTheme theme)
    {
        RetroDraw.Window(g, _commandRect, theme.WindowTop, theme.WindowBottom);
        for (int i = 0; i < _commands.Count; i++)
        {
            var row = new Rect(_commandRect.X + 6, _commandRect.Y + 8 + i * 16, _commandRect.Width - 12, 16);
            bool active = _focus == Pane.Commands && _command == i;
            bool current = i < 3 && (int)Data.List == i;
            if (active || (row.Contains(_mouse) && _overlay is null)) RetroDraw.Cursor(g, row, _tick, active);
            RetroDraw.Text(g, (current ? "▶" : "  ") + _commands[i].Label, row.X + 4, row.Y + 2,
                i == _commands.Count - 1 ? RetroDraw.Yellow : RetroDraw.TextColor);
            int index = i;
            _hot.Add(new VipHot(row, () =>
            {
                _command = index;
                _focus = Pane.Commands;
                _commands[index].Run();
            }, _commands[i].HintEn));
        }
    }

    private void PaintSide(DrawingContext g, VipTheme theme)
    {
        RetroDraw.Window(g, _sideRect, theme.WindowTop, theme.WindowBottom);
        string heading = Data.List switch
        {
            VipListMode.Matsuri => "祭り一覧",
            VipListMode.Author => "作者一覧",
            _ => "手持ち"
        };
        RetroDraw.Text(g, heading, _sideRect.X + 8, _sideRect.Y + 6, RetroDraw.System);
        var list = new Rect(_sideRect.X + 6, _sideRect.Y + 22, _sideRect.Width - 12, _sideRect.Height - 30);
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
                if (i == _sideIndex) RetroDraw.Cursor(g, row, _tick, _focus == Pane.Side);
                else if (row.Contains(_mouse) && _overlay is null) RetroDraw.Fill(g, row.Deflate(1), Color.FromArgb(40, 255, 255, 255));
                string count = item.Strays ? $"{item.Total}" : $"{item.Owned}/{item.Total}";
                double countWidth = RetroDraw.Width(count);
                bool isNew = item.Matsuri?.Year >= DateTime.Now.Year - 1 && _tick / 8 % 2 == 0;
                string label = RetroDraw.Fit(item.Label, row.Width - countWidth - (isNew ? 30 : 10));
                double labelWidth = RetroDraw.Text(g, label, row.X + 3, row.Y + 2, item.Owned > 0 ? RetroDraw.TextColor : RetroDraw.Disabled);
                if (isNew) RetroDraw.Text(g, "NEW", row.X + 6 + labelWidth, row.Y + 2, RetroDraw.Red);
                RetroDraw.Text(g, count, row.Right - countWidth - 3, row.Y + 2, item.Owned > 0 ? RetroDraw.Yellow : RetroDraw.Disabled);
                int index = i;
                _hot.Add(new VipHot(row, () =>
                {
                    _focus = Pane.Side;
                    SelectSide(index);
                }));
            }
        }
        if (_sideTop > 0) RetroDraw.MoreArrow(g, _sideRect.Center.X, _sideRect.Y + 16, _tick, down: false);
        if (_sideTop + _sideVisible < _side.Count) RetroDraw.MoreArrow(g, _sideRect.Center.X, _sideRect.Bottom - 7, _tick);
    }

    private void PaintRows(DrawingContext g, VipTheme theme)
    {
        RetroDraw.Window(g, _rowsRect, theme.WindowTop, theme.WindowBottom);
        var item = _sideIndex < _side.Count ? _side[_sideIndex] : null;
        string heading = item?.Matsuri is { } matsuri ? $"{matsuri.Name}　参加作品一覧"
            : item?.Author is { } author ? $"{author}さんの作品"
            : item?.Strays == true ? "その他のゲーム（wikiにない作品）"
            : "手持ちの作品";
        RetroDraw.Text(g, RetroDraw.Fit(heading, _rowsRect.Width - 16), _rowsRect.X + 8, _rowsRect.Y + 6, RetroDraw.System);

        double x = _rowsRect.X + 6, width = _rowsRect.Width - 12;
        double noWidth = Data.List == VipListMode.Matsuri ? 32 : Math.Min(110, width * 0.22);
        double stateWidth = 22, authorWidth = Data.List == VipListMode.Author ? 0 : Math.Min(130, width * 0.28);
        double titleWidth = width - noWidth - stateWidth - authorWidth - 12;
        double headerY = _rowsRect.Y + 22;
        RetroDraw.Fill(g, new Rect(x, headerY + 15, width, 1), Color.FromArgb(120, 200, 210, 255));
        RetroDraw.Text(g, Data.List == VipListMode.Matsuri ? "No." : "祭り", x + 3, headerY + 1, RetroDraw.System);
        RetroDraw.Text(g, "タイトル", x + noWidth + stateWidth + 3, headerY + 1, RetroDraw.System);
        if (authorWidth > 0) RetroDraw.Text(g, "作者", x + width - authorWidth, headerY + 1, RetroDraw.System);

        var list = new Rect(x, headerY + 18, width, _rowsRect.Bottom - headerY - 26);
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
                if (i == _rowIndex) RetroDraw.Cursor(g, rect, _tick, _focus == Pane.Rows);
                else if (rect.Contains(_mouse) && _overlay is null) RetroDraw.Fill(g, rect.Deflate(1), Color.FromArgb(40, 255, 255, 255));

                bool owned = row.Local is not null;
                bool running = owned && _running.GetValueOrDefault(row.Local!.RootPath) > 0;
                var color = owned ? RetroDraw.TextColor : RetroDraw.Disabled;
                RetroDraw.Text(g, RetroDraw.Fit(row.No, noWidth - 4), rect.X + 3, rect.Y + 2, owned ? RetroDraw.Yellow : RetroDraw.Disabled);
                string state = running ? "●" : owned ? "▶" : row.Zip is not null ? "Z" : "・";
                RetroDraw.Text(g, state, rect.X + noWidth + 4, rect.Y + 2,
                    running ? RetroDraw.Red : owned ? RetroDraw.Green : row.Zip is not null ? RetroDraw.Yellow : RetroDraw.Disabled);
                RetroDraw.Text(g, RetroDraw.Fit(row.Title, titleWidth), rect.X + noWidth + stateWidth + 3, rect.Y + 2, color);
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
        if (_rowTop > 0) RetroDraw.MoreArrow(g, _rowsRect.Center.X, headerY + 20, _tick, down: false);
        if (_rowTop + _rowsVisible < _rows.Count) RetroDraw.MoreArrow(g, _rowsRect.Center.X, _rowsRect.Bottom - 7, _tick);
    }

    private void PaintDetail(DrawingContext g, VipTheme theme)
    {
        RetroDraw.Window(g, _detailRect, theme.WindowTop, theme.WindowBottom);
        var row = SelectedRow;
        double x = _detailRect.X + 8, y = _detailRect.Y + 8, width = _detailRect.Width - 16;
        if (row is null)
        {
            RetroDraw.Text(g, "作品を選んでね", x, y, RetroDraw.Disabled);
            return;
        }

        // The title screen at RPG Maker 2000's own 320×240 when it fits, else half that.
        double scale = width >= 320 && _detailRect.Height >= 240 + 150 ? 1 : 0.5;
        var picture = new Rect(x + Math.Floor((width - 320 * scale) / 2), y, 320 * scale, 240 * scale);
        RetroDraw.Fill(g, picture.Inflate(1), RetroDraw.FrameOuter);
        RetroDraw.Fill(g, picture, Colors.Black);
        if (row.Local is { } game && TitleFor(game) is { } bitmap)
        {
            var source = new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            double fit = Math.Min(picture.Width / source.Width, picture.Height / source.Height);
            var dest = new Rect(picture.Center.X - source.Width * fit / 2, picture.Center.Y - source.Height * fit / 2,
                source.Width * fit, source.Height * fit);
            g.DrawImage(bitmap, source, RetroDraw.Snap(dest));
        }
        else
        {
            string text = row.Local is null ? "未所持" : "NO IMAGE";
            RetroDraw.Text(g, text, picture.Center.X - RetroDraw.Width(text) / 2, picture.Center.Y - 6, RetroDraw.Disabled);
        }
        y = picture.Bottom + 6;

        RetroDraw.Text(g, RetroDraw.Fit(row.Title, width), x, y, RetroDraw.TextColor);
        y += 16;
        if (row.Work is { } work)
        {
            if (!string.IsNullOrWhiteSpace(work.Author)) y += Line(g, "作者", work.Author!, x, y, width);
            if (work.Event is { } matsuri)
                y += Line(g, "祭り", matsuri.Name + (string.IsNullOrWhiteSpace(work.No) ? "" : $"  No.{work.No}"), x, y, width);
            if (!string.IsNullOrWhiteSpace(work.Genre)) y += Line(g, "ジャンル", work.Genre!, x, y, width);
        }
        if (row.Local is { } local)
        {
            y += Line(g, "ツール", local.Engine, x, y, width);
            if (Data.Games.TryGetValue(local.RootPath, out var record) && record.PlayCount > 0)
                y += Line(g, "プレイ", $"{record.PlayCount}回 ／ {Look.FormatDuration(Math.Max(1, record.PlaySeconds))}", x, y, width);
        }

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
        if (row.Work?.WikiUrl is { } wiki) buttons.Add(("wiki", () => OpenUrl(wiki), true));
        if (row.Work?.ForumUrl is { } forum) buttons.Add(("感想スレ", () => OpenUrl(forum), true));
        if (row.Work?.Event is { } eventInfo)
        {
            if (eventInfo.ArchiveUrl is { } archive) buttons.Add(("祭りページ", () => OpenUrl(archive), true));
            if (eventInfo.SiteUrl is { } site) buttons.Add(("公式サイト", () => OpenUrl(site), true));
            else if (eventInfo.WikiUrl is { } matsuriWiki) buttons.Add(("祭りwiki", () => OpenUrl(matsuriWiki), true));
        }

        double bx = x, by = Math.Max(y + 4, _detailRect.Bottom - 8 - 18 * ((buttons.Count + 2) / 3));
        double buttonWidth = Math.Floor((width - 8) / 3);
        for (int i = 0; i < buttons.Count; i++)
        {
            var rect = new Rect(bx + i % 3 * (buttonWidth + 4), by + i / 3 * 18, buttonWidth, 16);
            bool hover = rect.Contains(_mouse) && _overlay is null;
            RetroDraw.Fill(g, rect, hover ? Color.FromArgb(80, 255, 255, 255) : Color.FromArgb(90, 0, 0, 0));
            RetroDraw.Outline(g, rect, hover ? Colors.White : Color.FromArgb(150, 255, 255, 255));
            var (label, run, _) = buttons[i];
            RetroDraw.Text(g, label, rect.Center.X - RetroDraw.Width(label) / 2, rect.Y + 2, i == 0 && row.Local is not null ? RetroDraw.Yellow : RetroDraw.TextColor);
            _hot.Add(new VipHot(rect, run));
        }
    }

    private static double Line(DrawingContext g, string label, string value, double x, double y, double width)
    {
        double labelWidth = RetroDraw.Text(g, label, x, y, RetroDraw.System) + 8;
        RetroDraw.Text(g, RetroDraw.Fit(value, width - Math.Max(labelWidth, 56)), x + Math.Max(labelWidth, 56), y, RetroDraw.TextColor);
        return 15;
    }

    private void PaintMessage(DrawingContext g, VipTheme theme)
    {
        RetroDraw.Window(g, _messageRect, theme.WindowTop, theme.WindowBottom);
        double x = _messageRect.X + 8;
        if (_speaker.Face >= 0 && VipSprites.Faces is { } faces && VipSprites.Face(faces, _speaker.Face) is { } face)
        {
            RetroDraw.Sprite(g, faces, face, _messageRect.X + 7, _messageRect.Y + 7);
            x += 56;
        }
        double width = _messageRect.Right - x - 8;
        RetroDraw.Text(g, RetroDraw.Fit($"{_speaker.Name}「{_say}」", width), x, _messageRect.Y + 9, RetroDraw.TextColor);
        RetroDraw.Text(g, RetroDraw.Fit(_sayEn, width), x, _messageRect.Y + 27, RetroDraw.System);
        if (_tick / 6 % 2 == 0) RetroDraw.MoreArrow(g, _messageRect.Right - 12, _messageRect.Bottom - 10, _tick);
    }

    /// <summary>A game's title screen, loaded in the background on first use.</summary>
    private Bitmap? TitleFor(GameEntry game)
    {
        if (_titles.TryGetValue(game.RootPath, out var cached)) return cached;
        if (_titlesLoading.Add(game.RootPath))
        {
            Task.Run(() =>
            {
                Bitmap? bitmap = null;
                try { bitmap = GameImageLoader.LoadSourcePicture(game); }
                catch (Exception ex) { Diagnostics.Log("VIPRPG title not loaded: " + ex.Message); }
                Dispatcher.UIThread.Post(() =>
                {
                    _titles[game.RootPath] = bitmap;
                    if (_titles.Count > 200)
                    {
                        foreach (var key in _titles.Keys.Where(k => k != game.RootPath).Take(50).ToList())
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
            Back();
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        if (_party.Hit(point) is { } line)
        {
            Say(line.Japanese, line.English, line.Member);
            e.Handled = true;
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
        else if (_sideRect.Contains(point)) _sideTop = Math.Max(0, _sideTop + steps);
        else if (_rowsRect.Contains(point)) _rowTop = Math.Max(0, _rowTop + steps);
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
            case Key.Left:
                _focus = _focus == Pane.Rows ? Pane.Side : Pane.Commands;
                break;
            case Key.Right:
            case Key.Tab:
                _focus = _focus == Pane.Commands ? Pane.Side : Pane.Rows;
                break;
            case Key.Enter or Key.Space or Key.Z: Confirm(); break;
            case Key.Escape or Key.X or Key.Back: Back(); break;
            case Key.F5: _ = ScanAsync(); break;
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
                Say(_commands[_command].Hint, _commands[_command].HintEn);
                break;
            case Pane.Side:
                SelectSide(_sideIndex + delta);
                break;
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
        _focus = _focus == Pane.Rows ? Pane.Side : Pane.Commands;
        Redraw();
    }
}
