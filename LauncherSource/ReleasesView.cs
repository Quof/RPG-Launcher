using System.Globalization;
using Avalonia.Rendering;

namespace RpgLauncher;

/// <summary>New DLsite game releases grouped by day, newest first; only visible cards are painted.</summary>
internal sealed class ReleasesView : Control, ICustomHitTest
{
    private sealed class Section
    {
        public required ReleaseDay Day { get; init; }
        public required List<ReleaseItem> Items { get; init; }
        public int HeaderY;
        public int CardsY;
        public int Rows;
        public int Bottom;
    }

    private static readonly UiFont HeaderFont = Look.Font(12, bold: true);
    private static readonly UiFont HeaderCountFont = Look.Font(9);
    private static readonly UiFont TitleFont = Look.Font(9.4, bold: true);
    private static readonly UiFont MakerFont = Look.Font(8.4);
    private static readonly UiFont MetaFont = Look.Font(8.4);
    private static readonly UiFont BadgeFont = Look.Font(7.2, bold: true);
    private static readonly UiFont EmptyFont = Look.Font(11, bold: true);
    private static readonly UiFont EmptyTextFont = Look.Font(9.5);

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    // Covers are decoded at the size they are drawn, so they are copied pixel for pixel.
    private static readonly RenderOptions PixelCopy = new() { BitmapInterpolationMode = BitmapInterpolationMode.None };

    private readonly ReleaseCovers _covers;
    private List<ReleaseDay> _days = new();
    private readonly List<Section> _sections = new();
    private string _filter = "";
    private MetadataLanguage _language = MetadataLanguage.English;

    private int _scroll;
    private int _maxScroll;
    private int _contentHeight;
    private bool _dragging;
    private double _dragAnchorY;
    private double _dragThumbStartY;
    private bool _scrollbarHover;

    private int _cols = 1;
    private int _cardW;
    private int _cardH;
    private int _startX;
    private int _gap;
    private int _pad;
    private Rect _imageRect;

    private ReleaseItem? _hover;
    private Point? _pointer;
    private bool _clickPending;
    private TopLevel? _topLevel;

    /// <summary>Raised from the right-click menu with the release and its release day.</summary>
    public event Action<ReleaseItem, DateTime>? InfoRequested;

    /// <summary>Returns true for product IDs already in the user's library.</summary>
    public Func<string, bool>? IsOwned { get; set; }

    /// <summary>Shown in place of the list when there is nothing to show.</summary>
    public string EmptyTitle { get; set; } = "No releases loaded yet";
    public string EmptyText { get; set; } = "";

    /// <summary>True while releases are being downloaded (changes the empty-state blob).</summary>
    public bool Loading { get; set; }

    public int ShownCount => _sections.Sum(s => s.Items.Count);
    public int TotalCount => _days.Sum(d => d.Items.Count);

    public ReleasesView(string coverDirectory)
    {
        ClipToBounds = true;
        _covers = new ReleaseCovers(coverDirectory);
        _covers.Loaded += _ => InvalidateVisual();
    }

