namespace RpgLauncher;

/// <summary>Dialog for how the launcher reaches DLsite: network adapter and optional SOCKS/HTTP proxy.</summary>
internal sealed class ProxyDialog : DialogWindow
{
    private readonly UiFont _captionFont = Look.Font(7.8, bold: true);
    private readonly UiFont _inputFont = Look.Font(10);
    private readonly UiFont _checkFont = Look.Font(9.2);
    private readonly UiFont _resultFont = Look.Font(9, bold: true);

    private sealed record AdapterChoice(string? Value, string Label);

    private readonly TextBlock _adapterCaption, _proxyCaption;
    private readonly DarkComboBox _adapter;
    private readonly List<AdapterChoice> _choices;
    private readonly TextBox _address, _user, _password;
    private readonly DarkCheckBox _redownload;
    private readonly TextBlock _result;
    private readonly PillButton _test, _save, _cancel;
    private readonly string _originalAddress;
    private readonly string? _originalAdapter;
    private CancellationTokenSource? _testCts;
    private bool _closed;

    public ProxySettings Settings => new()
    {
        Address = string.IsNullOrWhiteSpace(_address.Text) ? null : _address.Text.Trim(),
        Username = string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim(),
        Password = string.IsNullOrEmpty(_password.Text) ? null : _password.Text,
        Adapter = SelectedChoice?.Value
    };

    /// <summary>True when the user wants the cached release calendar fetched again.</summary>
    public bool RedownloadReleases => _redownload.Checked;

    private AdapterChoice? SelectedChoice => _adapter.SelectedIndex >= 0 ? _choices[_adapter.SelectedIndex] : null;

    public ProxyDialog(ProxySettings? current)
    {
        Title = "DLsite connection";

        _originalAddress = current?.Address?.Trim() ?? "";
        _originalAdapter = current?.Adapter;

        TextBlock Caption(string text) => Ui.Label(text, _captionFont, Look.TextDim);

        TextBox Input(string? value, string placeholder, bool secret = false)
        {
            var box = Ui.TextBox(_inputFont, value ?? "");
            box.Watermark = placeholder;
            if (secret) box.PasswordChar = '\u25cf';
            return box;
        }

        _adapterCaption = Caption("NETWORK ADAPTER");
        _adapter = new DarkComboBox { Font = _inputFont };
        _choices = new List<AdapterChoice>
        {
            new(null, OperatingSystem.IsWindows() ? "Automatic (Windows decides)" : "Automatic (the system decides)"),
            new(DlsiteConnection.AnyVpnAdapter, "Any connected VPN adapter")
        };
        _choices.AddRange(DlsiteConnection.AdapterChoices().Select(c => new AdapterChoice(c.Value, c.Label)));
        if (_originalAdapter is { } saved && _choices.All(c => c.Value != saved))
            _choices.Add(new AdapterChoice(saved, saved + "  \u00b7  not connected"));
        _adapter.SetItems(_choices.Select(c => c.Label));
        _adapter.SelectedIndex = _choices.FindIndex(c => c.Value == _originalAdapter);

        _proxyCaption = Caption("PROXY (OPTIONAL)");
        _address = Input(current?.Address, "socks5://host:port  or  http://host:port");
        _user = Input(current?.Username, "Username");
        _password = Input(current?.Password, "Password", secret: true);

        // Explanations go in tooltips, not on the dialog.
        Ui.Tip(_adapter,
            "Send DLsite requests (new releases, covers, metadata) out through this adapter, e.g. your VPN.\n" +
            "Useful when the VPN's split tunneling doesn't catch the launcher.\n" +
            "If the adapter is disconnected, requests fail instead of using your normal connection.");
        Ui.Tip(_address, "socks5://, socks4:// or http:// with a port. Leave empty for no proxy.");
        Ui.Tip(_password, OperatingSystem.IsWindows()
            ? "Saved encrypted for your Windows account."
            : "Saved encrypted with a key only your user account can read.");

        _redownload = new DarkCheckBox("Re-download new releases through this connection") { Font = _checkFont };
        void ConnectionChanged() => _redownload.Checked =
            !string.Equals((_address.Text ?? "").Trim(), _originalAddress, StringComparison.OrdinalIgnoreCase) ||
            SelectedChoice?.Value != _originalAdapter;
        _address.TextChanged += (_, _) => ConnectionChanged();
        _adapter.SelectedIndexChanged += (_, _) => ConnectionChanged();

        _result = Ui.Label("", _resultFont, Look.TextMuted);

        _test = new PillButton { Text = "Test" };
        _save = new PillButton { Text = "Save", Checked = true };
        _cancel = new PillButton { Text = "Cancel" };
        Ui.Tip(_test, "Shows which country DLsite sees with these settings");
        _test.Click += async (_, _) => await TestAsync();
        _save.Click += (_, _) => TrySave();
        _cancel.Click += (_, _) => Cancel();

        var backdrop = new Backdrop(PaintBackdrop);
        Body.Children.AddRange(new Control[]
        {
            backdrop, _adapterCaption, _adapter, _proxyCaption, _address, _user, _password,
            _redownload, _result, _test, _save, _cancel
        });

        SetClientSize(480, LayoutDialog());
        Body.Layout = size =>
        {
            Body.Place(backdrop, 0, 0, size.Width, size.Height);
            LayoutDialog();
        };
    }

