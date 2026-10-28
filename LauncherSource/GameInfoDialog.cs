using Avalonia.Controls.Primitives;

namespace RpgLauncher;

/// <summary>Read-only details for a game and its cached store metadata.</summary>
internal sealed class GameInfoDialog : DialogWindow
{
    private readonly GameMetadata _metadata;
    private readonly MetadataLanguage _language;

    private readonly UiFont _titleFont = Look.Font(16, bold: true);
    private readonly UiFont _captionFont = Look.Font(8, bold: true);
    private readonly UiFont _valueFont = Look.Font(9.5);
    private readonly UiFont _bodyFont = Look.Font(9.5);
    private readonly UiFont _noteFont = Look.Font(8.2);

    private double _pad;
    private double _captionWidth;
    private double _contentWidth;

    public GameInfoDialog(GameEntry game, IEnumerable<(string Caption, string Value)>? extraRows = null,
        string? note = null)
    {
        _metadata = game.Metadata ?? new GameMetadata();
        _language = game.MetadataLanguage;

        Title = "Game info";

        // The game's own title (with version), so it stays identifiable when the card shows the store title.
        TitleLabel = Ui.Label(game.DisplayTitle, _titleFont, Look.Text);

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

        DescriptionLabel = Ui.Label("ABOUT", _captionFont, Look.TextDim);
        DescriptionBox = new SelectableTextBlock
        {
            Text = _metadata.DescriptionFor(_language) ?? "",
            Foreground = Look.Brush(Look.TextMuted),
            SelectionBrush = Look.Brush(Look.AccentBorder),
            TextWrapping = TextWrapping.Wrap
        };
        _bodyFont.ApplyTo(DescriptionBox);
        KeyboardNavigation.SetIsTabStop(DescriptionBox, false);
        DescriptionPanel = new Border
        {
            Background = Look.Brush(Look.Card),
            Padding = new Thickness(12, 8, 12, 8),
            // The box is sized to the text up to a limit; longer text scrolls.
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = DescriptionBox
            }
        };
        bool hasDescription = !string.IsNullOrWhiteSpace(_metadata.DescriptionFor(_language));
        DescriptionLabel.IsVisible = hasDescription;
        DescriptionPanel.IsVisible = hasDescription;

        NoteLabel = Ui.Label(note ?? "", _noteFont, Look.TextDim);