    public void SetDays(List<ReleaseDay> days, bool resetScroll = false)
    {
        _days = days;
        Relayout(resetScroll);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            value = (value ?? "").Trim();
            if (value == _filter) return;
            _filter = value;
            Relayout(resetScroll: true);
        }
    }

    /// <summary>Which store text (circle, type, tags) to show.</summary>
    public MetadataLanguage Language
    {
        get => _language;
        set
        {
            if (value == _language) return;
            _language = value;
            Relayout();
        }
    }

    /// <summary>Tag filter (in the current language); the owner changes it and calls <see cref="RefreshFilter"/>.</summary>
    public TagFilter? Tags { get; set; }

    public void RefreshFilter() => Relayout(resetScroll: true);

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private bool Matches(ReleaseItem item, string[] tokens)
    {
        if (Tags is { IsEmpty: false } tags && !tags.Matches(item.TagsFor(_language)))
            return false;

        bool Has(string? text, string token) =>
            !string.IsNullOrEmpty(text) && text.Contains(token, StringComparison.CurrentCultureIgnoreCase);

        // Search both languages so a Japanese or English word finds the same release.
        return tokens.All(token =>
            Has(item.Title, token) || Has(item.Maker, token) || Has(item.WorkTypeName, token) ||
            Has(item.ProductId, token) || item.Tags.Any(t => Has(t, token)) ||
            (item.English is { } en &&
             (Has(en.Maker, token) || Has(en.WorkTypeName, token) || en.Tags.Any(t => Has(t, token)))));
    }

    private void Relayout(bool resetScroll = false)
    {
        // Avalonia lays a control out only after it is shown, so this also runs before there is a size:
        // the sections are still filtered (ShownCount is right at once) and placed again on the first resize.
        int width = (int)Bounds.Width, height = (int)Bounds.Height;
        int side = 26, reserve = 14;
        _gap = 16;
        _pad = 10;
        int available = Math.Max(1, width - side * 2 - reserve);
        int minW = 210, maxW = 290;
        _cols = Math.Max(1, (available + _gap) / (minW + _gap));
        _cardW = Math.Min(maxW, Math.Max(1, (available - _gap * (_cols - 1)) / _cols));
        int totalW = _cols * _cardW + (_cols - 1) * _gap;
        _startX = side + Math.Max(0, (available - totalW) / 2);

        int imageW = Math.Max(1, _cardW - _pad * 2);
        int imageH = (int)Math.Round(imageW * 0.75);
        _imageRect = new Rect(_pad, _pad, imageW, imageH);
        _cardH = _pad + imageH + 8 + 36 + 18 + 20 + _pad;

        var tokens = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _sections.Clear();
        int y = 14;
        int headerH = 42;
        foreach (var day in _days)
        {
            var items = tokens.Length == 0 && (Tags is null || Tags.IsEmpty)
                ? day.Items
                : day.Items.Where(i => Matches(i, tokens)).ToList();
            if (items.Count == 0) continue;
            int rows = (items.Count + _cols - 1) / _cols;
            var section = new Section { Day = day, Items = items, HeaderY = y, CardsY = y + headerH, Rows = rows };
            section.Bottom = section.CardsY + rows * _cardH + (rows - 1) * _gap;
            _sections.Add(section);
            y = section.Bottom + 20;
        }

        _contentHeight = _sections.Count == 0 ? 0 : y + 10;
        _maxScroll = Math.Max(0, _contentHeight - height);
        if (resetScroll) _scroll = 0;
        _scroll = Math.Clamp(_scroll, 0, _maxScroll);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout();
    }

    /// <summary>Scrolls by mouse wheel notches; positive scrolls up.</summary>
    public void ScrollByWheel(double notches)
    {
        if (_maxScroll <= 0) return;
        int step = (int)Math.Round(notches * 96);
        int scroll = Math.Clamp(_scroll - step, 0, _maxScroll);
        if (scroll == _scroll) return;
        _scroll = scroll;
        InvalidateVisual();
        if (_pointer is { } pointer) UpdateHover(pointer);
    }

    public void ScrollToTop()
    {
        _scroll = 0;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _pointer = e.GetPosition(this);
        ScrollByWheel(e.Delta.Y);
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    private Rect CardBounds(Section section, int index)
    {
        int col = index % _cols, row = index / _cols;
        return new Rect(_startX + col * (_cardW + _gap),
            section.CardsY + row * (_cardH + _gap) - _scroll, _cardW, _cardH);
    }

    private ReleaseItem? ItemAt(Point point, out Rect bounds) => ItemAt(point, out bounds, out _);

    private ReleaseItem? ItemAt(Point point, out Rect bounds, out DateTime date)
    {
        bounds = default;
        date = default;
        double contentY = point.Y + _scroll;
        foreach (var section in _sections)
        {
            if (contentY < section.CardsY || contentY > section.Bottom) continue;
            int row = (int)((contentY - section.CardsY) / (_cardH + _gap));
            int col = (int)((point.X - _startX) / (_cardW + _gap));
            if (col < 0 || col >= _cols || point.X < _startX) return null;
            int index = row * _cols + col;
            if (index >= section.Items.Count) return null;
            bounds = CardBounds(section, index);
            date = section.Day.Date;
            return Contains(bounds, point) ? section.Items[index] : null;
        }
        return null;
    }

    /// <summary>Point in rectangle, right and bottom edges excluded (as WinForms tested it).</summary>
    private static bool Contains(Rect rect, Point point) =>
        point.X >= rect.X && point.X < rect.Right && point.Y >= rect.Y && point.Y < rect.Bottom;

    // ---- painting ----

    public override void Render(DrawingContext g)
    {
        var view = new Rect(Bounds.Size);
        g.FillRectangle(Look.Brush(Look.Bg), view);

        if (_sections.Count == 0)
        {
            DrawEmpty(g);
            return;
        }

        // Covers are decoded at device pixels so they stay sharp on scaled screens.
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var coverSize = new PixelSize((int)Math.Round(_imageRect.Width * scale), (int)Math.Round(_imageRect.Height * scale));
        int viewTop = _scroll, viewBottom = _scroll + (int)Bounds.Height;
        foreach (var section in _sections)
        {
            if (section.Bottom < viewTop || section.HeaderY > viewBottom) continue;
            DrawHeader(g, section);

            int firstRow = Math.Max(0, (viewTop - section.CardsY) / (_cardH + _gap));
            int lastRow = Math.Min(section.Rows - 1, (viewBottom - section.CardsY) / (_cardH + _gap));
            for (int row = firstRow; row <= lastRow; row++)
            {
                for (int col = 0; col < _cols; col++)
                {
                    int index = row * _cols + col;
                    if (index >= section.Items.Count) break;
                    var bounds = CardBounds(section, index);
                    if (!bounds.Intersects(view)) continue;
                    using (g.PushTransform(Matrix.CreateTranslation(bounds.X, bounds.Y)))
                        DrawCard(g, section.Items[index], coverSize);
                }
            }
        }

        if (_maxScroll > 0) DrawScrollbar(g);
    }

    private static string DayLabel(DateTime date)
    {
        var today = ReleaseStore.TodayJst();
        string name = date.ToString("ddd, MMM d", CultureInfo.GetCultureInfo("en-US"));
        if (date == today) return "Today · " + name;
        if (date == today.AddDays(-1)) return "Yesterday · " + name;
        return name;
    }

    private void DrawHeader(DrawingContext g, Section section)
    {
        int y = section.HeaderY - _scroll;
        string label = DayLabel(section.Day.Date);
        var rect = new Rect(_startX, y, Math.Ceiling(Look.Measure(label, HeaderFont).Width) + 2, 30);
        Look.DrawText(g, label, HeaderFont, Look.Text, rect);

        int total = section.Day.Items.Count;
        string count = section.Items.Count == total
            ? $"{total} {(total == 1 ? "game" : "games")}"
            : $"{section.Items.Count} of {total} games";
        Look.DrawText(g, count, HeaderCountFont, Look.TextDim, new Rect(rect.Right + 10, y, 200, rect.Height));

        double lineY = y + 36 + 0.5;
        g.DrawLine(Look.Pen(Look.Divider, Look.IsLight ? 1.5 : 1),
            new Point(_startX, lineY), new Point(_startX + _cols * _cardW + (_cols - 1) * _gap, lineY));
    }

    private void DrawCard(DrawingContext g, ReleaseItem item, PixelSize coverSize)
    {
        bool hover = ReferenceEquals(item, _hover);
        double inset = Look.BorderWidth / 2;
        var card = new Rect(inset, inset, _cardW - Look.BorderWidth, _cardH - Look.BorderWidth);
        g.DrawRectangle(Look.Brush(hover ? Look.CardHover : Look.Card),
            Look.Pen(hover ? Look.CardBorderHover : Look.CardBorder, Look.BorderWidth),
            Look.Rounded(card, Math.Max(0, Look.CardRadius - 2)));

        var cover = _covers.Get(item, coverSize);
        var clip = Look.Rounded(_imageRect, Math.Max(0, Look.ImageRadius - 1));
        if (cover is not null)
        {
            using (g.PushClip(clip))
            using (g.PushRenderOptions(PixelCopy))
                g.DrawImage(cover, new Rect(cover.Size), _imageRect);
        }
        else
        {
            g.DrawRectangle(Look.Gradient(Look.PlaceholderTop, Look.PlaceholderBottom, 45), null, clip);
            Look.DrawText(g, item.ProductId, MakerFont, Look.TextDim, _imageRect, TextAlignment.Center);
        }

        if (hover)
            g.DrawRectangle(Look.Brush(Color.FromArgb(40, 255, 255, 255)), null, clip);

        double bx = _imageRect.Left + 7, by = _imageRect.Top + 7;
        if (item.DiscountPercent > 0)
            bx += DrawBadge(g, $"-{item.DiscountPercent}%", bx, by, Look.Gold, Color.FromArgb(225, 24, 20, 12)) + 5;
        if (item.HasEnglish)
            DrawBadge(g, "EN", bx, by, Color.FromRgb(150, 200, 255), Color.FromArgb(225, 12, 18, 30));
        if (IsOwned?.Invoke(item.ProductId) == true)
        {
            double w = MeasureBadge("IN LIBRARY");
            DrawBadge(g, "IN LIBRARY", _imageRect.Right - 7 - w, by, Look.Success, Color.FromArgb(225, 9, 22, 14));
        }

        double textX = _pad;
        double textW = _cardW - _pad * 2;
        double y = _imageRect.Bottom + 8;
        Look.DrawText(g, item.Title, TitleFont, Look.Text, new Rect(textX, y, textW, 36),
            TextAlignment.Left, TextLine.Top, wrap: true);
        y += 36;

        Look.DrawText(g, item.MakerFor(_language), MakerFont, Look.TextMuted, new Rect(textX, y, textW, 18));
        y += 18;

        string price = item.Price > 0 ? "¥" + item.Price.ToString("N0", CultureInfo.InvariantCulture) : "Free";
        string type = item.TypeFor(_language);
        var metaRect = new Rect(textX, y, textW, 20);
        double priceX = metaRect.X;
        if (!string.IsNullOrEmpty(type))
        {
            string prefix = type + " · ";
            Look.DrawText(g, prefix, MetaFont, Look.TextDim, metaRect);
            priceX += Look.Measure(prefix, MetaFont).Width;
        }
        Look.DrawText(g, price, MetaFont, item.DiscountPercent > 0 ? Look.Gold : Look.TextMuted,
            new Rect(priceX, metaRect.Y, Math.Max(1, metaRect.Right - priceX), metaRect.Height));
        if (item.Rating is { } rating)
        {
            Look.DrawText(g, $"★ {rating:0.0} ({item.RatingCount})", MetaFont, Look.TextMuted, metaRect,
                TextAlignment.Right);
        }
    }

    // GDI+ measured text with 1/6 em of room on each side; it is kept so badges stay the same size.
    private static double MeasureBadge(string text) =>
        Look.Measure(text, BadgeFont).Width + BadgeFont.Size / 3 + 8;

    private static double DrawBadge(DrawingContext g, string text, double x, double y, Color fore, Color back)
    {
        double w = MeasureBadge(text);
        var rect = new Rect(x, y, w, 17);
        g.DrawRectangle(Look.Brush(back), null, Look.Rounded(rect, 4));
        Look.DrawText(g, text, BadgeFont, fore, rect, TextAlignment.Center);
        return w;
    }

    private void DrawEmpty(DrawingContext g)
    {
        double width = Bounds.Width, height = Bounds.Height;
        double w = Math.Min(width - 60, 560);
        double cx = width / 2, cy = height / 2 - 30;
        var blob = Loading ? Mascots.For(MascotSpot.ReleasesLoading) : Mascots.Enabled ? Mascots.Empty : null;
        if (blob is not null)
        {
            double h = 110;
            Mascots.Draw(g, blob, cx - Mascots.WidthAt(blob, h) / 2, cy - 10, h);
        }
        Look.DrawText(g, EmptyTitle, EmptyFont, Look.Text, new Rect(cx - w / 2, cy, w, 30), TextAlignment.Center);
        if (!string.IsNullOrEmpty(EmptyText))
            Look.DrawText(g, EmptyText, EmptyTextFont, Look.TextMuted, new Rect(cx - w / 2, cy + 32, w, 24),
                TextAlignment.Center);
    }

    // ---- scrollbar ----

    private Rect ScrollThumb()
    {
        double height = Bounds.Height;
        double trackTop = 12;
        double track = Math.Max(1, height - 24);
        double thumbH = Math.Min(track, Math.Max(36, track * height / Math.Max(1, _contentHeight)));
        double progress = _maxScroll <= 0 ? 0 : _scroll / (double)_maxScroll;
        return new Rect(Bounds.Width - 12, trackTop + (track - thumbH) * progress, 5, thumbH);
    }

    private void DrawScrollbar(DrawingContext g)
    {
        var thumb = ScrollThumb();
        g.DrawRectangle(Look.Brush(Look.ScrollTrack), null,
            Look.Rounded(new Rect(thumb.Left, 12, thumb.Width, Math.Max(1, Bounds.Height - 24)), thumb.Width / 2));
        g.DrawRectangle(Look.Brush(_scrollbarHover || _dragging ? Look.ScrollThumbActive : Look.ScrollThumb), null,
            Look.Rounded(thumb, thumb.Width / 2));
    }

    private bool IsOverScrollbar(Point p)
    {
        if (_maxScroll <= 0) return false;
        return p.X >= Bounds.Width - 22;
    }

    // ---- mouse ----

    private void UpdateHover(Point point)
    {
        var item = IsOverScrollbar(point) || !Contains(new Rect(Bounds.Size), point) ? null : ItemAt(point, out _);
        if (!ReferenceEquals(item, _hover))
        {
            _hover = item;
            InvalidateVisual();
        }
        Cursor = item is not null || IsOverScrollbar(point) ? HandCursor : Cursor.Default;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        _pointer = point;
        if (_dragging)
        {
            double trackTop = 12;
            double travel = Math.Max(1, Bounds.Height - 24 - ScrollThumb().Height);
            double newY = Math.Clamp(_dragThumbStartY + (point.Y - _dragAnchorY), trackTop, trackTop + travel);
            int scroll = (int)Math.Round((newY - trackTop) / travel * _maxScroll);
            if (scroll != _scroll)
            {
                _scroll = scroll;
                InvalidateVisual();
            }
            return;
        }

        bool over = IsOverScrollbar(point);
        if (over != _scrollbarHover)
        {
            _scrollbarHover = over;
            InvalidateVisual();
        }
        UpdateHover(point);
        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _pointer = null;
        if (_hover is not null || _scrollbarHover)
        {
            _hover = null;
            _scrollbarHover = false;
            InvalidateVisual();
        }
        base.OnPointerExited(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        bool left = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        // A WinForms click: pressed and released inside, but not the second press of a double-click.
        _clickPending = left && e.ClickCount % 2 == 1;
        if (left && IsOverScrollbar(point))
        {
            var thumb = ScrollThumb();
            if (!Contains(thumb, point))
            {
                double trackTop = 12;
                double travel = Math.Max(1, Bounds.Height - 24 - thumb.Height);
                double progress = Math.Clamp((point.Y - trackTop - thumb.Height / 2) / travel, 0, 1);
                _scroll = (int)Math.Round(progress * _maxScroll);
                thumb = ScrollThumb();
            }
            _dragAnchorY = point.Y;
            _dragThumbStartY = thumb.Top;
            _dragging = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var point = e.GetPosition(this);
        bool click = _clickPending && e.InitialPressMouseButton == MouseButton.Left && Contains(new Rect(Bounds.Size), point);
        _clickPending = false;
        // WinForms raised the click before MouseUp, while a scrollbar drag still counted as dragging.
        if (click && !_dragging && !IsOverScrollbar(point) && ItemAt(point, out _) is { } clicked)
            OpenUrl(clicked.Url);

        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Right && ItemAt(point, out _, out var date) is { } item)
            ShowMenu(item, date, point);
        base.OnPointerReleased(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_dragging) return;
        _dragging = false;
        InvalidateVisual();
    }

    private void ShowMenu(ReleaseItem item, DateTime date, Point location)
    {
        var menu = Menus.Create();
        var open = Menus.Item("Open on DLsite", (_, _) => OpenUrl(item.Url));
        open.Bold = true;
        menu.Items.Add(open);
        menu.Items.Add(Menus.Item("Release info…", (_, _) => InfoRequested?.Invoke(item, date)));
        menu.Items.Add(Menus.Item("Search Nepchan", (_, _) =>
            OpenUrl("https://nepchan.org/v?q=" + Uri.EscapeDataString(MetadataService.CleanTitle(item.Title)))));
        menu.Items.Add(Menus.Separator());
        menu.Items.Add(Menus.Item("Copy title", (_, _) => CopyText(item.Title)));
        menu.Items.Add(Menus.Item($"Copy product ID ({item.ProductId})", (_, _) => CopyText(item.ProductId)));
        Menus.Show(menu, this, location);
    }

    private async void CopyText(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch
        {
            // clipboard busy
        }
    }

    private static void OpenUrl(string url)
    {
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

    // ---- window ----

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += OnScalingChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        // What WinForms did in Dispose; covers load again if the view comes back.
        _covers.Clear();
    }

    // Covers are decoded at device pixels: on another screen's scale they are decoded again.
    private void OnScalingChanged(object? sender, EventArgs e) => InvalidateVisual();
}
