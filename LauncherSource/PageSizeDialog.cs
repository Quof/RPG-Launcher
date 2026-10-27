namespace RpgLauncher;

/// <summary>Asks for a custom number of games per page.</summary>
internal sealed class PageSizeDialog : Form
{
    public const int MaxPageSize = 1000;

    private readonly Font _captionFont = Theme.Font(9.5f);
    private readonly Font _inputFont = Theme.Font(11f);
    private readonly Label _caption;
    private readonly TextBox _input;
    private readonly PillButton _ok;
    private readonly PillButton _cancel;

    public int PageSize => int.TryParse(_input.Text, out int size) ? Math.Clamp(size, 1, MaxPageSize) : 48;

    public PageSizeDialog(int current)
    {
        Text = "Games per page";
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

        _caption = new Label
        {
            Text = "How many games should each page show?",
            Font = _captionFont,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        // A TextBox because NumericUpDown's spinner can't be themed dark.
        _input = new TextBox
        {
            Text = Math.Clamp(current > 0 ? current : 48, 1, MaxPageSize).ToString(),
            Font = _inputFont,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            MaxLength = 4
        };
        _input.KeyPress += (_, e) =>
        {
            if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true;
        };
        _ok = new PillButton { Text = "Use this" };
        _cancel = new PillButton { Text = "Cancel" };
        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.Add(_caption);
        Controls.Add(_input);
        Controls.Add(_ok);
        Controls.Add(_cancel);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DialogResult = DialogResult.OK; Close(); }
            else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        };
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

        int pad = R(22f);
        int width = R(380f);
        int buttonW = R(96f), buttonH = R(36f);
        ClientSize = new Size(width, pad + R(24f) + R(8f) + _input.PreferredHeight + R(22f) + buttonH + pad);

        _caption.SetBounds(pad, pad, width - pad * 2, R(24f));
        _input.SetBounds(pad, _caption.Bottom + R(8f), R(140f), _input.PreferredHeight);
        _ok.Bounds = new Rectangle(width - pad - buttonW, ClientSize.Height - pad - buttonH, buttonW, buttonH);
        _cancel.Bounds = new Rectangle(_ok.Left - R(10f) - buttonW, _ok.Top, buttonW, buttonH);
        CenterToParent();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Blob style: a blob fills the empty corner left of the buttons.
        Mascots.DrawCentered(e.Graphics, MascotSpot.PageSizeDialog, _cancel.Left / 2f,
            ClientSize.Height, 54f * Theme.Scale(this));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _input.Focus();
        _input.SelectAll();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _captionFont.Dispose();
            _inputFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
