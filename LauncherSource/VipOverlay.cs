namespace RpgLauncher;

/// <summary>A clickable area of the VIPRPG screen, recorded while painting.</summary>
internal sealed record VipHot(Rect Area, Action Click, string? Hint = null);

/// <summary>A window drawn over the VIPRPG screen that takes the keys while it is open (readme, settings).</summary>
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

/// <summary>A game's readme files, shown in a message window: VIPRPG games are explained in their .txt files.</summary>
internal sealed class VipReadme : VipOverlay
{
    private const double LineHeight = 14;
    private const long MaxBytes = 512 * 1024;

    private static readonly string[] Preferred = { "readme", "read me", "説明", "はじめに", "取扱", "読んで", "manual" };

    private readonly List<string> _files;
    private int _file;
    private List<string> _lines = new();
    private double _wrapWidth;
    private int _top, _visible = 1;

    public VipReadme(List<string> files)
    {
        _files = files;
    }

    /// <summary>Text files in the game folder and its first-level subfolders, likely readmes first.</summary>
    public static List<string> Find(string folder)
    {
        var files = new List<string>();
        try
        {
            files.AddRange(Directory.EnumerateFiles(folder, "*.txt", new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive
            }).Where(path => new FileInfo(path).Length is > 0 and <= MaxBytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unreadable folder has no readme to show
        }
        int Rank(string path)
        {
            string name = Path.GetFileName(path).ToLowerInvariant();
            int index = Array.FindIndex(Preferred, word => name.Contains(word));
            return index < 0 ? Preferred.Length : index;
        }
        return files.OrderBy(Rank).ThenBy(path => path.Count(ch => ch == Path.DirectorySeparatorChar))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).Take(12).ToList();
    }

    private void Load(double width)
    {
        _wrapWidth = width;
        _lines = new List<string>();
        string text;
        try
        {
            text = TextFiles.Decode(File.ReadAllBytes(_files[_file]));
        }
        catch (Exception ex)
        {
            text = "読めませんでした: " + ex.Message;
        }
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Replace("\t", "    ");
            if (line.Length == 0)
            {
                _lines.Add("");
                continue;
            }
            // Wrap by measured width: the files mix half- and full-width characters.
            while (line.Length > 0)
            {
                int take = line.Length;
                while (take > 1 && RetroDraw.Width(line[..take]) > width) take = Math.Max(1, take * 9 / 10);
                while (take < line.Length && RetroDraw.Width(line[..(take + 1)]) <= width) take++;
                _lines.Add(line[..take]);
                line = line[take..];
            }
        }
        _top = 0;
    }

    public override void Paint(DrawingContext g, Size size, int tick, List<VipHot> hot, Point mouse)
    {
        Area = Centered(size, Math.Min(size.Width - 40, 720), size.Height - 40);
        RetroDraw.Fill(g, new Rect(size), Color.FromArgb(120, 0, 0, 0));
        RetroDraw.Window(g, Area);

        // File tabs.
        double x = Area.X + 8;
        for (int i = 0; i < _files.Count; i++)
        {
            string name = RetroDraw.Fit(Path.GetFileName(_files[i]), 150);
            var tab = new Rect(x, Area.Y + 6, RetroDraw.Width(name) + 10, 16);
            if (tab.Right > Area.Right - 8) break;
            if (i == _file) RetroDraw.Cursor(g, tab, tick);
            RetroDraw.Text(g, name, tab.X + 5, tab.Y + 2, i == _file ? RetroDraw.Yellow : RetroDraw.System);
            int index = i;
            hot.Add(new VipHot(tab, () =>
            {
                _file = index;
                _wrapWidth = 0;
            }));
            x = tab.Right + 4;
        }

        var body = new Rect(Area.X + 10, Area.Y + 28, Area.Width - 20, Area.Height - 50);
        if (_wrapWidth != body.Width - 4) Load(body.Width - 4);
        _visible = Math.Max(1, (int)(body.Height / LineHeight));
        _top = Math.Clamp(_top, 0, Math.Max(0, _lines.Count - _visible));
        using (g.PushClip(body))
        {
            for (int i = _top; i < Math.Min(_lines.Count, _top + _visible); i++)
                RetroDraw.Text(g, _lines[i], body.X, body.Y + (i - _top) * LineHeight, RetroDraw.TextColor);
        }
        if (_top > 0) RetroDraw.MoreArrow(g, Area.Center.X, body.Y - 6, tick, down: false);
        if (_top + _visible < _lines.Count) RetroDraw.MoreArrow(g, Area.Center.X, Area.Bottom - 20, tick);
        RetroDraw.Text(g, "↑↓ スクロール 　←→ ファイル 　Esc 閉じる",
            Area.X + 10, Area.Bottom - 18, RetroDraw.System);
    }

    public override void Scroll(int steps) => _top = Math.Clamp(_top + steps, 0, Math.Max(0, _lines.Count - _visible));

    public override bool OnKey(Key key)
    {
        switch (key)
        {
            case Key.Up: Scroll(-1); break;
            case Key.Down: Scroll(1); break;
            case Key.PageUp: Scroll(-_visible); break;
            case Key.PageDown or Key.Space: Scroll(_visible); break;
            case Key.Home: _top = 0; break;
            case Key.End: _top = int.MaxValue; Scroll(0); break;
            case Key.Left:
                _file = (_file + _files.Count - 1) % _files.Count;
                _wrapWidth = 0;
                break;
            case Key.Right:
                _file = (_file + 1) % _files.Count;
                _wrapWidth = 0;
                break;
            case Key.Escape or Key.X or Key.Back or Key.Enter: return false;
        }
        return true;
    }
}

