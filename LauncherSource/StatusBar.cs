using Avalonia.Rendering;

namespace RpgLauncher;

internal sealed class StatusBar : Control, ICustomHitTest
{
    private enum PagerPart { None, Previous, Label, Next }

    private static readonly UiFont TextFont = Look.Font(8.4);
    private static readonly UiFont PagerFont = Look.Font(8.6, bold: true);
    private readonly Cursor _hand = new(StandardCursorType.Hand);
    private string _left = "";
    private string _right = "";

    private int _page;
    private int _pageCount = 1;
    private PagerPart _hoverPart;
    private bool _pressed;

    /// <summary>Raised with the requested zero-based page.</summary>
    public event Action<int>? PageRequested;

    /// <summary>Raised when the page label is clicked; the location is in this control's coordinates.</summary>
    public event Action<Point>? PageMenuRequested;

    public string LeftText
    {
        get => _left;
        set { if (_left == value) return; _left = value ?? ""; InvalidateVisual(); }
    }

    public string RightText
    {
        get => _right;
        set { if (_right == value) return; _right = value ?? ""; InvalidateVisual(); }
    }

    public bool PagerVisible => _pageCount > 1;

    public StatusBar()
    {
        Focusable = false;
        // The light styles' top border is two pixels, half of it above the bar; WinForms clipped it.
        ClipToBounds = true;
    }

    /// <summary>Shows the pager for a zero-based page; a single page hides it.</summary>
    public void SetPages(int page, int pageCount)
    {
        pageCount = Math.Max(1, pageCount);
        page = Math.Clamp(page, 0, pageCount - 1);
        if (page == _page && pageCount == _pageCount) return;
        _page = page;
        _pageCount = pageCount;
        InvalidateVisual();
    }

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>The pager's previous button, page label and next button; empty when the pager is hidden.</summary>
    private (Rect Previous, Rect Label, Rect Next, Rect Pager) LayoutPager()
    {
        if (!PagerVisible) return default;
        double width = Bounds.Width, height = Bounds.Height;
        const double buttonW = 28, gap = 4;
        double buttonH = Math.Max(1, height - 10);
        double labelW = Math.Ceiling(Look.Measure(PageLabel(_pageCount - 1), PagerFont).Width) + 20;

        double total = buttonW * 2 + labelW + gap * 2;
        double x = Math.Floor((width - total) / 2);
        double y = Math.Floor((height - buttonH) / 2) + 1;
        var previous = new Rect(x, y, buttonW, buttonH);
        var label = new Rect(previous.Right + gap, y, labelW, buttonH);
        var next = new Rect(label.Right + gap, y, buttonW, buttonH);
        return (previous, label, next, new Rect(previous.Left, y, next.Right - previous.Left, buttonH));
    }

    private string PageLabel(int page) => $"Page {page + 1} of {_pageCount}";

    public override void Render(DrawingContext g)
    {
        double width = Bounds.Width, height = Bounds.Height;
        g.FillRectangle(Look.Brush(Look.StatusBg), new Rect(Bounds.Size));
        // on the first row of pixels
        g.DrawLine(Look.Pen(Look.IsLight ? Look.CardBorder : Look.Divider, Look.IsLight ? 2 : 1),
            new Point(0, 0.5), new Point(width, 0.5));

        const double pad = 28;
        var pager = LayoutPager();

        // Blob style: a blob peeks up from the bottom-right corner.
        double rightLimit = width - pad;
        if (Mascots.For(MascotSpot.StatusBar) is { } blob)
        {
            double blobHeight = height - 3;
            double blobX = width - pad - Mascots.WidthAt(blob, blobHeight);
            Mascots.Draw(g, blob, blobX, height, blobHeight);
            rightLimit = Math.Floor(blobX) - 12;
        }

        // With the pager in the middle, the side texts stop short of it.
        const double gap = 16;
        double leftEnd = PagerVisible ? pager.Pager.Left - gap : rightLimit;
        double rightStart = PagerVisible ? pager.Pager.Right + gap : pad;

        Look.DrawText(g, _left, TextFont, Look.TextMuted, new Rect(pad, 0, Math.Max(0, leftEnd - pad), height));
        Look.DrawText(g, _right, TextFont, Look.TextDim, new Rect(rightStart, 0, Math.Max(0, rightLimit - rightStart), height),
            TextAlignment.Right);

        if (PagerVisible) DrawPager(g, pager.Previous, pager.Label, pager.Next);
    }

    private void DrawPager(DrawingContext g, Rect previous, Rect label, Rect next)
    {
        bool canPrevious = _page > 0;
        bool canNext = _page < _pageCount - 1;

        // WinForms drew these whole-pixel shapes with crisp outlines; half a pixel in keeps them crisp here.
        void Button(Rect rect, bool hover, bool enabled)
        {
            g.DrawRectangle(Look.Brush(hover && enabled ? Look.FieldHover : Look.Field),
                Look.Pen(hover && enabled ? Look.CardBorderHover : Look.FieldBorder, 1),
                Look.Rounded(rect.Translate(new Vector(0.5, 0.5)), 5));
        }

        void Chevron(Rect rect, bool left, bool enabled)
        {
            double cx = rect.Left + rect.Width / 2 + 0.5;
            double cy = rect.Top + rect.Height / 2 + 0.5;
            const double s = 3.6;
            double dir = left ? 1 : -1;
            g.DrawGeometry(null, Look.Pen(enabled ? Look.Text : Look.TextDim, 1.6, round: true), Look.Lines(false,
                new Point(cx + dir * s * 0.6, cy - s),
                new Point(cx - dir * s * 0.6, cy),
                new Point(cx + dir * s * 0.6, cy + s)));
        }

        Button(previous, _hoverPart == PagerPart.Previous, canPrevious);
        Chevron(previous, left: true, canPrevious);
        Button(label, _hoverPart == PagerPart.Label, enabled: true);
        Look.DrawText(g, PageLabel(_page), PagerFont, _hoverPart == PagerPart.Label ? Look.GoldBright : Look.Gold,
            label, TextAlignment.Center);
        Button(next, _hoverPart == PagerPart.Next, canNext);
        Chevron(next, left: false, canNext);
    }

    private PagerPart HitPager(Point point)
    {
        if (!PagerVisible) return PagerPart.None;
        var pager = LayoutPager();
        if (pager.Previous.Contains(point)) return PagerPart.Previous;
        if (pager.Label.Contains(point)) return PagerPart.Label;
        if (pager.Next.Contains(point)) return PagerPart.Next;
        return PagerPart.None;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var part = HitPager(e.GetPosition(this));
        if (part != _hoverPart)
        {
            _hoverPart = part;
            InvalidateVisual();
        }
        Cursor = part == PagerPart.None ? Cursor.Default : _hand;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverPart != PagerPart.None)
        {
            _hoverPart = PagerPart.None;
            InvalidateVisual();
        }
        Cursor = Cursor.Default;
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
        if (!new Rect(Bounds.Size).Contains(point)) return;
        switch (HitPager(point))
        {
            case PagerPart.Previous when _page > 0:
                PageRequested?.Invoke(_page - 1);
                break;
            case PagerPart.Next when _page < _pageCount - 1:
                PageRequested?.Invoke(_page + 1);
                break;
            case PagerPart.Label:
                var label = LayoutPager().Label;
                PageMenuRequested?.Invoke(new Point(label.Left, label.Top));
                break;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressed = false;
    }
}
