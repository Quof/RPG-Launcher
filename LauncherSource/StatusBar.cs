using System.Drawing.Drawing2D;

namespace RpgLauncher;

internal sealed class StatusBar : Control
{
    private enum PagerPart { None, Previous, Label, Next }

    private readonly Font _font = Theme.Font(8.4f);
    private readonly Font _pagerFont = Theme.Font(8.6f, FontStyle.Bold);
    private string _left = "";
    private string _right = "";

    private int _page;
    private int _pageCount = 1;
    private PagerPart _hoverPart;
    private Rectangle _previousRect;
    private Rectangle _labelRect;
    private Rectangle _nextRect;
    private Rectangle _pagerRect;

    /// <summary>Raised with the requested zero-based page.</summary>
    public event Action<int>? PageRequested;

    /// <summary>Raised when the page label is clicked; the location is in this control's coordinates.</summary>
    public event Action<Point>? PageMenuRequested;

    public string LeftText
    {
        get => _left;
        set { if (_left == value) return; _left = value; Invalidate(); }
    }

    public string RightText
    {
        get => _right;
        set { if (_right == value) return; _right = value; Invalidate(); }
    }

    public bool PagerVisible => _pageCount > 1;

    public StatusBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.Bg;
        Height = 32;
        Font = _font;
    }

    /// <summary>Shows the pager for a zero-based page; a single page hides it.</summary>
    public void SetPages(int page, int pageCount)
    {
        pageCount = Math.Max(1, pageCount);
        page = Math.Clamp(page, 0, pageCount - 1);
        if (page == _page && pageCount == _pageCount) return;
        _page = page;
        _pageCount = pageCount;
        Invalidate();
    }

    private void LayoutPager(Graphics g, float scale)
    {
        if (!PagerVisible)
        {
            _previousRect = _labelRect = _nextRect = _pagerRect = Rectangle.Empty;
            return;
        }

        int buttonW = (int)Math.Round(28f * scale);
        int buttonH = Math.Max(1, Height - (int)Math.Round(10f * scale));
        int gap = (int)Math.Round(4f * scale);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        int labelW = TextRenderer.MeasureText(g, PageLabel(_pageCount - 1), _pagerFont,
            new Size(int.MaxValue, int.MaxValue), flags).Width + (int)Math.Round(20f * scale);

        int total = buttonW * 2 + labelW + gap * 2;
        int x = (Width - total) / 2;
        int y = (Height - buttonH) / 2 + 1;
        _previousRect = new Rectangle(x, y, buttonW, buttonH);
        _labelRect = new Rectangle(_previousRect.Right + gap, y, labelW, buttonH);
        _nextRect = new Rectangle(_labelRect.Right + gap, y, buttonW, buttonH);
        _pagerRect = Rectangle.FromLTRB(_previousRect.Left, y, _nextRect.Right, y + buttonH);
    }

    private string PageLabel(int page) => $"Page {page + 1} of {_pageCount}";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var brush = new SolidBrush(Theme.StatusBg)) g.FillRectangle(brush, ClientRectangle);
        using (var pen = new Pen(Theme.IsLight ? Theme.CardBorder : Theme.Divider, Theme.IsLight ? 2f : 1f))
            g.DrawLine(pen, 0, 0, Width, 0);

        float scale = Theme.Scale(this);
        int pad = (int)Math.Round(28f * scale);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

        LayoutPager(g, scale);

        // Blob style: a blob peeks up from the bottom-right corner.
        int rightLimit = Width - pad;
        if (Mascots.For(MascotSpot.StatusBar) is { } blob)
        {
            float blobHeight = Height - 3f * scale;
            float blobX = Width - pad - Mascots.WidthAt(blob, blobHeight);
            Mascots.Draw(g, blob, blobX, Height, blobHeight);
            rightLimit = (int)blobX - (int)Math.Round(12f * scale);
        }

        // With the pager in the middle, the side texts stop short of it.
        int gap = (int)Math.Round(16f * scale);
        int leftEnd = PagerVisible ? _pagerRect.Left - gap : rightLimit;
        int rightStart = PagerVisible ? _pagerRect.Right + gap : pad;

        TextRenderer.DrawText(g, _left, _font,
            new Rectangle(pad, 0, Math.Max(0, leftEnd - pad), Height), Theme.TextMuted, flags);
        TextRenderer.DrawText(g, _right, _font,
            new Rectangle(rightStart, 0, Math.Max(0, rightLimit - rightStart), Height), Theme.TextDim,
            flags | TextFormatFlags.Right);

        if (PagerVisible) DrawPager(g, scale);
    }

    private void DrawPager(Graphics g, float scale)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool canPrevious = _page > 0;
        bool canNext = _page < _pageCount - 1;

        void Button(Rectangle rect, bool hover, bool enabled)
        {
            using var path = Theme.Rounded(rect, 5f * scale);
            using var fill = new SolidBrush(hover && enabled ? Theme.FieldHover : Theme.Field);
            using var border = new Pen(hover && enabled ? Theme.CardBorderHover : Theme.FieldBorder, 1f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        void Chevron(Rectangle rect, bool left, bool enabled)
        {
            float cx = rect.Left + rect.Width / 2f;
            float cy = rect.Top + rect.Height / 2f;
            float s = 3.6f * scale;
            using var pen = new Pen(enabled ? Theme.Text : Theme.TextDim, 1.6f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float dir = left ? 1f : -1f;
            g.DrawLines(pen, new[]
            {
                new PointF(cx + dir * s * 0.6f, cy - s),
                new PointF(cx - dir * s * 0.6f, cy),
                new PointF(cx + dir * s * 0.6f, cy + s)
            });
        }

        Button(_previousRect, _hoverPart == PagerPart.Previous, canPrevious);
        Chevron(_previousRect, left: true, canPrevious);
        Button(_labelRect, _hoverPart == PagerPart.Label, enabled: true);
        TextRenderer.DrawText(g, PageLabel(_page), _pagerFont, _labelRect,
            _hoverPart == PagerPart.Label ? Theme.GoldBright : Theme.Gold,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        Button(_nextRect, _hoverPart == PagerPart.Next, canNext);
        Chevron(_nextRect, left: false, canNext);
    }

    private PagerPart HitPager(Point point)
    {
        if (!PagerVisible) return PagerPart.None;
        if (_previousRect.Contains(point)) return PagerPart.Previous;
        if (_labelRect.Contains(point)) return PagerPart.Label;
        if (_nextRect.Contains(point)) return PagerPart.Next;
        return PagerPart.None;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var part = HitPager(e.Location);
        if (part != _hoverPart)
        {
            _hoverPart = part;
            Invalidate();
        }
        Cursor = part == PagerPart.None ? Cursors.Default : Cursors.Hand;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverPart != PagerPart.None)
        {
            _hoverPart = PagerPart.None;
            Invalidate();
        }
        Cursor = Cursors.Default;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            switch (HitPager(e.Location))
            {
                case PagerPart.Previous when _page > 0:
                    PageRequested?.Invoke(_page - 1);
                    break;
                case PagerPart.Next when _page < _pageCount - 1:
                    PageRequested?.Invoke(_page + 1);
                    break;
                case PagerPart.Label:
                    PageMenuRequested?.Invoke(new Point(_labelRect.Left, _labelRect.Top));
                    break;
            }
        }
        base.OnMouseClick(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            _pagerFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
