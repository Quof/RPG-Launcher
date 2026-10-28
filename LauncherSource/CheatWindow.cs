using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;

namespace RpgLauncher;

/// <summary>
/// Live cheats for an MV or MZ game started with Play with Cheats: gold, walking speed, walking through walls, and each
/// party member's level, EXP, HP, MP and parameters, plus healing. Values refresh while the game runs; a field being
/// edited is kept.
/// </summary>
internal sealed class CheatWindow : Window
{
    private const int FieldCount = 12; // level, EXP, HP, MP, then RPG Maker's 8 parameters
    private static readonly string[] FieldNames = new[] { "Level", "EXP", "HP", "MP" }.Concat(MvCheats.ParamNames).ToArray();
    private static readonly double[] SpeedFactors = { 1, 1.5, 2, 3, 4 };
    private static readonly string[] SpeedNames = { "Normal speed", "1.5× speed", "2× speed", "3× speed", "4× speed" };
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(60);

    private const double Pad = 18, Gap = 10, Column = 104;
    private const double CaptionHeight = 18, AfterCaption = 3, SectionGap = 16;

    private readonly int _port;
    private readonly string _secret;
    private readonly Action _connectAttemptsOver;
    private readonly DateTime _opened = DateTime.UtcNow;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private readonly UiFont _captionFont = Look.Font(7.8, bold: true);
    private readonly UiFont _inputFont = Look.Font(10);
    private readonly UiFont _statusFont = Look.Font(9, bold: true);
    private readonly UiFont _checkFont = Look.Font(9.2);

    private readonly ManualPanel _body = new();
    private readonly TextBlock _status, _goldCaption, _movementCaption, _partyCaption;
    private readonly DarkCheckBox _onTop, _noclip;
    private readonly TextBox _gold;
    private readonly DarkComboBox _member, _speed;
    private readonly PillButton _setGold, _maxGold, _heal, _apply;
    private readonly TextBlock[] _captions = new TextBlock[FieldCount];
    private readonly TextBox[] _fields = new TextBox[FieldCount];

    private CheatBridge? _bridge;
    private MvState? _state;
    private bool _everConnected, _gaveUp, _loading;
    // What each field last showed; a field whose text differs has been edited and is left alone by refreshes.
    private string _shownGold = "";
    private readonly string[] _shown = Enumerable.Repeat("", FieldCount).ToArray();
    private int _shownActor = -1;

