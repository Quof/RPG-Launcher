namespace RpgLauncher;

/// <summary>Asks for a short line of text, such as a group name.</summary>
internal sealed class TextPromptDialog : Form
{
    private readonly Font _captionFont = Theme.Font(9.5f);
    private readonly Font _inputFont = Theme.Font(11f);
    private readonly Label _caption;
    private readonly TextBox _input;
    private readonly PillButton _ok;
    private readonly PillButton _cancel;

    public string Value => _input.Text.Trim();

    public TextPromptDialog(string title, string caption, string current, string okText)
    {
        Text = title;
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
            Text = caption,
            Font = _captionFont,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _input = new TextBox
        {
            Text = current,
            Font = _inputFont,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            MaxLength = 60
        };
        _ok = new PillButton { Text = okText };
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
        _input.SetBounds(pad, _caption.Bottom + R(8f), width - pad * 2, _input.PreferredHeight);
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
