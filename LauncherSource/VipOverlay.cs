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

/// <summary>VIPRPG mode's settings: how RPG Maker 2000/2003 games start, and the game folders.</summary>
internal sealed class VipSettings : VipOverlay
{
    private sealed record Entry(string Label, Func<string> Value, string Hint, Action Run, bool Enabled = true, string? Id = null);

    private readonly Rm2kOptions _options;
    private readonly VipData _data;
    private readonly Action _save;
    private readonly Action _rescan;
    private readonly VipView _owner;
    private int _index;

    private readonly Action _switchMode;

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

    private List<Entry> Entries()
    {
        bool rpgRt = !_options.UseEasyRpg;
        var entries = new List<Entry>
        {
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
            new(T("ウィンドウ表示", "Windowed"), () => OnOff(_options.Window),
                T("フルスクリーンではなくウィンドウで起動する。", "Start in a window instead of full screen."), () => Toggle(() => _options.Window = !_options.Window)),
            new(T("小さい画面で起動", "Small Window"), () => OnOff(_options.SmallWindow),
                T("RPG_RTを320x240の小さい画面にする（起動後にF5を押す）。", "Switch RPG_RT to its small 320x240 window (presses F5 once it is up)."), () => Toggle(() => _options.SmallWindow = !_options.SmallWindow), rpgRt),
            new(T("CPUを1つだけ使う", "Use One CPU Core"), () => OnOff(_options.SingleCpu),
                T("RPG_RTをCPU1つで動かす。速すぎたり固まったりする作品に効く。", "Keep RPG_RT on one CPU core: fixes games that run too fast or freeze."), () => Toggle(() => _options.SingleCpu = !_options.SingleCpu), rpgRt),
            new(T("メモスト対策", "Memory Stream Fix"), () => OnOff(_options.MemoryStreamFix),
                T("ゲーム中のRPG_RTのメモリーストリームエラーを防ぐ（Windows）。", "Patch out RPG_RT's memory stream error while the game runs (Windows)."), () => Toggle(() => _options.MemoryStreamFix = !_options.MemoryStreamFix), rpgRt && OperatingSystem.IsWindows()),
            new(T("ランタイム", "Runtime"), () => _options.RuntimeFolder ?? T("自動 (MoshimoLoaderを探す)", "Automatic (finds MoshimoLoader)"),
                T("RPG_RT.exeのない作品に使う、2000\\RPG_RT.exeと2003\\RPG_RT.exeの入ったフォルダ。Delで自動に戻す。",
                    "Folder with 2000\\RPG_RT.exe and 2003\\RPG_RT.exe for games shipped without one. Del resets to automatic."),
                () => _ = PickRuntimeAsync(), Id: "runtime"),
            new("EasyRPG Player", () => _options.EasyRpgPath ?? T("同梱版 (EasyRPGフォルダ)", "Bundled (EasyRPG folder)"),
                T("EasyRPG Playerのプログラム。選ばなければ同梱版。作品にPlayer.exeがあればそちらが先。Delで戻す。",
                    "EasyRPG Player's program: the bundled one unless you choose another. A game's own Player.exe is used first. Del resets."),
                () => _ = PickEasyRpgAsync(), Id: "easyrpg")
        };
        entries.Add(new Entry(T("クリア済みの作品", "Completed Games"), () => _data.ShowCompleted ? T("表示する", "Show") : T("表示しない", "Hide"),
            T("クリア済みにした作品を一覧に出すかどうか（作品を右クリック → クリア済みにする）。",
                "Show or hide the games you've marked completed (right-click a game → Mark Completed)."), () =>
            {
                Toggle(() => _data.ShowCompleted = !_data.ShowCompleted);
                _owner.RefreshList();
            }));
        entries.Add(new Entry(T("言語", "Language"), () => _data.English ? "English" : "日本語",
            T("ランチャーの表示を日本語か英語に。作品名・作者・祭りの名前はそのまま。",
                "Show the launcher in Japanese or English. Game titles, authors and festival names stay as they are."), () =>
            {
                Toggle(() => _data.English = !_data.English);
                _owner.ApplyLanguage();
            }));
        entries.Add(new Entry(T("ダウンロード先", "Download Folder"), () => _data.DownloadFolder ?? T("自動 (ランチャーのgamesフォルダ)", "Automatic (the launcher's games folder)"),
            T("ダウンロードした作品を保存・展開する場所。Delでランチャー横のgamesフォルダに戻す。",
                "Where downloaded games are saved and unpacked. Del resets to the games folder beside the launcher."), () => _ = PickDownloadFolderAsync(), Id: "downloads"));
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
        entries.Add(new Entry(T("モード", "Mode"), () => "VIPRPG",
            T("EnterでHRPGモード（DLsiteのライブラリ）に切り替える。", "Enter switches to HRPG mode (the DLsite library)."), _switchMode));
        entries.Add(new Entry(T("閉じる", "Close"), () => "", T("設定を閉じる。", "Close the settings."), _owner.CloseOverlay));
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

    public override void Paint(DrawingContext g, Size size, int tick, List<VipHot> hot, Point mouse)
    {
        var entries = Entries();
        _index = Math.Clamp(_index, 0, entries.Count - 1);
        Area = Centered(size, Math.Min(size.Width - 40, 640), Math.Min(size.Height - 40, entries.Count * 16 + 60));
        RetroDraw.Fill(g, new Rect(size), Color.FromArgb(120, 0, 0, 0));
        RetroDraw.Window(g, Area);
        RetroDraw.Text(g, T("設定 ― ツクール2000/2003の起動", "Settings — RPG Maker 2000/2003 Launch"), Area.X + 10, Area.Y + 6, RetroDraw.System);

        double labelWidth = 140;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var row = new Rect(Area.X + 6, Area.Y + 24 + i * 16, Area.Width - 12, 16);
            if (i == _index) RetroDraw.Cursor(g, row, tick);
            else if (row.Contains(mouse)) RetroDraw.Fill(g, row.Deflate(1), Color.FromArgb(40, 255, 255, 255));
            var color = entry.Enabled ? RetroDraw.TextColor : RetroDraw.Disabled;
            RetroDraw.Text(g, entry.Label, row.X + 4, row.Y + 2, color);
            string value = RetroDraw.Fit(entry.Value(), row.Width - labelWidth - 8);
            RetroDraw.Text(g, value, row.X + labelWidth, row.Y + 2, entry.Enabled ? RetroDraw.Yellow : RetroDraw.Disabled);
            int index = i;
            hot.Add(new VipHot(row, () =>
            {
                _index = index;
                if (entry.Enabled) entry.Run();
            }));
        }
        string hint = entries[_index].Hint;
        RetroDraw.Text(g, RetroDraw.Fit(hint, Area.Width - 20), Area.X + 10, Area.Bottom - 18, RetroDraw.System);
    }

    public override bool OnKey(Key key)
    {
        var entries = Entries();
        switch (key)
        {
            case Key.Up: _index = (_index + entries.Count - 1) % entries.Count; break;
            case Key.Down: _index = (_index + 1) % entries.Count; break;
            case Key.Enter or Key.Space or Key.Z:
                if (entries[_index].Enabled) entries[_index].Run();
                break;
            case Key.Delete:
                if (entries[_index].Id == "runtime") _options.RuntimeFolder = null;
                else if (entries[_index].Id == "easyrpg") _options.EasyRpgPath = null;
                else if (entries[_index].Id == "downloads") _data.DownloadFolder = null;
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
    public sealed record Item(string Label, Action? Run, bool Enabled = true, bool Checked = false);

    private readonly List<Item> _items;
    private readonly Point _at;
    private readonly Action _close;
    private int _index;

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