    /// <param name="connectAttemptsOver">Called once, when the game connects or stops being waited for.</param>
    public CheatWindow(string gameTitle, int port, string secret, WindowIcon? icon, Action connectAttemptsOver)
    {
        _port = port;
        _secret = secret;
        _connectAttemptsOver = connectAttemptsOver;

        Title = "Cheats · " + gameTitle;
        Icon = icon;
        Background = Look.Brush(Look.Bg);
        Foreground = Look.Brush(Look.Text);
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        RequestedThemeVariant = Look.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        var font = Look.Font(9);
        FontFamily = font.Family;
        FontSize = font.Size;
        Content = _body;

        TextBlock Caption(string text) => Ui.Label(text, _captionFont, Look.TextDim);

        TextBox Number()
        {
            var box = Ui.TextBox(_inputFont, maxLength: 12);
            box.AddHandler(TextInputEvent, (_, e) =>
            {
                if (e.Text is { } text && text.Any(c => !char.IsAsciiDigit(c))) e.Handled = true;
            }, RoutingStrategies.Tunnel);
            return box;
        }

        _status = Ui.Label("", _statusFont, Look.TextMuted);
        _onTop = new DarkCheckBox("Keep on top") { Font = _checkFont, Checked = true };
        _onTop.CheckedChanged += (_, _) => Topmost = _onTop.Checked;

        _goldCaption = Caption("GOLD");
        _gold = Number();
        _setGold = new PillButton { Text = "Set" };
        _maxGold = new PillButton { Text = "Max" };
        _setGold.Click += async (_, _) => await SetGoldAsync(max: false);
        _maxGold.Click += async (_, _) => await SetGoldAsync(max: true);
        Ui.Tip(_maxGold, "Sets gold to the most the game allows");

        _movementCaption = Caption("MOVEMENT");
        _speed = new DarkComboBox { Font = _inputFont };
        _speed.SetItems(SpeedNames);
        _speed.SelectedIndex = 0;
        _speed.SelectedIndexChanged += async (_, _) =>
        {
            if (_loading || _speed.SelectedIndex < 0) return;
            double factor = SpeedFactors[_speed.SelectedIndex];
            string name = SpeedNames[_speed.SelectedIndex];
            await RunAsync(async token =>
            {
                await MvCheats.SetSpeedAsync(_bridge!, factor, token);
                return factor == 1 ? "Walking at normal speed" : "Walking at " + name;
            });
        };
        Ui.Tip(_speed, "How fast you walk on the map. Dashing still adds to it; cutscenes keep their own pace");

        _noclip = new DarkCheckBox("Walk through walls (noclip)") { Font = _checkFont };
        Ui.Tip(_noclip, "Like holding Ctrl in an RPG Maker playtest: random battles don't start while it's on");
        _noclip.CheckedChanged += async (_, _) =>
        {
            if (_loading) return;
            bool on = _noclip.Checked;
            await RunAsync(async token =>
            {
                await MvCheats.SetNoclipAsync(_bridge!, on, token);
                return on ? "Walking through walls" : "Walls are solid again";
            });
        };

        _partyCaption = Caption("PARTY");
        _member = new DarkComboBox { Font = _inputFont };
        _member.SelectedIndexChanged += (_, _) => { if (!_loading) ShowActor(force: true); };
        _heal = new PillButton { Text = "Heal party" };
        _heal.Click += async (_, _) => await RunAsync(async token =>
        {
            await MvCheats.HealPartyAsync(_bridge!, token);
            return "Party healed";
        });
        Ui.Tip(_heal, "Restores every party member's HP and MP and removes their states");

        for (int i = 0; i < FieldCount; i++)
        {
            _captions[i] = Caption(FieldNames[i].ToUpperInvariant());
            _fields[i] = Number();
        }
        Ui.Tip(_fields[1], "Experience points; the level follows them");

        _apply = new PillButton { Text = "Apply", Checked = true };
        _apply.Click += async (_, _) => await ApplyAsync();
        Ui.Tip(_apply, "Applies the values you changed to the selected party member");

        _body.Children.AddRange(new Control[]
        {
            _status, _onTop, _goldCaption, _gold, _setGold, _maxGold, _movementCaption, _speed, _noclip,
            _partyCaption, _member, _heal, _apply
        });
        _body.Children.AddRange(_captions);
        _body.Children.AddRange(_fields);

        double width = Column * 4 + Gap * 3 + Pad * 2;
        _body.Layout = size => LayOut(size.Width);
        Width = width;
        Height = LayOut(width) + Pad;
        PlaceBesideLauncher();

        _poll.Tick += async (_, _) => await PollAsync();
        SetEditable(false);
        ShowStatus("Waiting for the game to start…", Look.TextMuted);
    }

    /// <summary>Places every control for a window this wide; returns the bottom of the last one.</summary>
    private double LayOut(double width)
    {
        double inner = width - Pad * 2;
        double inputH = Ui.TextBoxHeight(_inputFont);
        double half = Column * 2 + Gap;
        const double smallButton = 72;

        double y = Pad;
        _body.Place(_status, Pad, y, inner - 118, 22);
        _body.Place(_onTop, width - Pad - 110, y, 110, 22);
        y += 22 + 10;

        _body.Place(_goldCaption, Pad, y, inner, CaptionHeight);
        y += CaptionHeight + AfterCaption;
        _body.Place(_gold, Pad, y, half, inputH);
        _body.Place(_setGold, Pad + half + Gap, y, smallButton, inputH);
        _body.Place(_maxGold, Pad + half + Gap * 2 + smallButton, y, smallButton, inputH);
        y += inputH + SectionGap;

        _body.Place(_movementCaption, Pad, y, inner, CaptionHeight);
        y += CaptionHeight + AfterCaption;
        _body.Place(_speed, Pad, y, half, inputH);
        _body.Place(_noclip, Pad + half + Gap * 2, y + (inputH - 24) / 2, inner - half - Gap * 2, 24);
        y += inputH + SectionGap;

        _body.Place(_partyCaption, Pad, y, inner, CaptionHeight);
        y += CaptionHeight + AfterCaption;
        const double healW = 104;
        _body.Place(_member, Pad, y, inner - healW - Gap, inputH);
        _body.Place(_heal, width - Pad - healW, y, healW, inputH);
        y += inputH + 10;

        double bottom = y;
        for (int i = 0; i < FieldCount; i++)
        {
            double x = Pad + (i % 4) * (Column + Gap);
            double top = y + (i / 4) * (CaptionHeight + AfterCaption + inputH + 8);
            _body.Place(_captions[i], x, top, Column, CaptionHeight);
            _body.Place(_fields[i], x, top + CaptionHeight + AfterCaption, Column, inputH);
            bottom = top + CaptionHeight + AfterCaption + inputH;
        }
        y = bottom + 12;

        const double buttonW = 96, buttonH = 34;
        _body.Place(_apply, width - Pad - buttonW, y, buttonW, buttonH);
        return y + buttonH;
    }