        Link = Ui.Link("Open DLsite page", _valueFont, Look.Gold, OpenStorePage);
        // Brighter while pressed, like the LinkLabel's ActiveLinkColor.
        Link.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(Link).Properties.IsLeftButtonPressed) Link.Foreground = Look.Brush(Look.GoldBright);
        };
        Link.PointerReleased += (_, _) => Link.Foreground = Look.Brush(Look.Gold);
        Link.PointerCaptureLost += (_, _) => Link.Foreground = Look.Brush(Look.Gold);

        CloseButton = new PillButton { Text = "Close" };
        CloseButton.Click += (_, _) => Cancel();

        var backdrop = new Backdrop(PaintBackdrop);
        Body.Children.AddRange(new Control[] { backdrop, TitleLabel, DescriptionLabel, DescriptionPanel, NoteLabel, Link, CloseButton });
        LayoutDialog(backdrop);
    }

    private TextBlock TitleLabel { get; }
    private List<(string Caption, string Value, int Height)> InfoRows { get; }
    private TextBlock DescriptionLabel { get; }
    private Border DescriptionPanel { get; }
    private SelectableTextBlock DescriptionBox { get; }
    private TextBlock NoteLabel { get; }
    private TextBlock Link { get; }
    private PillButton CloseButton { get; }

    private void AddRow(string caption, string value, int height = 0)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        InfoRows.Add((caption, value, height <= 0 ? 22 : height));
    }

    private void LayoutDialog(Control backdrop)
    {
        _pad = 24;
        _captionWidth = 92;
        double width = 600;
        _contentWidth = width - _pad * 2;

        double titleHeight = 50;
        double valueWidth = _contentWidth - _captionWidth;

        // Multi-line rows grow to fit; measured narrower, as the WinForms label wrapped earlier than TextRenderer reported.
        double measureWidth = Math.Max(60, valueWidth - 24);
        var rowHeights = InfoRows
            .Select(row =>
            {
                double height = row.Height;
                if (row.Height <= 22) return height;
                var measured = Look.Format(row.Value, _valueFont, Look.TextMuted);
                measured.MaxTextWidth = measureWidth;
                return Math.Max(height, Math.Ceiling(measured.Height) + 8);
            })
            .ToList();
        double rowsHeight = rowHeights.Sum(height => height + 4);

        double descPanelHeight = 0;
        if (DescriptionPanel.IsVisible)
        {
            double innerWidth = Math.Max(1, _contentWidth - 24);
            var measured = Look.Format(DescriptionBox.Text ?? "", _bodyFont, Look.TextMuted);
            measured.MaxTextWidth = innerWidth;
            descPanelHeight = Math.Clamp(Math.Ceiling(measured.Height) + 18, 54, 210);
        }

        double buttonHeight = 38;
        double height = _pad
            + titleHeight - 6
            + 6
            + 1 + 12
            + rowsHeight
            + (DescriptionPanel.IsVisible ? 8 + 18 + 4 + descPanelHeight : 0)
            + (string.IsNullOrEmpty(NoteLabel.Text) ? 12 : 16 + 18 + 4)
            + buttonHeight + _pad;

        SetClientSize(width, height);

        var divider = new Border { Background = Look.Brush(Look.CardBorder), IsHitTestVisible = false };
        Body.Children.Add(divider);

        var rows = new List<(TextBlock Caption, TextBlock Value, double Height)>();
        int rowIndex = 0;
        foreach (var row in InfoRows)
        {
            double rowHeight = rowHeights[rowIndex++];
            var caption = Ui.Label(row.Caption, _captionFont, Look.TextDim);
            TextBlock value;
            if (row.Height > 22)
            {
                // Tall rows align the caption with the value's first line.
                caption.VerticalAlignment = VerticalAlignment.Top;
                caption.Padding = new Thickness(0, 3, 0, 0);
                value = Ui.Paragraph(row.Value, _valueFont, Look.TextMuted);
            }
            else
            {
                value = Ui.Label(row.Value, _valueFont, Look.TextMuted);
                TipWhenTrimmed(value, _valueFont, _contentWidth - _captionWidth);
            }
            Body.Children.Add(caption);
            Body.Children.Add(value);
            rows.Add((caption, value, rowHeight));
        }

        TipWhenTrimmed(TitleLabel, _titleFont, _contentWidth);
        NoteLabel.IsVisible = !string.IsNullOrEmpty(NoteLabel.Text);
        var linkSize = Look.Measure(Link.Text ?? "", _valueFont);
        double linkW = Math.Ceiling(linkSize.Width) + 2, linkH = Math.Ceiling(linkSize.Height);

        Body.Layout = size =>
        {
            Body.Place(backdrop, 0, 0, size.Width, size.Height);

            double y = _pad;
            Body.Place(TitleLabel, _pad, y, _contentWidth, titleHeight);
            y += titleHeight - 6;
            y += 6;

            Body.Place(divider, _pad, y, _contentWidth, 1);
            y += 12;

            foreach (var (caption, value, rowHeight) in rows)
            {
                Body.Place(caption, _pad, y, _captionWidth, rowHeight);
                Body.Place(value, _pad + _captionWidth, y, _contentWidth - _captionWidth, rowHeight);
                y += rowHeight + 4;
            }

            double buttonWidth = 96;
            double footerTop = size.Height - _pad - buttonHeight;

            Body.Place(NoteLabel, _pad, footerTop - 22, _contentWidth, 18);
            Body.Place(Link, _pad, footerTop + (buttonHeight - linkH) / 2, linkW, linkH);
            Body.Place(CloseButton, size.Width - _pad - buttonWidth, footerTop, buttonWidth, buttonHeight);

            if (DescriptionPanel.IsVisible)
            {
                double aboutTop = y + 8;
                Body.Place(DescriptionLabel, _pad, aboutTop, _contentWidth, 18);
                double descTop = aboutTop + 22;
                Body.Place(DescriptionPanel, _pad, descTop, _contentWidth, descPanelHeight);
            }
        };
    }

    /// <summary>Shows the whole text as a tooltip when it is cut off, as a WinForms label with AutoEllipsis did.</summary>
    private static void TipWhenTrimmed(TextBlock label, UiFont font, double width)
    {
        if (Look.Measure(label.Text ?? "", font).Width <= width) return;
        label.IsHitTestVisible = true;
        label.Background = Brushes.Transparent;
        Ui.Tip(label, label.Text);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    // Blob style: a blob sits between the store link and Close.
    private void PaintBackdrop(DrawingContext g, Size size) =>
        Mascots.DrawCentered(g, MascotSpot.InfoDialog, (Body.PlaceOf(Link).Right + Body.PlaceOf(CloseButton).Left) / 2,
            size.Height, 56);

    private void OpenStorePage()
    {
        string url = _metadata.UrlFor(_language);
        if (!WebLinks.IsTrusted(url)) return;
        try
        {
            Shell.OpenUrl(url);
        }
        catch
        {
            // opening the browser is best effort
        }
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
