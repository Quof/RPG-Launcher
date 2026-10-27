namespace RpgLauncher;

/// <summary>Dialog for how the launcher reaches DLsite: network adapter and optional SOCKS/HTTP proxy.</summary>
internal sealed class ProxyDialog : Form
{
    private readonly Font _captionFont = Theme.Font(7.8f, FontStyle.Bold);
    private readonly Font _inputFont = Theme.Font(10f);
    private readonly Font _checkFont = Theme.Font(9.2f);
    private readonly Font _resultFont = Theme.Font(9f, FontStyle.Bold);
    private readonly ToolTip _tips = new() { AutoPopDelay = 12000, InitialDelay = 400 };

    private sealed record AdapterChoice(string? Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly Label _adapterCaption, _proxyCaption;
    private readonly ComboBox _adapter;
    private readonly TextBox _address, _user, _password;
    private readonly CheckBox _redownload;
    private readonly Label _result;
    private readonly PillButton _test, _save, _cancel;
    private readonly string _originalAddress;
    private readonly string? _originalAdapter;
    private CancellationTokenSource? _testCts;

    public ProxySettings Settings => new()
    {
        Address = string.IsNullOrWhiteSpace(_address.Text) ? null : _address.Text.Trim(),
        Username = string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim(),
        Password = string.IsNullOrEmpty(_password.Text) ? null : _password.Text,
        Adapter = (_adapter.SelectedItem as AdapterChoice)?.Value
    };

    /// <summary>True when the user wants the cached release calendar fetched again.</summary>
    public bool RedownloadReleases => _redownload.Checked;

    public ProxyDialog(ProxySettings? current)
    {
        Text = "DLsite connection";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Font(9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;

        _originalAddress = current?.Address?.Trim() ?? "";
        _originalAdapter = current?.Adapter;

        Label Caption(string text) => new()
        {
            Text = text,
            Font = _captionFont,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };

        TextBox Input(string? value, string placeholder, bool secret = false) => new()
        {
            Text = value ?? "",
            PlaceholderText = placeholder,
            Font = _inputFont,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            UseSystemPasswordChar = secret
        };

        _adapterCaption = Caption("NETWORK ADAPTER");
        _adapter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Font = _inputFont,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            // Drawn by hand: the stock list ignores BackColor and shows white.
            DrawMode = DrawMode.OwnerDrawFixed
        };
        _adapter.DrawItem += (_, e) =>
        {
            // The closed box (ComboBoxEdit) shouldn't show the hover highlight.
            bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var back = new SolidBrush(selected ? Theme.FieldHover : Theme.Field))
                e.Graphics.FillRectangle(back, e.Bounds);
            if (e.Index >= 0)
            {
                TextRenderer.DrawText(e.Graphics, _adapter.Items[e.Index]?.ToString() ?? "", _inputFont,
                    Rectangle.Inflate(e.Bounds, -4, 0), selected ? Theme.Text : Theme.TextMuted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        };
        var choices = new List<AdapterChoice>
        {
            new(null, "Automatic (Windows decides)"),
            new(DlsiteConnection.AnyVpnAdapter, "Any connected VPN adapter")
        };
        choices.AddRange(DlsiteConnection.AdapterChoices().Select(c => new AdapterChoice(c.Value, c.Label)));
        if (_originalAdapter is { } saved && choices.All(c => c.Value != saved))
            choices.Add(new AdapterChoice(saved, saved + "  \u00b7  not connected"));
        _adapter.Items.AddRange(choices.ToArray());
        _adapter.SelectedItem = choices.First(c => c.Value == _originalAdapter);

        _proxyCaption = Caption("PROXY (OPTIONAL)");
        _address = Input(current?.Address, "socks5://host:port  or  http://host:port");
        _user = Input(current?.Username, "Username");
        _password = Input(current?.Password, "Password", secret: true);

        // Explanations go in tooltips, not on the dialog.
        _tips.SetToolTip(_adapter,
            "Send DLsite requests (new releases, covers, metadata) out through this adapter, e.g. your VPN.\n" +
            "Useful when the VPN's split tunneling doesn't catch the launcher.\n" +
            "If the adapter is disconnected, requests fail instead of using your normal connection.");
        _tips.SetToolTip(_address, "socks5://, socks4:// or http:// with a port. Leave empty for no proxy.");
        _tips.SetToolTip(_password, "Saved encrypted for your Windows account.");

        _redownload = new CheckBox
        {
            Text = "Re-download new releases through this connection",
            Font = _checkFont,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = false
        };
        void ConnectionChanged() => _redownload.Checked =
            !string.Equals(_address.Text.Trim(), _originalAddress, StringComparison.OrdinalIgnoreCase) ||
            (_adapter.SelectedItem as AdapterChoice)?.Value != _originalAdapter;
        _address.TextChanged += (_, _) => ConnectionChanged();
        _adapter.SelectedIndexChanged += (_, _) => ConnectionChanged();

        _result = new Label
        {
            Font = _resultFont,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _test = new PillButton { Text = "Test" };
        _save = new PillButton { Text = "Save", Checked = true };
        _cancel = new PillButton { Text = "Cancel" };
        _tips.SetToolTip(_test, "Shows which country DLsite sees with these settings");
        _test.Click += async (_, _) => await TestAsync();
        _save.Click += (_, _) => TrySave();
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange(new Control[]
        {
            _adapterCaption, _adapter, _proxyCaption, _address, _user, _password,
            _redownload, _result, _test, _save, _cancel
        });

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TrySave(); }
        };
    }

    private void ShowResult(string text, Color color)
    {
        _result.Text = text;
        _result.ForeColor = color;
    }

    private void TrySave()
    {
        if (!DlsiteConnection.TryCreateProxy(Settings, out _, out var error))
        {
            ShowResult(error ?? "That proxy address can't be used.", Theme.Gold);
            _address.Focus();
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task TestAsync()
    {
        _testCts?.Cancel();
        var cts = _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _test.Enabled = false;
        ShowResult("Connecting\u2026", Theme.TextMuted);
        try
        {
            string where = await DlsiteConnection.TestAsync(Settings, cts.Token);
            if (!IsDisposed) ShowResult("✓ " + where, Theme.Success);
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            string message = ex is TaskCanceledException or OperationCanceledException
                ? "No answer within 20 seconds"
                : (ex.InnerException?.Message ?? ex.Message);
            ShowResult("✗ " + message, Theme.Gold);
        }
        finally
        {
            if (!IsDisposed) _test.Enabled = true;
        }
    }

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

        int pad = R(22f), width = R(480f), inner = width - pad * 2;
        int inputH = _address.PreferredHeight;
        int captionH = R(18f), afterCaption = R(4f), sectionGap = R(16f), rowGap = R(8f);

        int y = pad;
        _adapterCaption.SetBounds(pad, y, inner, captionH);
        y += captionH + afterCaption;
        _adapter.ItemHeight = Math.Max(_adapter.ItemHeight, inputH - R(6f));
        _adapter.SetBounds(pad, y, inner, inputH);
        y = _adapter.Bottom + sectionGap;

        _proxyCaption.SetBounds(pad, y, inner, captionH);
        y += captionH + afterCaption;
        _address.SetBounds(pad, y, inner, inputH);
        y = _address.Bottom + rowGap;
        int half = (inner - rowGap) / 2;
        _user.SetBounds(pad, y, half, inputH);
        _password.SetBounds(pad + half + rowGap, y, inner - half - rowGap, inputH);
        y = _user.Bottom + sectionGap;

        _redownload.SetBounds(pad, y, inner, R(24f));
        y = _redownload.Bottom + R(6f);
        _result.SetBounds(pad, y, inner, R(22f));

        int buttonH = R(36f), buttonW = R(96f);
        int buttonsY = _result.Bottom + R(12f);
        ClientSize = new Size(width, buttonsY + buttonH + pad);
        _test.Bounds = new Rectangle(pad, buttonsY, buttonW, buttonH);
        _save.Bounds = new Rectangle(width - pad - buttonW, buttonsY, buttonW, buttonH);
        _cancel.Bounds = new Rectangle(_save.Left - rowGap - buttonW, buttonsY, buttonW, buttonH);
        CenterToParent();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Blob style: a blob sits between Test and Cancel.
        Mascots.DrawCentered(e.Graphics, MascotSpot.ConnectionDialog, (_test.Right + _cancel.Left) / 2f,
            ClientSize.Height, 52f * Theme.Scale(this));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _testCts?.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _captionFont.Dispose();
            _inputFont.Dispose();
            _checkFont.Dispose();
            _resultFont.Dispose();
            _tips.Dispose();
            _testCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