    /// <summary>Beside the game rather than over its middle: the right edge of the launcher's screen.</summary>
    private void PlaceBesideLauncher()
    {
        var launcher = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var screen = (launcher is not null ? Screens.ScreenFromVisual(launcher) : null) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        Position = new PixelPoint(area.Right - (int)Math.Round((Width + 24) * scale), area.Y + (int)Math.Round(80 * scale));
    }

    private MvActor? SelectedActor =>
        _state is { } state && _member.SelectedIndex >= 0 && _member.SelectedIndex < state.Actors.Count
            ? state.Actors[_member.SelectedIndex]
            : null;

    // ---- talking to the game ----

    private async Task PollAsync()
    {
        if (_cts.IsCancellationRequested || _gaveUp || !_gate.Wait(0)) return;
        try
        {
            if (_bridge is null)
            {
                var bridge = await CheatBridge.TryConnectAsync(_port, _secret, _cts.Token);
                if (_cts.IsCancellationRequested)
                {
                    bridge?.Dispose();
                    return;
                }
                if (bridge is null)
                {
                    if (!_everConnected && DateTime.UtcNow - _opened > ConnectWait)
                    {
                        _gaveUp = true;
                        _connectAttemptsOver();
                        ShowStatus("✗ The game didn't connect. It may not be a standard MV or MZ game.", Look.Gold);
                    }
                    else if (_everConnected)
                    {
                        ShowStatus("✗ Lost the connection. Start the game again with Play with Cheats.", Look.Gold);
                        SetEditable(false);
                    }
                    return;
                }
                _bridge = bridge;
                if (!_everConnected)
                {
                    _everConnected = true;
                    _connectAttemptsOver();
                    _poll.Interval = TimeSpan.FromMilliseconds(1500);
                }
                _state = null;
            }
            await RefreshAsync(force: false);
        }
        catch (Exception ex) when (IsGameError(ex))
        {
            if (!_cts.IsCancellationRequested) ShowStatus("✗ " + GameErrorText(ex), Look.Gold);
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            DropConnection();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshAsync(bool force)
    {
        var state = await MvCheats.ReadAsync(_bridge!, _cts.Token);
        if (_cts.IsCancellationRequested) return;
        bool phaseChanged = _state?.Phase != state.Phase;
        _state = state;
        Show(state, force || phaseChanged);
        if (phaseChanged)
        {
            if (state.Phase == MvPhase.InGame) ShowStatus("Connected", Look.Success);
            else if (state.Phase == MvPhase.Title) ShowStatus("Connected · start or load a game to change values", Look.TextMuted);
            else ShowStatus("Connected · the game is still loading", Look.TextMuted);
        }
    }

    /// <summary>
    /// Runs one change in the game, refreshes and reports the result. Other fields being edited are kept;
    /// <paramref name="reset"/> then shows the game's values in the fields the change was about.
    /// </summary>
    private async Task RunAsync(Func<CancellationToken, Task<string>> change, Action? reset = null)
    {
        if (_bridge is null || _cts.IsCancellationRequested) return;
        await _gate.WaitAsync();
        try
        {
            if (_bridge is null || _cts.IsCancellationRequested) return;
            string result = await change(_cts.Token);
            if (_cts.IsCancellationRequested) return;
            await RefreshAsync(force: false);
            if (_cts.IsCancellationRequested) return;
            reset?.Invoke();
            ShowStatus("✓ " + result, Look.Success);
        }
        catch (Exception ex) when (IsGameError(ex))
        {
            // The script failed in the game (for example a plugin that changed how the party works).
            if (!_cts.IsCancellationRequested) ShowStatus("✗ " + GameErrorText(ex), Look.Gold);
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            DropConnection();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SetGoldAsync(bool max)
    {
        long? gold = null;
        if (!max)
        {
            if (!long.TryParse((_gold.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            {
                ShowStatus("✗ Enter the amount of gold as a whole number", Look.Gold);
                _gold.Focus();
                return;
            }
            gold = value;
        }
        await RunAsync(async token =>
        {
            long now = await MvCheats.SetGoldAsync(_bridge!, gold, token);
            return $"Gold is now {now.ToString("N0", CultureInfo.CurrentCulture)}";
        }, () => ShowGold(force: true));
    }

    private async Task ApplyAsync()
    {
        if (SelectedActor is not { } actor) return;
        var values = new long?[FieldCount];
        for (int i = 0; i < FieldCount; i++)
        {
            string text = (_fields[i].Text ?? "").Trim();
            if (text == _shown[i]) continue;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            {
                ShowStatus($"✗ {FieldNames[i]} must be a whole number", Look.Gold);
                _fields[i].Focus();
                return;
            }
            values[i] = value;
        }
        if (values.All(v => v is null))
        {
            ShowStatus("Change a value first, then Apply", Look.TextMuted);
            return;
        }

        var changes = new MvActorChanges(actor.Id, values[0], values[1], values[2], values[3], values[4..]);
        await RunAsync(async token =>
        {
            string? error = await MvCheats.ApplyAsync(_bridge!, changes, token);
            if (error is not null) throw new CheatBridgeException(error, fromGame: true);
            return $"Updated {actor.Name}";
        }, () => ShowActor(force: true));
    }

    private void DropConnection()
    {
        _bridge?.Dispose();
        _bridge = null;
        if (_cts.IsCancellationRequested) return;
        ShowStatus("✗ Lost the connection. Start the game again with Play with Cheats.", Look.Gold);
        SetEditable(false);
    }

    // A script that failed in the game, or an answer that isn't the expected JSON; the connection is fine.
    private static bool IsGameError(Exception ex) => ex is CheatBridgeException { FromGame: true } or JsonException;

    private static string GameErrorText(Exception ex) =>
        ex is JsonException ? "The game sent back something unexpected" : ex.Message;

    private static bool IsConnectionError(Exception ex) =>
        ex is IOException or CheatBridgeException or SocketException or ObjectDisposedException or
            OperationCanceledException or InvalidOperationException;

    // ---- showing values ----

    private void Show(MvState state, bool force)
    {
        _loading = true;
        try
        {
            SetEditable(state.Phase == MvPhase.InGame);
            ShowGold(force);

            var labels = state.Actors.Select(a => $"{a.Name}  ·  Lv {a.Level}").ToArray();
            if (!_member.DroppedDown && !labels.SequenceEqual(_member.Items))
            {
                int index = _member.SelectedIndex;
                _member.SetItems(labels);
                _member.SelectedIndex = labels.Length == 0 ? -1 : Math.Clamp(index, 0, labels.Length - 1);
            }
            ShowActor(force);
            _noclip.Checked = state.Noclip;
            if (!_speed.DroppedDown) _speed.SelectedIndex = SpeedIndex(state.Speed);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The listed speed nearest the game's.</summary>
    private static int SpeedIndex(double factor)
    {
        int best = 0;
        for (int i = 1; i < SpeedFactors.Length; i++)
        {
            if (Math.Abs(SpeedFactors[i] - factor) < Math.Abs(SpeedFactors[best] - factor)) best = i;
        }
        return best;
    }

    private void ShowGold(bool force) =>
        Put(_gold, ref _shownGold, _state is { Phase: MvPhase.InGame } state ? state.Gold.ToString(CultureInfo.InvariantCulture) : "", force);

    private void ShowActor(bool force)
    {
        var actor = SelectedActor;
        bool other = actor?.Id != _shownActor;
        var values = actor is null
            ? Enumerable.Repeat("", FieldCount).ToArray()
            : new[] { actor.Level, actor.Exp, actor.Hp, actor.Mp }.Concat(actor.Params)
                .Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray();
        for (int i = 0; i < FieldCount; i++) Put(_fields[i], ref _shown[i], values[i], force || other);
        _shownActor = actor?.Id ?? -1;
        string tip = actor is { NextExp: >= 0 } a
            ? $"Experience points; the next level is at {a.NextExp.ToString("N0", CultureInfo.CurrentCulture)}"
            : "Experience points; the level follows them";
        if (ToolTip.GetTip(_fields[1]) as string != tip) Ui.Tip(_fields[1], tip);
    }

    private static void Put(TextBox box, ref string shown, string value, bool force)
    {
        if (force || (box.Text ?? "") == shown) box.Text = value;
        shown = value;
    }

    private void SetEditable(bool editable)
    {
        foreach (var control in new Control[] { _gold, _setGold, _maxGold, _speed, _noclip, _member, _heal, _apply }.Concat(_fields))
            control.IsEnabled = editable;
    }

    private void ShowStatus(string text, Color color)
    {
        _status.Text = text;
        _status.Foreground = Look.Brush(color);
    }

    // ---- window ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.Enter && FocusManager?.GetFocusedElement() is TextBox box)
        {
            e.Handled = true;
            _ = box == _gold ? SetGoldAsync(max: false) : ApplyAsync();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NativeMethods.StyleTitleBar(this);
        _poll.Start();
        _ = PollAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _poll.Stop();
        _cts.Cancel();
        _bridge?.Dispose();
        _bridge = null;
        base.OnClosed(e);
    }
}
