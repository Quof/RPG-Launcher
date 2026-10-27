using System.Diagnostics;

namespace RpgLauncher;

/// <summary>Read-only details for a game and its cached store metadata.</summary>
internal sealed class GameInfoDialog : Form
{
    private readonly GameMetadata _metadata;
    private readonly MetadataLanguage _language;

    private readonly Font _titleFont = Theme.Font(16f, FontStyle.Bold);
    private readonly Font _captionFont = Theme.Font(8f, FontStyle.Bold);
    private readonly Font _valueFont = Theme.Font(9.5f);
    private readonly Font _bodyFont = Theme.Font(9.5f);
    private readonly Font _noteFont = Theme.Font(8.2f);

    private int _pad;
    private int _captionWidth;
    private int _contentWidth;

    public GameInfoDialog(GameEntry game, IEnumerable<(string Caption, string Value)>? extraRows = null,
        string? note = null)
    {
        _metadata = game.Metadata ?? new GameMetadata();
        _language = game.MetadataLanguage;

        Text = "Game info";
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

        TitleLabel = new Label
        {
            Font = _titleFont,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        Controls.Add(TitleLabel);

        // The game's own title (with version), so it stays identifiable when the card shows the store title.
        TitleLabel.Text = game.DisplayTitle;

        string type = _metadata.WorkTypeFor(_language);
        string typeName = _metadata.WorkTypeNameFor(_language);
        if (!string.IsNullOrWhiteSpace(typeName) &&
            !string.Equals(typeName, type, StringComparison.OrdinalIgnoreCase))
        {
            type = string.IsNullOrWhiteSpace(type)
                ? typeName
                : type + " \u00b7 " + typeName;
        }

        InfoRows = new List<(string Caption, string Value, int Height)>();
        AddRow("CIRCLE", _metadata.MakerFor(_language));
        if (_metadata.Released is { } released)
            AddRow("RELEASED", released.ToString("yyyy-MM-dd"));
        AddRow("TYPE", type);
        if (extraRows is not null)
        {
            foreach (var (caption, value) in extraRows) AddRow(caption, value);
        }
        AddRow("TAGS", string.Join(" \u00b7 ", game.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag))), 40);

