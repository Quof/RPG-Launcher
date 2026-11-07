using static RpgLauncher.VipText;

namespace RpgLauncher;

/// <summary>A clickable area of the VIPRPG screen, recorded while painting.</summary>
internal sealed record VipHot(Rect Area, Action Click, string? Hint = null);

/// <summary>A window drawn over the VIPRPG screen that takes the keys while it is open (settings, menus).</summary>
internal abstract class VipOverlay
{
    public Rect Area { get; protected set; }

    public bool Contains(Point point) => Area.Contains(point);

    public abstract void Paint(DrawingContext g, Size size, int tick, List<VipHot> hot, Point mouse);

    public virtual void Scroll(int steps)
    {
    }

    /// <summary>Handles a key; false closes the overlay.</summary>
    public abstract bool OnKey(Key key);

    /// <summary>A big window centred on the canvas.</summary>
    protected static Rect Centered(Size size, double width, double height) =>
        RetroDraw.Snap(new Rect((size.Width - width) / 2, (size.Height - height) / 2, width, height));
}

/// <summary>
/// VIPRPG mode's settings, on three tabs: 全般 (language, lists, right-click, mode), 起動 (how RPG Maker 2000/2003 and
/// WOLF RPG Editor games start) and フォルダ (downloads and the folders searched for games). Headings split each tab.
/// </summary>
internal sealed class VipSettings : VipOverlay
{
    /// <summary>A setting; one with no Run is a heading line, which the cursor steps over.</summary>
    private sealed record Entry(string Label, Func<string> Value, string Hint, Action? Run, bool Enabled = true, string? Id = null)
    {
        public bool Heading => Run is null;
    }

    private const double Line = 16;

    private static readonly (string Ja, string En)[] TabNames = { ("全般", "General"), ("起動", "Launch"), ("フォルダ", "Folders") };

    private readonly Rm2kOptions _options;
    private readonly VipData _data;
    private readonly Action _save;
    private readonly Action _rescan;
    private readonly VipView _owner;
    private readonly Action _switchMode;

    // The tab shown, kept while the launcher runs so 設定 opens again where it was left.
    private static int _tab;
    private int _index = -1;
    private int _top;

    // The wheel moved the view: the cursor follows it, rather than the view snapping back to the cursor.
    private bool _scrolled;

