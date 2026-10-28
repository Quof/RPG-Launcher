using Avalonia.Rendering;

namespace RpgLauncher;

/// <summary>Segmented switch in the header, between the library and new releases.</summary>
internal sealed class ViewToggle : Control, ICustomHitTest
{
    private static readonly UiFont SegmentFont = Look.Font(9, bold: true);
    private readonly string[] _labels;
    private int _selected;
    private int _hover = -1;
    private bool _pressed;

    public event Action<int>? SelectedChanged;

    public ViewToggle(params string[] labels)
    {
        _labels = labels;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
    }

    public int Selected
    {
        get => _selected;
        set
        {
            value = Math.Clamp(value, 0, _labels.Length - 1);
            if (value == _selected) return;
            _selected = value;
            InvalidateVisual();
            SelectedChanged?.Invoke(value);
        }
    }

    /// <summary>Width that fits every label.</summary>
    public double PreferredWidth()
    {
        double width = 0;
        foreach (var label in _labels) width += LabelWidth(label) + 26;
        return width + 6;
    }

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private static double LabelWidth(string label) => Math.Ceiling(Look.Measure(label, SegmentFont).Width);

    private Rect SegmentRect(int index)
    {
        const double inset = 3;
        double x = inset;
        for (int i = 0; i < _labels.Length; i++)
        {
            double w = LabelWidth(_labels[i]) + 26;
            if (i == _labels.Length - 1) w = Bounds.Width - inset - x;
            if (i == index) return new Rect(x, inset, w, Bounds.Height - inset * 2);
            x += w;
        }
        return default;
    }

    private int SegmentAt(Point point)
    {
        for (int i = 0; i < _labels.Length; i++)
        {
            if (SegmentRect(i).Contains(point)) return i;
        }
        return -1;
    }

    public override void Render(DrawingContext g)
    {
        if (Bounds.Width < 2 || Bounds.Height < 2) return;
        var outer = new Rect(0.5, 0.5, Bounds.Width - 1, Bounds.Height - 1);
        g.DrawRectangle(Look.Brush(Look.Field), Look.Pen(Look.FieldBorder, 1), Look.Rounded(outer, Radius(outer.Height)));

        for (int i = 0; i < _labels.Length; i++)
        {
            var rect = SegmentRect(i);
            bool selected = i == _selected;
            if (selected || i == _hover)
            {
                // WinForms drew these whole-pixel rectangles with crisp outlines; half a pixel in keeps them crisp here.
                var shape = Look.Rounded(rect.Translate(new Vector(0.5, 0.5)), Radius(rect.Height));
                g.DrawRectangle(Look.Brush(selected ? Look.AccentDim : Look.FieldHover),
                    selected ? Look.Pen(Look.AccentBorder, 1) : null, shape);
            }

            var fore = selected ? Look.Gold : i == _hover ? Look.Text : Look.TextMuted;
            Look.DrawText(g, _labels[i], SegmentFont, fore, rect, TextAlignment.Center);
        }
    }

    // A pill in most styles; square tabs in Angelic.
    private static double Radius(double height) => Look.Style switch
    {
        UiStyle.Angelic => 0,
        UiStyle.Dark => 6,
        _ => height / 2
    };

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int hit = SegmentAt(e.GetPosition(this));
        if (hit == _hover) return;
        _hover = hit;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || !_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        var point = e.GetPosition(this);
        if (new Rect(Bounds.Size).Contains(point) && SegmentAt(point) is var hit and >= 0) Selected = hit;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressed = false;
    }
}