    /// <summary>Places the controls; returns the height they need.</summary>
    private double LayoutDialog()
    {
        double pad = 22, width = 480, inner = width - pad * 2;
        double inputH = Ui.TextBoxHeight(_inputFont);
        double captionH = 18, afterCaption = 4, sectionGap = 16, rowGap = 8;

        double y = pad;
        Body.Place(_adapterCaption, pad, y, inner, captionH);
        y += captionH + afterCaption;
        Body.Place(_adapter, pad, y, inner, inputH);
        y += inputH + sectionGap;

        Body.Place(_proxyCaption, pad, y, inner, captionH);
        y += captionH + afterCaption;
        Body.Place(_address, pad, y, inner, inputH);
        y += inputH + rowGap;
        double half = Math.Floor((inner - rowGap) / 2);
        Body.Place(_user, pad, y, half, inputH);
        Body.Place(_password, pad + half + rowGap, y, inner - half - rowGap, inputH);
        y += inputH + sectionGap;

        Body.Place(_redownload, pad, y, inner, 24);
        y += 24 + 6;
        Body.Place(_result, pad, y, inner, 22);

        double buttonH = 36, buttonW = 96;
        double buttonsY = y + 22 + 12;
        Body.Place(_test, pad, buttonsY, buttonW, buttonH);
        Body.Place(_save, width - pad - buttonW, buttonsY, buttonW, buttonH);
        Body.Place(_cancel, width - pad - buttonW - rowGap - buttonW, buttonsY, buttonW, buttonH);
        return buttonsY + buttonH + pad;
    }

    private void ShowResult(string text, Color color)
    {
        _result.Text = text;
        _result.Foreground = Look.Brush(color);
        // The whole message as a tooltip when it is cut off, as the WinForms label's AutoEllipsis did.
        bool trimmed = Look.Measure(text, _resultFont).Width > Body.PlaceOf(_result).Width;
        _result.IsHitTestVisible = trimmed;
        _result.Background = trimmed ? Brushes.Transparent : null;
        Ui.Tip(_result, trimmed ? text : null);
    }

    private void TrySave()
    {
        if (!DlsiteConnection.TryCreateProxy(Settings, out _, out var error))
        {
            ShowResult(error ?? "That proxy address can't be used.", Look.Gold);
            _address.Focus();
            return;
        }
        Accept();
    }

    private async Task TestAsync()
    {
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _test.IsEnabled = false;
        ShowResult("Connecting\u2026", Look.TextMuted);
        try
        {
            string where = await DlsiteConnection.TestAsync(Settings, cts.Token);
            if (!_closed) ShowResult("✓ " + where, Look.Success);
        }
        catch (Exception ex)
        {
            if (_closed) return;
            string message = ex is TaskCanceledException or OperationCanceledException
                ? "No answer within 20 seconds"
                : (ex.InnerException?.Message ?? ex.Message);
            ShowResult("✗ " + message, Look.Gold);
        }
        finally
        {
            if (!_closed) _test.IsEnabled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TrySave();
        }
    }

    // Blob style: a blob sits between Test and Cancel.
    private void PaintBackdrop(DrawingContext g, Size size) =>
        Mascots.DrawCentered(g, MascotSpot.ConnectionDialog, (Body.PlaceOf(_test).Right + Body.PlaceOf(_cancel).Left) / 2,
            size.Height, 52);

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _testCts?.Cancel();
        _testCts?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Paints under the dialog's controls, as the form's OnPaint did.</summary>
    private sealed class Backdrop : Control
    {
        private readonly Action<DrawingContext, Size> _paint;

        public Backdrop(Action<DrawingContext, Size> paint)
        {
            _paint = paint;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext g) => _paint(g, Bounds.Size);
    }
}