    public VipSettings(Rm2kOptions options, VipData data, Action save, Action rescan, VipView owner, Action switchMode)
    {
        _switchMode = switchMode;
        _options = options;
        _data = data;
        _save = save;
        _rescan = rescan;
        _owner = owner;
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static Entry Heading(string label) => new(label, () => "", "", null);

    private List<Entry> Entries(int tab) => tab switch
    {
        1 => Launch(),
        2 => Folders(),
        _ => General()
    };

    private List<Entry> General() => new()
    {
        Heading(T("表示", "Display")),
        new(T("言語", "Language"), () => _data.English ? "English" : "日本語",
            T("ランチャーの表示を日本語か英語に。作品名・作者・祭りの名前はそのまま。",
                "Show the launcher in Japanese or English. Game titles, authors and festival names stay as they are."), () =>
            {
                Toggle(() => _data.English = !_data.English);
                _owner.ApplyLanguage();
            }),
        new(T("クリア済みの作品", "Completed Games"), () => _data.ShowCompleted ? T("表示する", "Show") : T("表示しない", "Hide"),
            T("クリア済みにした作品を一覧に出すかどうか（作品を右クリック → クリア済みにする）。",
                "Show or hide the games you've marked completed (right-click a game → Mark Completed)."), () =>
            {
                Toggle(() => _data.ShowCompleted = !_data.ShowCompleted);
                _owner.RefreshList();
            }),
        Heading(T("操作", "Controls")),
        new(T("空いた所を右クリック", "Right-Click Blank Space"),
            () => _data.RightClickBlankOpensMenu ? T("メニューを開く", "Opens the menu") : T("前に戻る", "Goes back"),
            T("一覧の行の、文字のない所を右クリックした時：前に戻るか、その行のメニューを開くか。文字の上ならいつもメニュー。",
                "Right-clicking a list line away from its words: go back a page, or open that line's menu. On the words it always opens the menu."),
            () => Toggle(() => _data.RightClickBlankOpensMenu = !_data.RightClickBlankOpensMenu)),
        Heading(T("モード", "Mode")),
        new(T("HRPGモードに切り替える", "Switch to HRPG Mode"), () => "",
            T("EnterでHRPGモード（DLsiteのライブラリ）に切り替える。", "Enter switches to HRPG mode (the DLsite library)."), _switchMode)
    };

    private List<Entry> Launch()
    {
        bool rpgRt = !_options.UseEasyRpg;
        return new List<Entry>
        {
            Heading(T("ツクール2000/2003", "RPG Maker 2000/2003")),
            new(T("EasyRPGを使う", "Use EasyRPG"), () => _options.UseEasyRpg ? T("いつも", "Always")
                    : _options.EasyRpgFallback ? T("RPG_RT.exeがない時", "When there's no RPG_RT.exe") : T("使わない", "Never"),
                T("同梱のEasyRPG Playerを、すべての2000/2003作品に使うか、RPG_RT.exeのない作品だけに使うか、使わないか。",
                    "EasyRPG Player (bundled): for every 2000/2003 game, only for games without RPG_RT.exe, or never."), () => Toggle(() =>
                {
                    // いつも → RPG_RT.exeがない時 → 使わない → いつも
                    if (_options.UseEasyRpg) (_options.UseEasyRpg, _options.EasyRpgFallback) = (false, true);
                    else if (_options.EasyRpgFallback) _options.EasyRpgFallback = false;
                    else (_options.UseEasyRpg, _options.EasyRpgFallback) = (true, true);
                })),
            new("EasyRPG Player", () => _options.EasyRpgPath ?? T("同梱版 (EasyRPGフォルダ)", "Bundled (EasyRPG folder)"),
                T("EasyRPG Playerのプログラム。選ばなければ同梱版。作品にPlayer.exeがあればそちらが先。Delで戻す。",
                    "EasyRPG Player's program: the bundled one unless you choose another. A game's own Player.exe is used first. Del resets."),
                () => _ = PickEasyRpgAsync(), Id: "easyrpg"),
            new(T("ウィンドウ表示", "Windowed"), () => OnOff(_options.Window),
                T("フルスクリーンではなくウィンドウで起動する。", "Start in a window instead of full screen."), () => Toggle(() => _options.Window = !_options.Window)),
            Heading(T("RPG_RT.exeで遊ぶ時", "When Playing with RPG_RT.exe")),
            new(T("小さい画面で起動", "Small Window"), () => OnOff(_options.SmallWindow),
                T("RPG_RTを320x240の小さい画面にする（起動後にF5を押す）。", "Switch RPG_RT to its small 320x240 window (presses F5 once it is up)."),
                () => Toggle(() => _options.SmallWindow = !_options.SmallWindow), rpgRt),
            new(T("CPUを1つだけ使う", "Use One CPU Core"), () => OnOff(_options.SingleCpu),
                T("RPG_RTをCPU1つで動かす。速すぎたり固まったりする作品に効く。", "Keep RPG_RT on one CPU core: fixes games that run too fast or freeze."),
                () => Toggle(() => _options.SingleCpu = !_options.SingleCpu), rpgRt),
            new(T("メモスト対策", "Memory Stream Fix"), () => OnOff(_options.MemoryStreamFix),
                T("ゲーム中のRPG_RTのメモリーストリームエラーを防ぐ（Windows）。", "Patch out RPG_RT's memory stream error while the game runs (Windows)."),
                () => Toggle(() => _options.MemoryStreamFix = !_options.MemoryStreamFix), rpgRt && OperatingSystem.IsWindows()),
            new(T("ランタイム", "Runtime"), () => _options.RuntimeFolder ?? T("自動 (MoshimoLoaderを探す)", "Automatic (finds MoshimoLoader)"),
                T("RPG_RT.exeのない作品に使う、2000\\RPG_RT.exeと2003\\RPG_RT.exeの入ったフォルダ。Delで自動に戻す。",
                    "Folder with 2000\\RPG_RT.exe and 2003\\RPG_RT.exe for games shipped without one. Del resets to automatic."),
                () => _ = PickRuntimeAsync(), Id: "runtime"),
            Heading(T("WOLF RPGエディター", "WOLF RPG Editor")),
            new(T("スクショキー", "Screenshot Key"), () => OnOff(_data.WolfScreenshotKey),
                T("ウディタ作品の、キーで画面をpngに保存する機能。オフなら起動前にGame.iniのScreenShotFlagを0にする。",
                    "WOLF RPG Editor games' own key that saves the screen as a png. Off sets ScreenShotFlag=0 in the game's Game.ini before it starts."),
                () => Toggle(() => _data.WolfScreenshotKey = !_data.WolfScreenshotKey))
        };
    }

    private List<Entry> Folders()
    {
        var entries = new List<Entry>
        {
            Heading(T("ダウンロード", "Downloads")),
            new(T("ダウンロード先", "Download Folder"), () => _data.DownloadFolder ?? T("自動 (ランチャーのgamesフォルダ)", "Automatic (the launcher's games folder)"),
                T("ダウンロードした作品を保存・展開する場所。Delでランチャー横のgamesフォルダに戻す。",
                    "Where downloaded games are saved and unpacked. Del resets to the games folder beside the launcher."), () => _ = PickDownloadFolderAsync(), Id: "downloads"),
            Heading(T("ゲームフォルダ（作品を探す場所）", "Game Folders (searched for games)"))
        };
        foreach (string folder in _data.Directories.ToList())
        {
            entries.Add(new Entry(T("フォルダ", "Folder"), () => folder,
                T("Enterでこのフォルダを外す（ファイルは消えない）。", "Enter removes this folder from VIPRPG mode (nothing is deleted)."), () =>
                {
                    _data.Directories.Remove(folder);
                    _save();
                    _rescan();
                }));
        }
        entries.Add(new Entry(T("＋ フォルダを追加", "+ Add Folder"), () => "",
            T("ゲームが入っているフォルダを選んで追加する。", "Choose a folder that holds VIPRPG games to add it."), _owner.AddFolder));
        return entries;
    }

    private void Toggle(Action change)
    {
        change();
        _save();
    }

    private async Task PickRuntimeAsync()
    {
        string? folder = await _owner.PickAsync(folder: true, T("RPG_RT.exeの入ったランタイムフォルダ", "Runtime Folder with RPG_RT.exe"));
        if (folder is null) return;
        _options.RuntimeFolder = folder;
        _save();
    }

    private async Task PickEasyRpgAsync()
    {
        string? file = await _owner.PickAsync(folder: false, "EasyRPG Player (Player.exe)");
        if (file is null) return;
        _options.EasyRpgPath = file;
        _save();
    }

    private async Task PickDownloadFolderAsync()
    {
        string? folder = await _owner.PickAsync(folder: true, T("ダウンロードしたゲームを置くフォルダ", "Folder for Downloaded Games"));
        if (folder is null) return;
        _data.DownloadFolder = folder;
        _save();
    }

    /// <summary>The next line after <paramref name="from"/> that isn't a heading, going round the ends.</summary>
    private static int Next(List<Entry> entries, int from, int step)
    {
        for (int i = 1; i <= entries.Count; i++)
        {
            int index = ((from + step * i) % entries.Count + entries.Count) % entries.Count;
            if (!entries[index].Heading) return index;
        }
        return Math.Clamp(from, 0, entries.Count - 1);
    }

    /// <summary>The cursor's line on this tab: where it was, or the first setting.</summary>
    private int Chosen(List<Entry> entries)
    {
        if (_index < 0 || _index >= entries.Count || entries[_index].Heading) _index = Next(entries, -1, 1);
        return _index;
    }

    private void ShowTab(int tab)
    {
        _tab = (tab + TabNames.Length) % TabNames.Length;
        _index = -1;
        _top = 0;
    }

    public override void Scroll(int steps)
    {
        _top = Math.Max(0, _top + steps);
        _scrolled = true;
    }

    public override void Paint(DrawingContext g, Size size, int tick, List<VipHot> hot, Point mouse)
    {
        var entries = Entries(_tab);
        int chosen = Chosen(entries);
        // As tall as the longest tab, with the values lined up the same on every tab, so switching tabs doesn't move things.
        var tabs = Enumerable.Range(0, TabNames.Length).Select(Entries).ToList();
        int lines = tabs.Max(tab => tab.Count);
        Area = Centered(size, Math.Min(size.Width - 40, 640), Math.Min(size.Height - 40, 30 + lines * Line + 48));
        RetroDraw.Fill(g, new Rect(size), Color.FromArgb(120, 0, 0, 0));
        RetroDraw.Window(g, Area);

        // The title, then the tabs, and a close button at the right.
        double x = Area.X + 10;
        x += RetroDraw.Text(g, T("設定", "Settings"), x, Area.Y + 7, RetroDraw.System) + 14;
        for (int i = 0; i < TabNames.Length; i++)
        {
            string label = T(TabNames[i].Ja, TabNames[i].En);
            var tab = new Rect(x, Area.Y + 5, RetroDraw.Width(label) + 16, 17);
            bool open = i == _tab, hover = tab.Contains(mouse);
            RetroDraw.Fill(g, tab, open ? Color.FromArgb(150, 255, 255, 255) : hover ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(110, 0, 0, 0));
            RetroDraw.Outline(g, tab, open || hover ? Colors.White : Color.FromArgb(160, 255, 255, 255));
            RetroDraw.Text(g, label, tab.X + 8, tab.Y + 2, open || hover ? RetroDraw.TextColor : RetroDraw.System);
            int index = i;
            hot.Add(new VipHot(tab, () => ShowTab(index)));
            x = tab.Right + 4;
        }
        var close = new Rect(Area.Right - 25, Area.Y + 5, 17, 17);
        bool closeHover = close.Contains(mouse);
        if (closeHover) RetroDraw.Fill(g, close, Color.FromArgb(70, 255, 255, 255));
        RetroDraw.Text(g, "×", close.X + 3, close.Y + 2, closeHover ? Colors.White : RetroDraw.Yellow);
        hot.Add(new VipHot(close, _owner.CloseOverlay));
        var rule = Color.FromArgb(120, 200, 210, 255);
        RetroDraw.Fill(g, new Rect(Area.X + 6, Area.Y + 26, Area.Width - 12, 1), rule);

        // The tab's lines, scrolled to keep the cursor, and the heading just above it, in view.
        double listY = Area.Y + 30, footerY = Area.Bottom - 44;
        int visible = Math.Max(1, (int)((footerY - listY) / Line));
        if (_scrolled)
        {
            _scrolled = false;
            _top = Math.Clamp(_top, 0, Math.Max(0, entries.Count - visible));
            if (chosen < _top || chosen >= _top + visible)
            {
                int inView = Enumerable.Range(_top, Math.Min(visible, entries.Count - _top)).FirstOrDefault(i => !entries[i].Heading, -1);
                if (inView >= 0) _index = chosen = inView;
            }
        }
        int first = chosen > 0 && entries[chosen - 1].Heading ? chosen - 1 : chosen;
        if (first < _top) _top = first;
        if (chosen >= _top + visible) _top = chosen - visible + 1;
        _top = Math.Clamp(_top, 0, Math.Max(0, entries.Count - visible));
        double labelWidth = Math.Clamp(tabs.SelectMany(tab => tab).Where(entry => !entry.Heading).Max(entry => RetroDraw.Width(entry.Label)) + 32,
            160, Math.Floor(Area.Width * 0.45));
        for (int i = _top; i < Math.Min(entries.Count, _top + visible); i++)
        {
            var entry = entries[i];
            var row = new Rect(Area.X + 6, listY + (i - _top) * Line, Area.Width - 12, Line);
            if (entry.Heading)
            {
                // A heading: its name, with a rule out to the right edge.
                double width = RetroDraw.Text(g, entry.Label, row.X + 4, row.Y + 2, RetroDraw.System);
                RetroDraw.Fill(g, new Rect(row.X + 12 + width, Math.Floor(row.Center.Y), Math.Max(0, row.Width - 14 - width), 1),
                    Color.FromArgb(170, 152, 184, 255));
                continue;
            }
            if (i == chosen) RetroDraw.Cursor(g, row, tick);
            else if (row.Contains(mouse)) RetroDraw.Fill(g, row.Deflate(1), Color.FromArgb(40, 255, 255, 255));
            var color = entry.Enabled ? RetroDraw.TextColor : RetroDraw.Disabled;
            RetroDraw.Text(g, RetroDraw.Fit(entry.Label, labelWidth - 24), row.X + 16, row.Y + 2, color);
            string value = RetroDraw.Fit(entry.Value(), row.Width - labelWidth - 8);
            RetroDraw.Text(g, value, row.X + labelWidth, row.Y + 2, entry.Enabled ? RetroDraw.Yellow : RetroDraw.Disabled);
            int index = i;
            hot.Add(new VipHot(row, () =>
            {
                // The first click picks a setting (its hint shows below); clicking the picked one changes it.
                if (_index != index)
                {
                    _index = index;
                    return;
                }
                if (entry is { Enabled: true, Run: { } run }) run();
            }));
        }
        if (_top > 0) RetroDraw.MoreArrow(g, Area.Center.X, listY - 4, tick, down: false);
        if (_top + visible < entries.Count) RetroDraw.MoreArrow(g, Area.Center.X, footerY - 6, tick);

        // What the chosen setting does, and the keys.
        RetroDraw.Fill(g, new Rect(Area.X + 6, footerY + 2, Area.Width - 12, 1), rule);
        var selected = entries[chosen];
        string hint = selected.Enabled || !_options.UseEasyRpg ? selected.Hint
            : T("EasyRPGを「いつも」使う間は使われない。", "Not used while Use EasyRPG is Always.") + " " + selected.Hint;
        RetroDraw.Text(g, RetroDraw.Fit(hint, Area.Width - 20), Area.X + 10, footerY + 7, RetroDraw.System);
        string keys = T("←→ タブ　↑↓ 選ぶ　Enter・もう一度クリック 変更　Esc 閉じる", "←→ Tabs   ↑↓ Choose   Enter or Click Again Change   Esc Close") +
                      (selected.Id is not null ? T("　Del 元に戻す", "   Del Reset") : "");
        RetroDraw.Text(g, RetroDraw.Fit(keys, Area.Width - 20), Area.X + 10, footerY + 24, RetroDraw.Disabled);
    }

    public override bool OnKey(Key key)
    {
        var entries = Entries(_tab);
        int chosen = Chosen(entries);
        switch (key)
        {
            case Key.Up: _index = Next(entries, chosen, -1); break;
            case Key.Down: _index = Next(entries, chosen, 1); break;
            case Key.Left: ShowTab(_tab - 1); break;
            case Key.Right or Key.Tab: ShowTab(_tab + 1); break;
            case Key.Enter or Key.Space or Key.Z:
                if (entries[chosen] is { Enabled: true, Run: { } run }) run();
                break;
            case Key.Delete:
                string? id = entries[chosen].Id;
                if (id == "runtime") _options.RuntimeFolder = null;
                else if (id == "easyrpg") _options.EasyRpgPath = null;
                else if (id == "downloads") _data.DownloadFolder = null;
                else break;
                _save();
                break;
            case Key.Escape or Key.X or Key.Back: return false;
        }
        return true;
    }
}

/// <summary>A right-click menu: a small command window at the pointer, like RPG Maker's item command lists.</summary>
internal sealed class VipMenu : VipOverlay
{
    /// <summary>A command; a null action is a heading line.</summary>
    public sealed record Item(string Label, Action? Run, bool Enabled = true, bool Checked = false, Action? RightClick = null);