        DescriptionLabel = new Label
        {
            Text = "ABOUT",
            Font = _captionFont,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        DescriptionPanel = new Panel
        {
            BackColor = Theme.Card,
            Padding = new Padding(12, 8, 12, 8)
        };
        DescriptionBox = new TextBox
        {
            Text = _metadata.DescriptionFor(_language) ?? "",
            Font = _bodyFont,
            ForeColor = Theme.TextMuted,
            BackColor = Theme.Card,
            BorderStyle = BorderStyle.None,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            // The native scrollbar is light even on a dark form, so the box is sized to the text instead.
            ScrollBars = ScrollBars.None,
            Dock = DockStyle.Fill,
            TabStop = false
        };
        DescriptionPanel.Controls.Add(DescriptionBox);
        Controls.Add(DescriptionLabel);
        Controls.Add(DescriptionPanel);
        bool hasDescription = !string.IsNullOrWhiteSpace(_metadata.DescriptionFor(_language));
        DescriptionLabel.Visible = hasDescription;
        DescriptionPanel.Visible = hasDescription;

        NoteLabel = new Label
        {
            Font = _noteFont,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = note ?? ""
        };
        Controls.Add(NoteLabel);

        Link = new LinkLabel
        {
            Text = "Open DLsite page",
            Font = _valueFont,
            LinkColor = Theme.Gold,
            ActiveLinkColor = Theme.GoldBright,
            VisitedLinkColor = Theme.Gold,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = Color.Transparent,
            AutoSize = true,
            Cursor = Cursors.Hand
        };
        Link.LinkClicked += (_, _) => OpenStorePage();
        Controls.Add(Link);

        CloseButton = new PillButton { Text = "Close" };
        CloseButton.Click += (_, _) => Close();
        Controls.Add(CloseButton);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };
    }

    private Label TitleLabel { get; }
    private List<(string Caption, string Value, int Height)> InfoRows { get; }
    private Label DescriptionLabel { get; }
    private Panel DescriptionPanel { get; }
    private TextBox DescriptionBox { get; }
    private Label NoteLabel { get; }
    private LinkLabel Link { get; }
    private PillButton CloseButton { get; }

    private void AddRow(string caption, string value, int height = 0)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        InfoRows.Add((caption, value, height <= 0 ? 22 : height));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { NativeMethods.ApplyDarkTitleBar(Handle); } catch { }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LayoutDialog(Theme.Scale(this));
        CenterToParent();
    }

    private void LayoutDialog(float scale)
    {
        int R(float value) => (int)Math.Round(value * scale);

        _pad = R(24f);
        _captionWidth = R(92f);
        int width = R(600f);
        _contentWidth = width - _pad * 2;

        int titleHeight = R(50f);
        int valueWidth = _contentWidth - _captionWidth;

        // Multi-line rows grow to fit; measure narrower because the label wraps earlier than TextRenderer reports.
        int measureWidth = Math.Max(R(60f), valueWidth - R(24f));
        var rowHeights = InfoRows
            .Select(row =>
            {
                int height = R(row.Height);
                if (row.Height <= 22) return height;
                var measured = TextRenderer.MeasureText(row.Value, _valueFont,
                    new Size(measureWidth, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                return Math.Max(height, measured.Height + R(8f));
            })
            .ToList();
        int rowsHeight = rowHeights.Sum(height => height + R(4f));

        int descPanelHeight = 0;
        if (DescriptionPanel.Visible)
        {
            int innerWidth = Math.Max(1, _contentWidth - R(24f));
            var measured = TextRenderer.MeasureText(DescriptionBox.Text, _bodyFont,
                new Size(innerWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            descPanelHeight = Math.Clamp(measured.Height + R(18f), R(54f), R(210f));
        }

        int buttonHeight = R(38f);
        int height = _pad
            + titleHeight - R(6f)
            + R(6f)
            + Math.Max(1, R(1f)) + R(12f)
            + rowsHeight
            + (DescriptionPanel.Visible ? R(8f) + R(18f) + R(4f) + descPanelHeight : 0)
            + (string.IsNullOrEmpty(NoteLabel.Text) ? R(12f) : R(16f) + R(18f) + R(4f))
            + buttonHeight + _pad;

        ClientSize = new Size(width, height);

        int y = _pad;
        TitleLabel.SetBounds(_pad, y, _contentWidth, titleHeight);
        y += titleHeight - R(6f);
        y += R(6f);

        var divider = new Panel { BackColor = Theme.CardBorder };
        divider.SetBounds(_pad, y, _contentWidth, Math.Max(1, R(1f)));
        Controls.Add(divider);
        y += R(12f);

        int rowIndex = 0;
        foreach (var row in InfoRows)
        {
            int rowHeight = rowHeights[rowIndex++];
            var caption = new Label
            {
                Text = row.Caption,
                Font = _captionFont,
                ForeColor = Theme.TextDim,
                BackColor = Color.Transparent,
                AutoSize = false,
                // Tall rows align the caption with the value's first line.
                TextAlign = row.Height > 22 ? ContentAlignment.TopLeft : ContentAlignment.MiddleLeft,
                Padding = row.Height > 22 ? new Padding(0, R(3f), 0, 0) : Padding.Empty
            };
            caption.SetBounds(_pad, y, _captionWidth, rowHeight);

            var value = new Label
            {
                Text = row.Value,
                Font = _valueFont,
                ForeColor = Theme.TextMuted,
                BackColor = Color.Transparent,
                AutoSize = false,
                TextAlign = row.Height > 22 ? ContentAlignment.TopLeft : ContentAlignment.MiddleLeft,
                AutoEllipsis = row.Height <= 22
            };
            value.SetBounds(_pad + _captionWidth, y, _contentWidth - _captionWidth, rowHeight);
            Controls.Add(caption);
            Controls.Add(value);
            y += rowHeight + R(4f);
        }

        int buttonWidth = R(96f);
        int footerTop = ClientSize.Height - _pad - buttonHeight;

        NoteLabel.SetBounds(_pad, footerTop - R(22f), _contentWidth, R(18f));
        NoteLabel.Visible = !string.IsNullOrEmpty(NoteLabel.Text);
        Link.Location = new Point(_pad, footerTop + (buttonHeight - Link.Height) / 2);
        CloseButton.Bounds = new Rectangle(ClientSize.Width - _pad - buttonWidth,
            footerTop, buttonWidth, buttonHeight);

        if (DescriptionPanel.Visible)
        {
            int aboutTop = y + R(8f);
            DescriptionLabel.SetBounds(_pad, aboutTop, _contentWidth, R(18f));
            int descTop = aboutTop + R(22f);
            DescriptionPanel.SetBounds(_pad, descTop, _contentWidth, descPanelHeight);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Blob style: a blob sits between the store link and Close.
        float scale = Theme.Scale(this);
        Mascots.DrawCentered(e.Graphics, MascotSpot.InfoDialog, (Link.Right + CloseButton.Left) / 2f,
            ClientSize.Height, 56f * scale);
    }

    private void OpenStorePage()
    {
        string url = _metadata.UrlFor(_language);
        if (!WebLinks.IsTrusted(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // opening the browser is best effort
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _captionFont.Dispose();
            _valueFont.Dispose();
            _bodyFont.Dispose();
            _noteFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
