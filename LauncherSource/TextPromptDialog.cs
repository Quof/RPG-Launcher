namespace RpgLauncher;

/// <summary>Asks for a short line of text, such as a group name.</summary>
internal sealed class TextPromptDialog : DialogWindow
{
    private readonly UiFont _captionFont = Look.Font(9.5);
    private readonly UiFont _inputFont = Look.Font(11);
    private readonly TextBlock _caption;
    private readonly TextBox _input;
    private readonly PillButton _ok;
    private readonly PillButton _cancel;

    public string Value => (_input.Text ?? "").Trim();

    public TextPromptDialog(string title, string caption, string current, string okText)
    {
        Title = title;

        _caption = Ui.Label(caption, _captionFont, Look.TextMuted);
        _input = Ui.TextBox(_inputFont, current, maxLength: 60);
        _ok = new PillButton { Text = okText };
        _cancel = new PillButton { Text = "Cancel" };
        _ok.Click += (_, _) => Accept();
        _cancel.Click += (_, _) => Cancel();

        var backdrop = new Backdrop(PaintBackdrop);
        Body.Children.AddRange(new Control[] { backdrop, _caption, _input, _ok, _cancel });

        const double pad = 22, width = 380, buttonW = 96, buttonH = 36;
        double inputH = Ui.TextBoxHeight(_inputFont);
        SetClientSize(width, pad + 24 + 8 + inputH + 22 + buttonH + pad);
        Body.Layout = size =>
        {
            Body.Place(backdrop, 0, 0, size.Width, size.Height);
            Body.Place(_caption, pad, pad, width - pad * 2, 24);
            Body.Place(_input, pad, pad + 24 + 8, width - pad * 2, inputH);
            double top = size.Height - pad - buttonH;
            Body.Place(_ok, width - pad - buttonW, top, buttonW, buttonH);
            Body.Place(_cancel, width - pad - buttonW - 10 - buttonW, top, buttonW, buttonH);
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _input.Focus();
        _input.SelectAll();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Accept();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    // Blob style: a blob fills the empty corner left of the buttons.
    private void PaintBackdrop(DrawingContext g, Size size) =>
        Mascots.DrawCentered(g, MascotSpot.PageSizeDialog, Body.PlaceOf(_cancel).Left / 2, size.Height, 54);

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
