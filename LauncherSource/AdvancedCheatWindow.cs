using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Styling;

namespace RpgLauncher;

/// <summary>
/// The cheat window's Advanced tools for an MV or MZ game: variables, switches, items, common and map events, the
/// game's own save and load screens, and a script console. It keeps its own connection to the game.
/// </summary>
internal sealed class AdvancedCheatWindow : Window
{
    private enum Tab
    {
        Variables,
        Switches,
        Items,
        Events,
        Saves,
        Console
    }

    private const double Pad = 16;
    private const int MaxOutput = 200_000;
    private static readonly FontFamily Mono = new("Consolas, DejaVu Sans Mono, Liberation Mono");

    private readonly int _port;
    private readonly string _secret;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private CheatBridge? _bridge;
    private Tab _tab;
    private MvMap? _map;
    private bool _loading, _atTitle, _everConnected;
    // The status line shows the connection (or a refresh's error) rather than the last change's result.
    private bool _statusIsBase;
    // Which list each of the two switchable lists shows, so a different one starts at its top.
    private MvItemKind? _shownItems;
    private int? _shownEvents; // 0 for common events, else the map's ID

    private readonly UiFont _font = Look.Font(9.5);
    private readonly ManualPanel _body = new();
    private readonly ViewToggle _tabs = new("Variables", "Switches", "Items", "Events", "Saves", "Console");
    private readonly TextBlock _status;
    private readonly TextBlock _count;
    private readonly TextBox _filter;
    private readonly DarkComboBox _itemKind;
    private readonly DarkComboBox _eventSource;
    private readonly CheatList _variables = new() { EmptyText = "This game has no variables" };
    private readonly CheatList _switches = new() { EmptyText = "This game has no switches" };
    private readonly CheatList _items = new() { EmptyText = "None of these in this game" };
    private readonly CheatList _events = new() { EmptyText = "No events here" };
    private readonly TextBlock _savesText;
    private readonly PillButton _openSave = new() { Text = "Open save screen", Checked = true };
    private readonly PillButton _openLoad = new() { Text = "Open load screen" };
    private readonly DarkCheckBox _allowSave;
    private readonly TextBox _code;
    private readonly PillButton _run = new() { Text = "Run", Checked = true };
    private readonly PillButton _clearOutput = new() { Text = "Clear" };
    private readonly TextBlock _consoleHint;
    private readonly TextBox _output;