/// <summary>VIPRPG mode's settings: how RPG Maker 2000/2003 games start, and the game folders.</summary>
internal sealed class VipSettings : VipOverlay
{
    private sealed record Entry(string Label, Func<string> Value, string Hint, Action Run, bool Enabled = true);

    private readonly Rm2kOptions _options;
    private readonly VipData _data;
    private readonly Action _save;
    private readonly Action _rescan;
    private readonly VipView _owner;
    private int _index;

    public VipSettings(Rm2kOptions options, VipData data, Action save, Action rescan, VipView owner)
    {
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
            new("起動に使うもの", () => _options.UseEasyRpg ? "EasyRPG Player" : "RPG_RT.exe",
                "Run 2000/2003 games with their RPG_RT.exe, or with EasyRPG Player.", () => Toggle(() => _options.UseEasyRpg = !_options.UseEasyRpg)),
            new("ウィンドウ表示", () => OnOff(_options.Window),
                "Start in a window instead of full screen.", () => Toggle(() => _options.Window = !_options.Window)),
            new("小さい画面で起動", () => OnOff(_options.SmallWindow),
                "Switch RPG_RT to its small 320x240 window (presses F5 once it is up).", () => Toggle(() => _options.SmallWindow = !_options.SmallWindow), rpgRt),
            new("CPUを1つだけ使う", () => OnOff(_options.SingleCpu),
                "Keep RPG_RT on one CPU core: fixes games that run too fast or freeze.", () => Toggle(() => _options.SingleCpu = !_options.SingleCpu), rpgRt),
            new("メモスト対策", () => OnOff(_options.MemoryStreamFix),
                "Patch out RPG_RT's memory stream error while the game runs (Windows).", () => Toggle(() => _options.MemoryStreamFix = !_options.MemoryStreamFix), rpgRt && OperatingSystem.IsWindows()),
            new("ランタイム", () => _options.RuntimeFolder ?? "自動 (MoshimoLoaderを探す)",
                "Folder with 2000\\RPG_RT.exe and 2003\\RPG_RT.exe for games shipped without one. Del resets to automatic.",
                () => _ = PickRuntimeAsync()),
            new("EasyRPG Player", () => _options.EasyRpgPath ?? "自動 (ランチャーの隣)",
                "EasyRPG Player's program. A game's own Player.exe is used first. Del resets to automatic.",
                () => _ = PickEasyRpgAsync())
        };
        foreach (string folder in _data.Directories.ToList())
        {
            entries.Add(new Entry("フォルダ", () => folder, "Enter removes this folder from VIPRPG mode (nothing is deleted).", () =>
            {
                _data.Directories.Remove(folder);
                _save();
                _rescan();
            }));
        }
        entries.Add(new Entry("閉じる", () => "", "Close the settings.", _owner.CloseOverlay));
        return entries;
    }

    private void Toggle(Action change)
    {
        change();
        _save();
    }

    private async Task PickRuntimeAsync()
    {
        string? folder = await _owner.PickAsync(folder: true, "RPG_RT.exeの入ったランタイムフォルダ");
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

    public override void Paint(DrawingContext g, Size size, int tick, List<VipHot> hot, Point mouse)
    {
        var entries = Entries();
        _index = Math.Clamp(_index, 0, entries.Count - 1);
        Area = Centered(size, Math.Min(size.Width - 40, 640), Math.Min(size.Height - 40, entries.Count * 16 + 60));
        RetroDraw.Fill(g, new Rect(size), Color.FromArgb(120, 0, 0, 0));
        RetroDraw.Window(g, Area);
        RetroDraw.Text(g, "設定 ― ツクール2000/2003の起動", Area.X + 10, Area.Y + 6, RetroDraw.System);

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
                if (entries[_index].Label == "ランタイム") _options.RuntimeFolder = null;
                else if (entries[_index].Label == "EasyRPG Player") _options.EasyRpgPath = null;
                _save();
                break;
            case Key.Escape or Key.X or Key.Back: return false;
        }
        return true;
    }
}
