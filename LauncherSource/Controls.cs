using Avalonia.Rendering;

namespace RpgLauncher;

/// <summary>
/// A panel whose children sit where its <see cref="Layout"/> callback places them, in the panel's
/// coordinates: the WinForms way of calling SetBounds from OnLoad and OnResize.
/// </summary>
internal class ManualPanel : Panel
{
    private readonly Dictionary<Control, Rect> _places = new();

    /// <summary>Called with the panel's size before its children are measured and arranged; calls <see cref="Place"/>.</summary>
    public Action<Size>? Layout { get; set; }

    public void Place(Control child, double x, double y, double width, double height) =>
        _places[child] = new Rect(x, y, Math.Max(0, width), Math.Max(0, height));

    public void Place(Control child, Rect rect) => Place(child, rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Where a child was last placed.</summary>
    public Rect PlaceOf(Control child) => _places.TryGetValue(child, out var rect) ? rect : default;

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        Layout?.Invoke(size);
        foreach (var child in Children)
            child.Measure(_places.TryGetValue(child, out var rect) ? rect.Size : size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout?.Invoke(finalSize);
        foreach (var child in Children)
            child.Arrange(_places.TryGetValue(child, out var rect) ? rect : default);
        return finalSize;
    }
}

/// <summary>Factories for the plain controls the launcher uses, styled for the active style.</summary>
internal static class Ui
{
    /// <summary>A one-line label, vertically centered in the space it is given.</summary>
    public static TextBlock Label(string text, UiFont font, Color color, TextAlignment align = TextAlignment.Left)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = Look.Brush(color),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = align,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };
        font.ApplyTo(label);
        return label;
    }

    /// <summary>Wrapped text, top-aligned in the space it is given.</summary>
    public static TextBlock Paragraph(string text, UiFont font, Color color)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = Look.Brush(color),
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        font.ApplyTo(block);
        return block;
    }

    /// <summary>A single-line text box.</summary>
    public static TextBox TextBox(UiFont font, string text = "", int maxLength = 0)
    {
        var box = new TextBox
        {
            Text = text,
            MaxLength = maxLength,
            AcceptsReturn = false,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(7, 2),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        font.ApplyTo(box);
        return box;
    }

    /// <summary>The height a single-line text box needs for a font (WinForms' PreferredHeight: 25 at 10 pt, 27 at 11 pt).</summary>
    public static double TextBoxHeight(UiFont font) => Math.Round(font.Size * 1.2 + 9);

    /// <summary>Clickable text that underlines on hover.</summary>
    public static TextBlock Link(string text, UiFont font, Color color, Action onClick)
    {
        var link = new TextBlock
        {
            Text = text,
            Foreground = Look.Brush(color),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent
        };
        font.ApplyTo(link);
        link.PointerEntered += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.PointerExited += (_, _) => link.TextDecorations = null;
        // A click is a release over the link; releasing elsewhere after a press cancels it.
        link.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && new Rect(link.Bounds.Size).Contains(e.GetPosition(link)))
                onClick();
        };
        return link;
    }

    /// <summary>Shows <paramref name="text"/> as the control's tooltip.</summary>
    public static void Tip(Control control, string? text)
    {
        ToolTip.SetTip(control, text);
        ToolTip.SetShowDelay(control, 400);
    }
}

/// <summary>A check box drawn in the launcher's style.</summary>
internal sealed class DarkCheckBox : Control, ICustomHitTest
{
    private const double BoxSize = 16;
    private const double Gap = 8;

    private bool _checked;
    private bool _hover;
    private string _text = "";

    static DarkCheckBox() => AffectsRender<DarkCheckBox>(IsEffectivelyEnabledProperty);

    public DarkCheckBox(string text = "")
    {
        _text = text;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public UiFont Font { get; set; } = Look.Font(9.2);

    /// <summary>Text color when not hovered; defaults to <see cref="Look.TextMuted"/>.</summary>
    public Color? TextColor { get; set; }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            InvalidateVisual();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    protected override Size MeasureOverride(Size availableSize) =>
        new(BoxSize + Gap + Look.Measure(_text, Font).Width + 2, Math.Max(BoxSize, Font.Size * 1.4));

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (!new Rect(Bounds.Size).Contains(e.GetPosition(this))) return;
        e.Handled = true;
        Checked = !Checked;
    }