    private readonly List<Item> _items;
    private readonly Point _at;
    private readonly Action _close;
    private int _index;

    // Where each item was last drawn, for right-clicks.
    private readonly List<(Rect Area, Item Item)> _drawn = new();

    public VipMenu(Point at, List<Item> items, Action close)
    {
        _at = at;
        _items = items;
        _close = close;
        _index = Math.Max(0, items.FindIndex(item => item.Run is not null && item.Enabled));
    }

    public override void Paint(DrawingContext g, Size size, int tick, List<VipHot> hot, Point mouse)
    {
        double width = Math.Min(size.Width - 8, Math.Max(140, _items.Max(item => RetroDraw.Width(item.Label)) + 40));
        double height = _items.Count * 16 + 16;
        // Beside the pointer, kept on the canvas.
        double x = Math.Clamp(_at.X + 4, 4, size.Width - width - 4);
        double y = Math.Clamp(_at.Y - 8, 4, size.Height - height - 4);
        Area = RetroDraw.Snap(new Rect(x, y, width, height));
        RetroDraw.Window(g, Area);
        _drawn.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var row = new Rect(Area.X + 6, Area.Y + 8 + i * 16, Area.Width - 12, 16);
            if (item.Run is null)
            {
                // A heading splits the menu: centred, with a rule out to each edge.
                double labelWidth = RetroDraw.Width(item.Label);
                double labelX = Math.Floor(row.Center.X - labelWidth / 2), lineY = Math.Floor(row.Center.Y);
                var rule = Color.FromArgb(170, 152, 184, 255);
                RetroDraw.Fill(g, new Rect(row.X + 2, lineY, Math.Max(0, labelX - 6 - row.X - 2), 1), rule);
                RetroDraw.Fill(g, new Rect(labelX + labelWidth + 6, lineY, Math.Max(0, row.Right - 2 - labelX - labelWidth - 6), 1), rule);
                RetroDraw.Text(g, item.Label, labelX, row.Y + 2, RetroDraw.System);
                continue;
            }
            _drawn.Add((row, item));
            if (i == _index) RetroDraw.Cursor(g, row, tick);
            else if (row.Contains(mouse) && item.Enabled) RetroDraw.Fill(g, row.Deflate(1), Color.FromArgb(40, 255, 255, 255));
            string mark = item.Checked ? "\u2713" : "  ";
            RetroDraw.Text(g, mark + item.Label, row.X + 4, row.Y + 2, item.Enabled ? RetroDraw.TextColor : RetroDraw.Disabled);
            int index = i;
            hot.Add(new VipHot(row, () =>
            {
                _index = index;
                Choose();
            }));
        }
    }

    /// <summary>A right-click on an item that has its own right-click action (a group: delete it); false otherwise.</summary>
    public bool RightClickAt(Point point)
    {
        foreach (var (area, item) in _drawn)
        {
            if (!area.Contains(point) || item.RightClick is not { } action) continue;
            _close();
            action();
            return true;
        }
        return false;
    }

    private void Choose()
    {
        var item = _items[_index];
        if (item.Run is null || !item.Enabled) return;
        _close();
        item.Run();
    }

    private void Move(int delta)
    {
        for (int step = 0; step < _items.Count; step++)
        {
            _index = (_index + delta + _items.Count) % _items.Count;
            if (_items[_index].Run is not null && _items[_index].Enabled) return;
        }
    }

    public override bool OnKey(Key key)
    {
        switch (key)
        {
            case Key.Up: Move(-1); break;
            case Key.Down: Move(1); break;
            case Key.Enter or Key.Space or Key.Z: Choose(); break;
            case Key.Escape or Key.X or Key.Back: return false;
        }
        return true;
    }
}
