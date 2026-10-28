using Avalonia.Rendering;

namespace RpgLauncher;

/// <summary>Icons a <see cref="PillButton"/> can show, drawn as shapes so they look the same on every system.</summary>
internal enum PillIcon
{
    None,
    Refresh,
    More
}

internal sealed class PillButton : Control, ICustomHitTest
{
    private bool _hover;
    private bool _pressed;
    private bool _checked;
    private string _text = "";

    static PillButton() => AffectsRender<PillButton>(IsEffectivelyEnabledProperty);

    public PillButton()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            InvalidateVisual();
        }
    }

    public PillIcon Icon { get; set; }
    public bool ShowHeart { get; set; }
    public bool DrawChevron { get; set; }
    public UiFont Font { get; set; } = Look.Font(9.5);

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            InvalidateVisual();
        }
    }

    public event EventHandler? Click;

    public void PerformClick() => Click?.Invoke(this, EventArgs.Empty);

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private const double IconSize = 15;
    private const double ChevronWidth = 18;

    /// <summary>Width that fits the icon, text and chevron with the usual padding.</summary>
    public double PreferredWidth()
    {
        bool hasIcon = ShowHeart || Icon != PillIcon.None;
        bool hasText = _text.Length > 0;
        double textW = hasText ? Look.Measure(_text, Font).Width : 0;
        return (hasIcon ? IconSize : 0) + (hasIcon && hasText ? 8 : 0) + textW + (DrawChevron ? ChevronWidth : 0) + 32;
    }

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
        _pressed = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || !_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        InvalidateVisual();
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this))) PerformClick();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressed = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext g)
    {
        double width = Bounds.Width, height = Bounds.Height;
        if (width < 2 || height < 2) return;
        using var faded = IsEffectivelyEnabled ? (DrawingContext.PushedState?)null : g.PushOpacity(0.5);

        var rect = new Rect(0.5, 0.5, width - 1, height - 1);
        Color fill = Checked ? Look.AccentDim : Look.Field;
        Color border = Checked ? Look.AccentBorder : Look.FieldBorder;
        Color fg = Checked ? Look.Gold : Look.TextMuted;
        if (_hover)
        {
            fill = Checked ? Look.AccentHover : Look.FieldHover;
            fg = Checked ? Look.GoldBright : Look.Text;
        }
        if (_pressed) fill = Look.Alpha(Checked ? 70 : 45, fill);

        g.DrawRectangle(Look.Brush(fill), Look.Pen(border, Look.ControlBorderWidth), Look.Rounded(rect, Look.ControlRadius));

        bool hasIcon = ShowHeart || Icon != PillIcon.None;
        bool hasText = _text.Length > 0;
        double textW = hasText ? Look.Measure(_text, Font).Width : 0;
        double gap = hasIcon && hasText ? 8 : 0;
        double chevronW = DrawChevron ? ChevronWidth : 0;
        double total = (hasIcon ? IconSize : 0) + gap + textW + chevronW;
        double start = Math.Max(8, (width - total) / 2);

        if (ShowHeart)
        {
            var heart = Look.Heart(new Rect(start, (height - IconSize) / 2 - 1, IconSize, IconSize));
            if (Checked) g.DrawGeometry(Look.Brush(fg), null, heart);
            else g.DrawGeometry(null, Look.Pen(fg, 1.8, round: true), heart);
            start += IconSize + gap;
        }
        else if (Icon != PillIcon.None)
        {
            DrawIcon(g, Icon, new Point(start + IconSize / 2, height / 2), fg);
            start += IconSize + gap;
        }

        if (hasText)
        {
            Look.DrawText(g, _text, Font, fg, new Rect(start, 0, Math.Ceiling(textW) + 2, height));
            start += textW;
        }

        if (DrawChevron)
        {
            double cx = start + chevronW / 2 + 1.5, cy = height / 2 + 1;
            g.DrawGeometry(null, Look.Pen(fg, 1.6, round: true),
                Look.Lines(false, new Point(cx - 4, cy - 2), new Point(cx, cy + 2.5), new Point(cx + 4, cy - 2)));
        }
    }

    private static void DrawIcon(DrawingContext g, PillIcon icon, Point center, Color color)
    {
        switch (icon)
        {
            case PillIcon.More:
                for (int i = -1; i <= 1; i++)
                    g.DrawEllipse(Look.Brush(color), null, new Point(center.X + i * 5.5, center.Y), 1.45, 1.45);
                break;

            case PillIcon.Refresh:
            {
                // A circular arrow: most of a ring, with an arrowhead at its end.
                const double radius = 5.6;
                const double startAngle = -70, sweep = 290;
                Point At(double degrees) => new(center.X + radius * Math.Cos(degrees * Math.PI / 180),
                    center.Y + radius * Math.Sin(degrees * Math.PI / 180));
                var arc = new StreamGeometry();
                using (var ctx = arc.Open())
                {
                    ctx.BeginFigure(At(startAngle), false);
                    ctx.ArcTo(At(startAngle + sweep), new Size(radius, radius), 0, true, SweepDirection.Clockwise);
                    ctx.EndFigure(false);
                }
                g.DrawGeometry(null, Look.Pen(color, 1.5, round: true), arc);

                // Arrowhead pointing along the ring at its end.
                var tip = At(startAngle + sweep);
                double tangent = (startAngle + sweep + 90) * Math.PI / 180;
                double back = tangent + Math.PI;
                var dir = new Vector(Math.Cos(tangent), Math.Sin(tangent));
                var normal = new Vector(-dir.Y, dir.X);
                var head = tip + dir * 1.6;
                var baseCenter = head + new Vector(Math.Cos(back), Math.Sin(back)) * 4.2;
                g.DrawGeometry(Look.Brush(color), null, Look.Lines(true, head, baseCenter + normal * 3, baseCenter - normal * 3));
                break;
            }
        }
    }
}