    public override void Render(DrawingContext g)
    {
        using var faded = IsEffectivelyEnabled ? (DrawingContext.PushedState?)null : g.PushOpacity(0.5);
        double height = Bounds.Height;
        var box = new Rect(0.5, Math.Round((height - BoxSize) / 2) + 0.5, BoxSize - 1, BoxSize - 1);
        var fill = Checked ? Look.AccentDim : _hover ? Look.FieldHover : Look.Field;
        var border = Checked ? Look.AccentBorder : _hover ? Look.CardBorderHover : Look.FieldBorder;
        g.DrawRectangle(Look.Brush(fill), Look.Pen(border, Look.ControlBorderWidth),
            Look.Rounded(box, Math.Min(4, Look.ControlRadius)));
        if (Checked) MenuView.DrawCheck(g, box.Deflate(1.5));

        var color = _hover ? Look.Text : TextColor ?? Look.TextMuted;
        Look.DrawText(g, _text, Font, color, new Rect(BoxSize + Gap, 0, Math.Max(1, Bounds.Width - BoxSize - Gap), height));
    }
}

/// <summary>A drop-down list drawn in the launcher's style; picking opens a <see cref="DarkMenu"/>.</summary>
internal sealed class DarkComboBox : Control, ICustomHitTest
{
    private readonly List<string> _items = new();
    private int _selectedIndex = -1;
    private bool _hover;
    private DarkMenu? _menu;

    static DarkComboBox() => AffectsRender<DarkComboBox>(IsEffectivelyEnabledProperty);

    public DarkComboBox()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
    }

    public UiFont Font { get; set; } = Look.Font(10);

    public IReadOnlyList<string> Items => _items;

    /// <summary>True while the list is open.</summary>
    public bool DroppedDown => _menu is { IsOpen: true };

    public void SetItems(IEnumerable<string> items)
    {
        _items.Clear();
        _items.AddRange(items);
        if (_selectedIndex >= _items.Count) SelectedIndex = _items.Count - 1;
        InvalidateVisual();
    }

    /// <summary>The picked item, or -1. Setting it raises <see cref="SelectedIndexChanged"/>, as WinForms did.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            value = Math.Clamp(value, -1, _items.Count - 1);
            if (value == _selectedIndex) return;
            _selectedIndex = value;
            InvalidateVisual();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? SelectedItem => _selectedIndex >= 0 ? _items[_selectedIndex] : null;

    public event EventHandler? SelectedIndexChanged;

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || _items.Count == 0) return;
        e.Handled = true;
        var menu = Menus.Create(checks: true);
        menu.MinWidth = Bounds.Width;
        for (int i = 0; i < _items.Count; i++)
        {
            int index = i;
            var item = Menus.Item(_items[i].Replace("&", "&&"), (_, _) => SelectedIndex = index);
            item.Checked = i == _selectedIndex;
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => InvalidateVisual();
        _menu = menu;
        menu.Show(this, new Point(0, Bounds.Height + 2));
        InvalidateVisual();
    }

    public override void Render(DrawingContext g)
    {
        using var faded = IsEffectivelyEnabled ? (DrawingContext.PushedState?)null : g.PushOpacity(0.5);
        var rect = new Rect(0.5, 0.5, Bounds.Width - 1, Bounds.Height - 1);
        bool lit = _hover || DroppedDown;
        g.DrawRectangle(Look.Brush(lit ? Look.FieldHover : Look.Field),
            Look.Pen(DroppedDown ? Look.CardBorderHover : Look.FieldBorder, Look.ControlBorderWidth),
            Look.Rounded(rect, Look.ControlRadius));

        double arrowArea = 26;
        Look.DrawText(g, SelectedItem ?? "", Font, lit ? Look.Text : Look.TextMuted,
            new Rect(9, 0, Math.Max(1, Bounds.Width - 9 - arrowArea), Bounds.Height));

        double cx = Bounds.Width - arrowArea / 2 - 2, cy = Bounds.Height / 2 + 1;
        g.DrawGeometry(null, Look.Pen(lit ? Look.Text : Look.TextMuted, 1.5, round: true),
            Look.Lines(false, new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2)));
    }
}
