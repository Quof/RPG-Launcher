using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;

namespace RpgLauncher;

/// <summary>
/// Live cheats for an MV or MZ game started with Play with Cheats: gold, each party member's level, EXP, HP, MP and
/// parameters, healing, and walking through walls. Values refresh while the game runs; a field being edited is kept.
/// </summary>
internal sealed class CheatWindow : Form
{
    private const int FieldCount = 12; // level, EXP, HP, MP, then RPG Maker's 8 parameters
    private static readonly string[] FieldNames = new[] { "Level", "EXP", "HP", "MP" }.Concat(MvCheats.ParamNames).ToArray();
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(60);

    private readonly int _port;
    private readonly string _secret;
    private readonly Action _connectAttemptsOver;
    private readonly DateTime _opened = DateTime.UtcNow;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 500 };

    private readonly Font _captionFont = Theme.Font(7.8f, FontStyle.Bold);
    private readonly Font _inputFont = Theme.Font(10f);
    private readonly Font _statusFont = Theme.Font(9f, FontStyle.Bold);
    private readonly Font _checkFont = Theme.Font(9.2f);
    private readonly ToolTip _tips = new() { AutoPopDelay = 12000, InitialDelay = 400 };

    private readonly Label _status, _goldCaption, _partyCaption;
    private readonly CheckBox _onTop, _noclip;
    private readonly TextBox _gold;
    private readonly ComboBox _member;
    private readonly PillButton _setGold, _maxGold, _heal, _apply;
    private readonly Label[] _captions = new Label[FieldCount];
    private readonly TextBox[] _fields = new TextBox[FieldCount];

    private CheatBridge? _bridge;
    private MvState? _state;
    private bool _everConnected, _gaveUp, _loading;
    // What each field last showed; a field whose text differs has been edited and is left alone by refreshes.
    private string _shownGold = "";
    private readonly string[] _shown = Enumerable.Repeat("", FieldCount).ToArray();
    private int _shownActor = -1;

    /// <param name="connectAttemptsOver">Called once, when the game connects or stops being waited for.</param>
    public CheatWindow(string gameTitle, int port, string secret, Icon? icon, Action connectAttemptsOver)
    {
        _port = port;
        _secret = secret;
        _connectAttemptsOver = connectAttemptsOver;

        Text = "Cheats · " + gameTitle;
        Icon = icon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Font(9f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        TopMost = true;

        Label Caption(string text) => new()
        {
            Text = text,
            Font = _captionFont,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };

        TextBox Number() => new()
        {
            Font = _inputFont,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            MaxLength = 12
        };

        CheckBox Check(string text) => new()
        {
            Text = text,
            Font = _checkFont,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = false
        };

        _status = new Label
        {
            Font = _statusFont,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _onTop = Check("Keep on top");
        _onTop.Checked = true;
        _onTop.CheckedChanged += (_, _) => TopMost = _onTop.Checked;

        _goldCaption = Caption("GOLD");
        _gold = Number();
        _setGold = new PillButton { Text = "Set" };
        _maxGold = new PillButton { Text = "Max" };
        _setGold.Click += async (_, _) => await SetGoldAsync(max: false);
        _maxGold.Click += async (_, _) => await SetGoldAsync(max: true);
        _tips.SetToolTip(_maxGold, "Sets gold to the most the game allows");

        _partyCaption = Caption("PARTY");
        _member = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Font = _inputFont,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            // Drawn by hand: the stock list ignores BackColor and shows white.
            DrawMode = DrawMode.OwnerDrawFixed
        };
        _member.DrawItem += (_, e) =>
        {
            // The closed box (ComboBoxEdit) shouldn't show the hover highlight.
            bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var back = new SolidBrush(selected ? Theme.FieldHover : Theme.Field))
                e.Graphics.FillRectangle(back, e.Bounds);
            if (e.Index >= 0)
            {
                TextRenderer.DrawText(e.Graphics, _member.Items[e.Index]?.ToString() ?? "", _inputFont,
                    Rectangle.Inflate(e.Bounds, -4, 0), selected ? Theme.Text : Theme.TextMuted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        };
        _member.SelectedIndexChanged += (_, _) => { if (!_loading) ShowActor(force: true); };
        _heal = new PillButton { Text = "Heal party" };
        _heal.Click += async (_, _) => await RunAsync(async token =>
        {
            await MvCheats.HealPartyAsync(_bridge!, token);
            return "Party healed";
        });
        _tips.SetToolTip(_heal, "Restores every party member's HP and MP and removes their states");

        for (int i = 0; i < FieldCount; i++)
        {
            _captions[i] = Caption(FieldNames[i].ToUpperInvariant());
            _fields[i] = Number();
        }
        _tips.SetToolTip(_fields[1], "Experience points; the level follows them");

        _apply = new PillButton { Text = "Apply", Checked = true };
        _apply.Click += async (_, _) => await ApplyAsync();
        _tips.SetToolTip(_apply, "Applies the values you changed to the selected party member");

        _noclip = Check("Walk through walls (noclip)");
        _tips.SetToolTip(_noclip, "Like holding Ctrl in an RPG Maker playtest: random battles don't start while it's on");
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

        foreach (var box in _fields.Prepend(_gold))
        {
            box.KeyPress += (_, e) => e.Handled = !char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar);
        }

        Controls.AddRange(new Control[] { _status, _onTop, _goldCaption, _gold, _setGold, _maxGold, _partyCaption, _member, _heal, _apply, _noclip });
        Controls.AddRange(_captions);
        Controls.AddRange(_fields);

        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Enter && ActiveControl is TextBox box)
            {
                e.SuppressKeyPress = true;
                if (box == _gold) await SetGoldAsync(max: false);
                else await ApplyAsync();
            }
        };

        _poll.Tick += async (_, _) => await PollAsync();
        SetEditable(false);
        ShowStatus("Waiting for the game to start…", Theme.TextMuted);
    }

    private MvActor? SelectedActor =>
        _state is { } state && _member.SelectedIndex >= 0 && _member.SelectedIndex < state.Actors.Count
            ? state.Actors[_member.SelectedIndex]
            : null;

    // ---- talking to the game ----

    private async Task PollAsync()
    {
        if (IsDisposed || _gaveUp || !_gate.Wait(0)) return;
        try
        {
            if (_bridge is null)
            {
                var bridge = await CheatBridge.TryConnectAsync(_port, _secret, _cts.Token);
                if (IsDisposed)
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
                        ShowStatus("✗ The game didn't connect. It may not be a standard MV or MZ game.", Theme.Gold);
                    }
                    else if (_everConnected)
                    {
                        ShowStatus("✗ Lost the connection. Start the game again with Play with Cheats.", Theme.Gold);
                        SetEditable(false);
                    }
                    return;
                }
                _bridge = bridge;
                if (!_everConnected)
                {
                    _everConnected = true;
                    _connectAttemptsOver();
                    _poll.Interval = 1500;
                }
                _state = null;
            }
            await RefreshAsync(force: false);
        }
        catch (Exception ex) when (IsGameError(ex))
        {
            if (!IsDisposed) ShowStatus("✗ " + GameErrorText(ex), Theme.Gold);
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
        if (IsDisposed) return;
        bool phaseChanged = _state?.Phase != state.Phase;
        _state = state;
        Show(state, force || phaseChanged);
        if (phaseChanged)
        {
            if (state.Phase == MvPhase.InGame) ShowStatus("Connected", Theme.Success);
            else if (state.Phase == MvPhase.Title) ShowStatus("Connected · start or load a game to change values", Theme.TextMuted);
            else ShowStatus("Connected · the game is still loading", Theme.TextMuted);
        }
    }

    /// <summary>
    /// Runs one change in the game, refreshes and reports the result. Other fields being edited are kept;
    /// <paramref name="reset"/> then shows the game's values in the fields the change was about.
    /// </summary>
    private async Task RunAsync(Func<CancellationToken, Task<string>> change, Action? reset = null)
    {
        if (_bridge is null || IsDisposed) return;
        await _gate.WaitAsync();
        try
        {
            if (_bridge is null || IsDisposed) return;
            string result = await change(_cts.Token);
            if (IsDisposed) return;
            await RefreshAsync(force: false);
            if (IsDisposed) return;
            reset?.Invoke();
            ShowStatus("✓ " + result, Theme.Success);
        }
        catch (Exception ex) when (IsGameError(ex))
        {
            // The script failed in the game (for example a plugin that changed how the party works).
            if (!IsDisposed) ShowStatus("✗ " + GameErrorText(ex), Theme.Gold);
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
            if (!long.TryParse(_gold.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            {
                ShowStatus("✗ Enter the amount of gold as a whole number", Theme.Gold);
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
            string text = _fields[i].Text.Trim();
            if (text == _shown[i]) continue;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            {
                ShowStatus($"✗ {FieldNames[i]} must be a whole number", Theme.Gold);
                _fields[i].Focus();
                return;
            }
            values[i] = value;
        }
        if (values.All(v => v is null))
        {
            ShowStatus("Change a value first, then Apply", Theme.TextMuted);
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
        if (IsDisposed) return;
        ShowStatus("✗ Lost the connection. Start the game again with Play with Cheats.", Theme.Gold);
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
            if (!_member.DroppedDown && !labels.SequenceEqual(_member.Items.Cast<object>().Select(o => o.ToString())))
            {
                int index = _member.SelectedIndex;
                _member.Items.Clear();
                _member.Items.AddRange(labels);
                _member.SelectedIndex = labels.Length == 0 ? -1 : Math.Clamp(index, 0, labels.Length - 1);
            }
            ShowActor(force);
            _noclip.Checked = state.Noclip;
        }
        finally
        {
            _loading = false;
        }
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
        if (_tips.GetToolTip(_fields[1]) != tip) _tips.SetToolTip(_fields[1], tip);
    }

    private static void Put(TextBox box, ref string shown, string value, bool force)
    {
        if (force || box.Text == shown) box.Text = value;
        shown = value;
    }

    private void SetEditable(bool editable)
    {
        foreach (var control in new Control[] { _gold, _setGold, _maxGold, _member, _heal, _apply, _noclip }.Concat(_fields))
            control.Enabled = editable;
    }

    private void ShowStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    // ---- window ----

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { NativeMethods.ApplyDarkTitleBar(Handle); } catch { }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        float scale = Theme.Scale(this);
        int R(float value) => (int)Math.Round(value * scale);

        int pad = R(18f), gap = R(10f), column = R(104f);
        int inner = column * 4 + gap * 3, width = inner + pad * 2;
        int inputH = _gold.PreferredHeight, captionH = R(18f), afterCaption = R(3f), sectionGap = R(16f);
        int smallButton = R(72f);

        int y = pad;
        _status.SetBounds(pad, y, inner - R(118f), R(22f));
        _onTop.SetBounds(width - pad - R(110f), y, R(110f), R(22f));
        y = _status.Bottom + R(10f);

        _goldCaption.SetBounds(pad, y, inner, captionH);
        y += captionH + afterCaption;
        _gold.SetBounds(pad, y, column * 2 + gap, inputH);
        _setGold.Bounds = new Rectangle(_gold.Right + gap, y, smallButton, inputH);
        _maxGold.Bounds = new Rectangle(_setGold.Right + gap, y, smallButton, inputH);
        y = _gold.Bottom + sectionGap;

        _partyCaption.SetBounds(pad, y, inner, captionH);
        y += captionH + afterCaption;
        int healW = R(104f);
        _member.ItemHeight = Math.Max(_member.ItemHeight, inputH - R(6f));
        _member.SetBounds(pad, y, inner - healW - gap, inputH);
        _heal.Bounds = new Rectangle(width - pad - healW, y, healW, inputH);
        y = _member.Bottom + R(10f);

        for (int i = 0; i < FieldCount; i++)
        {
            int x = pad + (i % 4) * (column + gap);
            int top = y + (i / 4) * (captionH + afterCaption + inputH + R(8f));
            _captions[i].SetBounds(x, top, column, captionH);
            _fields[i].SetBounds(x, top + captionH + afterCaption, column, inputH);
        }
        y = _fields[FieldCount - 1].Bottom + R(12f);

        int buttonW = R(96f), buttonH = R(34f);
        _noclip.SetBounds(pad, y + (buttonH - R(24f)) / 2, inner - buttonW - gap, R(24f));
        _apply.Bounds = new Rectangle(width - pad - buttonW, y, buttonW, buttonH);
        ClientSize = new Size(width, _apply.Bottom + pad);

        // Beside the game rather than over its middle.
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - R(24f), area.Top + R(80f));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _poll.Start();
        _ = PollAsync();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        _cts.Cancel();
        _bridge?.Dispose();
        _bridge = null;
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
            _cts.Dispose();
            _captionFont.Dispose();
            _inputFont.Dispose();
            _statusFont.Dispose();
            _checkFont.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
