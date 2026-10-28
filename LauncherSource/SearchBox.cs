using Avalonia.Rendering;

namespace RpgLauncher;

/// <summary>The header's search field: a rounded box with a magnifier, cue text and a clear button.</summary>
internal sealed class SearchBox : Control, ICustomHitTest
{
    private const double Pad = 13;
    private const double IconSize = 15;
    private const double IconGap = 9;
    private const double ClearSize = 13;

    private readonly TextBox _text;
    private readonly Cursor _hand = new(StandardCursorType.Hand);
    private readonly Cursor _beam = new(StandardCursorType.Ibeam);
    private bool _focused;
    private bool _hoverClear;

    public event EventHandler? TextValueChanged;

    public string TextValue
    {
        get => _text.Text ?? "";
        set => _text.Text = value ?? string.Empty;
    }

    public SearchBox()
    {
        Focusable = false;
        Cursor = _beam;
        _text = PlainTextBox(Look.Font(9.5), "Search games…");
        _text.IsTabStop = false;
        _text.TextChanged += (_, _) =>
        {
            // the clear button takes room from the text once there is text
            InvalidateMeasure();
            InvalidateVisual();
            TextValueChanged?.Invoke(this, EventArgs.Empty);
        };
        _text.GotFocus += (_, _) =>
        {
            _focused = true;
            InvalidateVisual();
        };
        _text.LostFocus += (_, _) =>
        {
            _focused = false;
            InvalidateVisual();
        };
        VisualChildren.Add(_text);
        LogicalChildren.Add(_text);
    }

    /// <summary>Puts the caret in the search text.</summary>
    public new bool Focus(NavigationMethod method = NavigationMethod.Unspecified, KeyModifiers keyModifiers = KeyModifiers.None) =>
        _text.Focus(method, keyModifiers);

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private bool HasText => !string.IsNullOrEmpty(_text.Text);

    private Rect TextRect(Size size)
    {
        double left = Pad + IconSize + IconGap;
        double right = Pad + (HasText ? 26 : 0);
        return new Rect(left, 1, Math.Max(20, size.Width - left - right), Math.Max(0, size.Height - 2));
    }

    private Rect ClearRect()
    {
        double x = Bounds.Width - Pad - ClearSize, y = (Bounds.Height - ClearSize) / 2;
        return new Rect(x - 4, y - 4, ClearSize + 8, ClearSize + 8);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsInfinity(availableSize.Width) ? 270 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 38 : availableSize.Height);
        _text.Measure(TextRect(size).Size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _text.Arrange(TextRect(finalSize));
        return finalSize;
    }

    public override void Render(DrawingContext g)
    {
        double width = Bounds.Width, height = Bounds.Height;
        if (width < 2 || height < 2) return;
        var rect = new Rect(0.5, 0.5, width - 1, height - 1);
        g.DrawRectangle(Look.Brush(Look.Field),
            Look.Pen(_focused ? Look.CardBorderHover : Look.FieldBorder, Look.ControlBorderWidth + 0.2),
            Look.Rounded(rect, Look.ControlRadius));

        // magnifier
        double cx = Pad + IconSize / 2, cy = height / 2, radius = 5.2;
        var pen = Look.Pen(_focused ? Look.Gold : Look.TextDim, 1.7, round: true);
        g.DrawEllipse(null, pen, new Point(cx, cy), radius, radius);
        g.DrawLine(pen, new Point(cx + radius * 0.55, cy + radius * 0.55), new Point(cx + radius * 1.45, cy + radius * 1.45));

        if (HasText)
        {
            double x = width - Pad - ClearSize, y = (height - ClearSize) / 2;
            var cross = Look.Pen(_hoverClear ? Look.Text : Look.TextDim, 1.6, round: true);
            g.DrawLine(cross, new Point(x, y), new Point(x + ClearSize, y + ClearSize));
            g.DrawLine(cross, new Point(x + ClearSize, y), new Point(x, y + ClearSize));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Presses on the text itself are the text box's.
        if (!ReferenceEquals(e.Source, this)) return;
        e.Handled = true;
        if (HasText && ClearRect().Contains(e.GetPosition(this)))
        {
            _text.Clear();
            _text.Focus();
            return;
        }
        _text.Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        bool overClear = HasText && ClearRect().Contains(e.GetPosition(this));
        if (overClear != _hoverClear)
        {
            _hoverClear = overClear;
            InvalidateVisual();
        }
        Cursor = overClear ? _hand : _beam;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_hoverClear) return;
        _hoverClear = false;
        InvalidateVisual();
    }

    /// <summary>
    /// A text box without a border or fill of its own, for a field its parent draws. The cue shows in
    /// <see cref="Look.TextDim"/> while the box is empty and unfocused, like a WinForms cue banner.
    /// </summary>
    internal static TextBox PlainTextBox(UiFont font, string cue)
    {
        var box = Ui.TextBox(font);
        box.Padding = new Thickness(0);
        box.BorderThickness = new Thickness(0);
        box.Background = Brushes.Transparent;
        // The theme's hover and focus states take their fill and outline from these resources.
        box.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        box.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        box.Watermark = cue;
        box.TemplateApplied += (_, e) =>
        {
            // The theme draws the watermark in the text color at half opacity.
            if (e.NameScope.Find<TextBlock>("PART_Watermark") is not { } watermark) return;
            watermark.Opacity = 1;
            watermark.Bind(TextBlock.ForegroundProperty, watermark.GetResourceObservable("TextControlPlaceholderForeground"));
        };
        box.GotFocus += (_, _) => box.Watermark = null;
        box.LostFocus += (_, _) => box.Watermark = cue;
        return box;
    }
}