    public AdvancedCheatWindow(string gameTitle, int port, string secret, WindowIcon? icon)
    {
        _port = port;
        _secret = secret;

        Title = "Advanced cheats · " + gameTitle;
        Icon = icon;
        Background = Look.Brush(Look.Bg);
        Foreground = Look.Brush(Look.Text);
        WindowStartupLocation = WindowStartupLocation.Manual;
        RequestedThemeVariant = Look.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        var windowFont = Look.Font(9);
        FontFamily = windowFont.Family;
        FontSize = windowFont.Size;
        Width = 700;
        Height = 620;
        MinWidth = 560;
        MinHeight = 440;
        Topmost = true;
        Content = _body;

        _status = Ui.Label("Connecting to the game…", Look.Font(9, bold: true), Look.TextMuted);
        _count = Ui.Label("", Look.Font(8.5), Look.TextDim, TextAlignment.Right);

        _filter = Ui.TextBox(_font);
        _filter.Watermark = "Filter by name or ID";
        _filter.TextChanged += (_, _) =>
        {
            if (ListFor(_tab) is not { } list || list.Filter == (_filter.Text ?? "")) return;
            list.Filter = _filter.Text ?? "";
            UpdateCount();
        };

        _itemKind = new DarkComboBox { Font = _font };
        _itemKind.SetItems(new[] { "Items", "Weapons", "Armor" });
        _itemKind.SelectedIndex = 0;
        _itemKind.SelectedIndexChanged += async (_, _) => await RefreshAsync();
        _eventSource = new DarkComboBox { Font = _font };
        _eventSource.SetItems(new[] { "Common events", "Events on this map" });
        _eventSource.SelectedIndex = 0;
        _eventSource.SelectedIndexChanged += async (_, _) => await RefreshAsync();

        _variables.ValueEdited += async (row, text) => await ActAsync(
            token => MvAdvanced.SetVariableAsync(_bridge!, row.Id, text, token), $"Variable {Label(row)} is now {text}");
        _switches.Toggled += async row =>
        {
            bool on = row.On != true;
            await ActAsync(token => MvAdvanced.SetSwitchAsync(_bridge!, row.Id, on, token),
                $"Switch {Label(row)} is {(on ? "ON" : "OFF")}");
        };
        _items.ValueEdited += async (row, text) =>
        {
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long count))
            {
                ShowStatus("✗ Enter how many as a whole number", Look.Gold);
                return;
            }
            var kind = ItemKind;
            await ActAsync(token => MvAdvanced.SetItemCountAsync(_bridge!, kind, row.Id, count, token),
                $"Changed how many {row.Name} the party has");
        };
        _events.ActionClicked += async (row, action, rect) => await EventActionAsync(row, action, rect);
        _events.ActionTip = (_, action) => action switch
        {
            "Run" when CommonEvents => "Runs this common event on the map",
            "Run" => "Starts the event's active page, as if the player had triggered it",
            "Replay" => "Runs one of the event's pages again, whatever its conditions",
            "Reset" => "Turns the event's self switches off, so it can happen again",
            _ => null
        };

        _savesText = Ui.Paragraph(
            "Opens the game's own save or load screen, even where the game has turned saving off. A save keeps " +
            "every cheat. Saving works on the map; loading on the map or the title screen. Close menus and battles " +
            "first, then pick a file in the game's window.", _font, Look.TextMuted);
        _openSave.Click += async (_, _) => await ActAsync(token => MvAdvanced.OpenSaveScreenAsync(_bridge!, token),
            "The save screen is open in the game", refresh: false);
        _openLoad.Click += async (_, _) => await ActAsync(token => MvAdvanced.OpenLoadScreenAsync(_bridge!, token),
            "The load screen is open in the game", refresh: false);
        _allowSave = new DarkCheckBox("Allow saving from the game's menu") { Font = Look.Font(9.2), IsEnabled = false };
        _allowSave.CheckedChanged += async (_, _) =>
        {
            if (_loading) return;
            bool on = _allowSave.Checked;
            await ActAsync(token => MvAdvanced.SetSaveEnabledAsync(_bridge!, on, token),
                on ? "The game's menu offers saving" : "The game's menu no longer offers saving");
        };
        Ui.Tip(_allowSave, "Turns the Save command in the game's menu back on. Events that turn saving off later still do");

        _code = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Mono,
            FontSize = _font.Size,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(8, 6),
            VerticalContentAlignment = VerticalAlignment.Top,
            Watermark = "Script to run in the game, for example $gameParty.gold()"
        };
        // Ctrl+Enter runs; Enter alone starts a new line.
        _code.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            e.Handled = true;
            await RunScriptAsync();
        }, RoutingStrategies.Tunnel);
        _run.Click += async (_, _) => await RunScriptAsync();
        Ui.Tip(_run, "Runs the script in the game and shows what it returns (Ctrl+Enter)");
        _consoleHint = Ui.Label("Runs in the game's window, like its developer console", Look.Font(8.5), Look.TextDim);
        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Mono,
            FontSize = _font.Size,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(8, 6),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        _clearOutput.Click += (_, _) => _output.Text = "";

        _tabs.SelectedChanged += index => SwitchTab((Tab)index);

        _body.Children.AddRange(new Control[]
        {
            _tabs, _filter, _count, _itemKind, _eventSource, _variables, _switches, _items, _events,
            _savesText, _openSave, _openLoad, _allowSave, _code, _run, _clearOutput, _consoleHint, _output, _status
        });
        _body.Layout = LayOut;
        // Lists are read only while this window is in use, so the game isn't serializing them while it's played.
        _poll.Tick += async (_, _) =>
        {
            if (IsActive) await PollAsync();
        };
        Activated += async (_, _) => await PollAsync();
        SwitchTab(Tab.Variables);
    }

    /// <summary>To the left of <paramref name="other"/> (the cheat window), kept on its screen.</summary>
    public void PlaceBeside(Window other)
    {
        var screen = Screens.ScreenFromVisual(other) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        // Shorter on a small screen, down to the window's minimum.
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height / scale - 60));
        int width = (int)Math.Round((Width + 16) * scale), height = (int)Math.Round((Height + 39) * scale);
        int x = other.Position.X - width - (int)Math.Round(8 * scale);
        Position = new PixelPoint(Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(other.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    private MvItemKind ItemKind => (MvItemKind)Math.Max(0, _itemKind.SelectedIndex);

    private bool CommonEvents => _eventSource.SelectedIndex <= 0;

    private CheatList? ListFor(Tab tab) => tab switch
    {
        Tab.Variables => _variables,
        Tab.Switches => _switches,
        Tab.Items => _items,
        Tab.Events => _events,
        _ => null
    };

    private static string Label(CheatListRow row) =>
        row.Name.Length > 0 ? $"{row.Id} ({row.Name})" : row.Id.ToString(CultureInfo.InvariantCulture);

    private void LayOut(Size size)
    {
        double width = size.Width - Pad * 2;
        double y = Pad;
        _body.Place(_tabs, Pad, y, _tabs.PreferredWidth(), 30);
        y += 30 + 12;

        double statusTop = size.Height - Pad - 20;
        _body.Place(_status, Pad, statusTop, width, 20);
        double bottom = statusTop - 10;

        if (ListFor(_tab) is { } list)
        {
            double rowH = Ui.TextBoxHeight(_font);
            double x = Pad;
            if (_tab == Tab.Items)
            {
                _body.Place(_itemKind, x, y, 130, rowH);
                x += 140;
            }
            else if (_tab == Tab.Events)
            {
                _body.Place(_eventSource, x, y, 180, rowH);
                x += 190;
            }
            const double countW = 190;
            _body.Place(_count, size.Width - Pad - countW, y, countW, rowH);
            _body.Place(_filter, x, y, Math.Max(80, size.Width - Pad - countW - 10 - x), rowH);
            y += rowH + 10;
            _body.Place(list, Pad, y, width, Math.Max(40, bottom - y));
        }
        else if (_tab == Tab.Saves)
        {
            var text = Look.Format(_savesText.Text ?? "", _font, Look.TextMuted);
            text.MaxTextWidth = Math.Max(1, width);
            double textH = Math.Ceiling(text.Height) + 4;
            _body.Place(_savesText, Pad, y, width, textH);
            y += textH + 16;
            _body.Place(_openSave, Pad, y, 170, 36);
            _body.Place(_openLoad, Pad + 180, y, 170, 36);
            y += 36 + 22;
            _body.Place(_allowSave, Pad, y, width, 24);
        }
        else
        {
            _body.Place(_code, Pad, y, width, 130);
            y += 140;
            _body.Place(_run, Pad, y, 90, 32);
            _body.Place(_clearOutput, Pad + 100, y, 90, 32);
            _body.Place(_consoleHint, Pad + 204, y, Math.Max(10, width - 204), 32);
            y += 42;
            _body.Place(_output, Pad, y, width, Math.Max(40, bottom - y));
        }
    }

    private void SwitchTab(Tab tab)
    {
        _tab = tab;
        var list = ListFor(tab);
        foreach (var each in new[] { _variables, _switches, _items, _events }) each.IsVisible = each == list;
        _filter.IsVisible = _count.IsVisible = list is not null;
        _itemKind.IsVisible = tab == Tab.Items;
        _eventSource.IsVisible = tab == Tab.Events;
        _savesText.IsVisible = _openSave.IsVisible = _openLoad.IsVisible = _allowSave.IsVisible = tab == Tab.Saves;
        _code.IsVisible = _run.IsVisible = _clearOutput.IsVisible = _consoleHint.IsVisible = _output.IsVisible = tab == Tab.Console;
        // Each list keeps its own filter.
        if (list is not null) _filter.Text = list.Filter;
        UpdateCount();
        _body.InvalidateMeasure();
        if (tab != Tab.Console) _ = RefreshAsync();
    }

    // ---- talking to the game ----

    /// <summary>The timer's refresh; skipped while another call to the game is running.</summary>
    private async Task PollAsync()
    {
        if (_cts.IsCancellationRequested || !_gate.Wait(0)) return;
        try
        {
            if (await ConnectAsync()) await RefreshTabAsync();
        }
        catch (Exception ex) when (IsGameError(ex))
        {
            if (!_cts.IsCancellationRequested) ShowStatus("✗ " + GameErrorText(ex), Look.Gold, isBase: true);
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

    private async Task RefreshAsync()
    {
        if (_cts.IsCancellationRequested) return;
        await _gate.WaitAsync();
        try
        {
            if (await ConnectAsync()) await RefreshTabAsync();
        }
        catch (Exception ex) when (IsGameError(ex))
        {
            if (!_cts.IsCancellationRequested) ShowStatus("✗ " + GameErrorText(ex), Look.Gold, isBase: true);
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

    private Task ActAsync(Func<CancellationToken, Task<string?>> change, string done, bool refresh = true) =>
        ActAsync(change, () => done, refresh);

    /// <summary>Runs one change in the game, reports it and refreshes the tab.</summary>
    private async Task ActAsync(Func<CancellationToken, Task<string?>> change, Func<string> done, bool refresh = true)
    {
        if (_cts.IsCancellationRequested) return;
        await _gate.WaitAsync();
        try
        {
            if (!await ConnectAsync()) return;
            string? error = await change(_cts.Token);
            if (_cts.IsCancellationRequested) return;
            if (error is not null) ShowStatus("✗ " + error, Look.Gold);
            else ShowStatus("✓ " + done(), Look.Success);
            if (refresh) await RefreshTabAsync();
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

    private async Task<bool> ConnectAsync()
    {
        if (_bridge is not null) return true;
        var bridge = await CheatBridge.TryConnectAsync(_port, _secret, _cts.Token);
        if (_cts.IsCancellationRequested)
        {
            bridge?.Dispose();
            return false;
        }
        if (bridge is null)
        {
            ShowStatus(_everConnected
                ? "✗ Lost the connection. Start the game again with Play with Cheats."
                : "✗ Can't reach the game. Is it still running?", Look.Gold);
            return false;
        }
        _bridge = bridge;
        _everConnected = true;
        ShowStatus("Connected", Look.Success, isBase: true);
        return true;
    }

    /// <summary>Reads what the shown tab lists. A value being edited waits for the next refresh.</summary>
    private async Task RefreshTabAsync()
    {
        var token = _cts.Token;
        _atTitle = await MvAdvanced.AtTitleAsync(_bridge!, token);
        switch (_tab)
        {
            case Tab.Variables when !_variables.IsEditing:
            {
                var variables = await MvAdvanced.VariablesAsync(_bridge!, token);
                if (token.IsCancellationRequested || _variables.IsEditing) return;
                _variables.SetRows(variables.Select(v => new CheatListRow
                {
                    Id = v.Id, Name = v.Name, Value = v.Value, Editable = true
                }).ToList());
                break;
            }
            case Tab.Switches:
            {
                var switches = await MvAdvanced.SwitchesAsync(_bridge!, token);
                if (token.IsCancellationRequested) return;
                _switches.SetRows(switches.Select(s => new CheatListRow { Id = s.Id, Name = s.Name, On = s.On }).ToList());
                break;
            }
            case Tab.Items when !_items.IsEditing:
            {
                var kind = ItemKind;
                var items = await MvAdvanced.ItemsAsync(_bridge!, kind, token);
                if (token.IsCancellationRequested || _items.IsEditing || kind != ItemKind) return;
                _items.SetRows(items.Select(i => new CheatListRow
                {
                    Id = i.Id,
                    Name = i.Name,
                    Value = i.Count.ToString(CultureInfo.InvariantCulture),
                    Editable = true,
                    Detail = (i.Key ? "key item · " : "") + "up to " + i.Max.ToString(CultureInfo.InvariantCulture)
                }).ToList(), fromTop: _shownItems != kind);
                _shownItems = kind;
                break;
            }
            case Tab.Events when CommonEvents:
            {
                var events = await MvAdvanced.CommonEventsAsync(_bridge!, token);
                if (token.IsCancellationRequested || !CommonEvents) return;
                _events.SetRows(events.Select(e => new CheatListRow
                {
                    Id = e.Id, Name = e.Name, Detail = CommonDetail(e), Actions = new[] { "Run" }
                }).ToList(), fromTop: _shownEvents != 0);
                _shownEvents = 0;
                break;
            }
            case Tab.Events:
            {
                var map = await MvAdvanced.MapEventsAsync(_bridge!, token);
                if (token.IsCancellationRequested || CommonEvents) return;
                _map = map;
                _events.SetRows(map.Events.Select(e => new CheatListRow
                {
                    Id = e.Id, Name = e.Name, Detail = MapDetail(e), Actions = MapActions(e)
                }).ToList(), fromTop: _shownEvents != map.Id);
                _shownEvents = map.Id;
                break;
            }
            case Tab.Saves:
            {
                bool enabled = await MvAdvanced.SaveEnabledAsync(_bridge!, token);
                if (token.IsCancellationRequested) return;
                _loading = true;
                try
                {
                    _allowSave.Checked = enabled;
                    _allowSave.IsEnabled = true;
                }
                finally
                {
                    _loading = false;
                }
                break;
            }
        }
        UpdateCount();
        if (!_statusIsBase) return;
        if (_atTitle) ShowStatus("Connected · on the title screen, changes last only until a game starts or loads", Look.TextMuted, isBase: true);
        else ShowStatus("Connected", Look.Success, isBase: true);
    }

    private static string CommonDetail(MvCommonEvent e)
    {
        string trigger = e.Trigger switch { 1 => "autorun · ", 2 => "parallel · ", _ => "" };
        return trigger + (e.Commands == 0 ? "empty" : e.Commands == 1 ? "1 command" : $"{e.Commands} commands");
    }

    private static string MapDetail(MvMapEvent e)
    {
        string page = e.Page > 0 ? $"page {e.Page} of {e.Pages}" : e.Pages == 1 ? "its page isn't active" : $"{e.Pages} pages, none active";
        string self = e.SelfSwitches.Length > 0 ? " · self switch " + string.Join(", ", e.SelfSwitches.ToCharArray()) + " on" : "";
        return $"at {e.X}, {e.Y} · {page}{self}";
    }

    private static string[] MapActions(MvMapEvent e)
    {
        var actions = new List<string>();
        if (e.Page > 0) actions.Add("Run");
        actions.Add("Replay");
        if (e.SelfSwitches.Length > 0) actions.Add("Reset");
        return actions.ToArray();
    }

    private async Task EventActionAsync(CheatListRow row, string action, Rect button)
    {
        string label = Label(row);
        switch (action)
        {
            case "Run" when CommonEvents:
                await StartAsync(token => MvAdvanced.RunCommonEventAsync(_bridge!, row.Id, token), $"Common event {label}");
                break;
            case "Run":
                await StartAsync(token => MvAdvanced.RunEventAsync(_bridge!, row.Id, token), $"Event {label}");
                break;
            case "Reset":
                await ActAsync(token => MvAdvanced.ResetSelfSwitchesAsync(_bridge!, row.Id, token),
                    $"Event {label}'s self switches are off, so it can happen again");
                break;
            case "Replay" when _map?.Events.FirstOrDefault(e => e.Id == row.Id) is { } ev:
                if (ev.Pages == 1)
                {
                    await ReplayAsync(row, 0);
                    break;
                }
                // Which page: the active one, or any other, whatever its conditions.
                var menu = Menus.Create();
                for (int page = 0; page < ev.Pages; page++)
                {
                    int index = page;
                    menu.Items.Add(Menus.Item(index + 1 == ev.Page ? $"Page {index + 1}  (active now)" : $"Page {index + 1}",
                        async (_, _) => await ReplayAsync(row, index)));
                }
                var at = _events.TranslatePoint(new Point(button.Right, button.Bottom + 2), _body) ?? default;
                Menus.Show(menu, _body, at, MenuDirection.BelowLeft);
                break;
        }
    }

    private Task ReplayAsync(CheatListRow row, int page) =>
        StartAsync(token => MvAdvanced.ReplayPageAsync(_bridge!, row.Id, page, token), $"Page {page + 1} of event {Label(row)}");

    /// <summary>Starts an event and says when it runs: next, or (MZ, which pauses in the background) once the game is clicked.</summary>
    private Task StartAsync(Func<CancellationToken, Task<string?>> start, string what)
    {
        bool paused = false;
        return ActAsync(async token =>
        {
            string? error = await start(token);
            if (error is null) paused = await MvAdvanced.PausedAsync(_bridge!, token);
            return error;
        }, () => paused ? $"{what} runs when you click the game: it pauses in the background" : $"{what} runs next in the game");
    }

    private async Task RunScriptAsync()
    {
        string code = (_code.Text ?? "").Trim();
        if (code.Length == 0 || _cts.IsCancellationRequested) return;
        await _gate.WaitAsync();
        try
        {
            if (!await ConnectAsync()) return;
            string result;
            bool failed = false;
            try
            {
                result = await MvAdvanced.RunScriptAsync(_bridge!, code, _cts.Token);
            }
            catch (CheatBridgeException ex) when (ex.FromGame)
            {
                result = "✗ " + ex.Message;
                failed = true;
            }
            if (_cts.IsCancellationRequested) return;
            string shown = "> " + code.Replace("\r\n", "\n").Replace("\n", "\n  ") + "\n" + result + "\n\n";
            string all = (_output.Text ?? "") + shown;
            if (all.Length > MaxOutput) all = all[^MaxOutput..];
            _output.Text = all;
            _output.CaretIndex = all.Length;
            if (failed) ShowStatus("✗ The script stopped with an error", Look.Gold);
            else ShowStatus("✓ Ran the script", Look.Success);
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

    private void DropConnection()
    {
        _bridge?.Dispose();
        _bridge = null;
        if (_cts.IsCancellationRequested) return;
        ShowStatus("✗ Lost the connection. Start the game again with Play with Cheats.", Look.Gold);
    }

    // A script that failed in the game, or an answer that isn't the expected JSON; the connection is fine.
    private static bool IsGameError(Exception ex) => ex is CheatBridgeException { FromGame: true } or JsonException;

    private static string GameErrorText(Exception ex) =>
        ex is JsonException ? "The game sent back something unexpected" : ex.Message;

    private static bool IsConnectionError(Exception ex) =>
        ex is IOException or CheatBridgeException or SocketException or ObjectDisposedException or
            OperationCanceledException or InvalidOperationException;

    private void UpdateCount()
    {
        if (ListFor(_tab) is not { } list)
        {
            _count.Text = "";
            return;
        }
        string noun = _tab switch
        {
            Tab.Variables => "variables",
            Tab.Switches => "switches",
            Tab.Items => ItemKind switch { MvItemKind.Weapon => "weapons", MvItemKind.Armor => "armor", _ => "items" },
            _ when CommonEvents => "common events",
            _ => _map is { Name.Length: > 0 } map ? "events · " + map.Name : "events on this map"
        };
        _count.Text = list.ShownCount == list.Count ? $"{list.Count} {noun}" : $"{list.ShownCount} of {list.Count} {noun}";
    }

    private void ShowStatus(string text, Color color, bool isBase = false)
    {
        _status.Text = text;
        _status.Foreground = Look.Brush(color);
        _statusIsBase = isBase;
    }

    // ---- window ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Escape closes the window, but not from the console, where it would lose what was typed.
        var focused = FocusManager?.GetFocusedElement();
        if (e.Key == Key.Escape && !e.Handled && focused != _code && focused != _output) Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NativeMethods.StyleTitleBar(this);
        _poll.Start();
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
