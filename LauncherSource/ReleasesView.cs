using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace RpgLauncher;

/// <summary>New DLsite game releases grouped by day, newest first; only visible cards are painted.</summary>
internal sealed class ReleasesView : Control
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

    private static readonly Font HeaderFont = Theme.Font(12f, FontStyle.Bold);
    private static readonly Font HeaderCountFont = Theme.Font(9f);
    private static readonly Font TitleFont = Theme.Font(9.4f, FontStyle.Bold);
    private static readonly Font MakerFont = Theme.Font(8.4f);
    private static readonly Font MetaFont = Theme.Font(8.4f);
    private static readonly Font BadgeFont = Theme.Font(7.2f, FontStyle.Bold);
    private static readonly Font EmptyFont = Theme.Font(11f, FontStyle.Bold);
    private static readonly Font EmptyTextFont = Theme.Font(9.5f);
    private static readonly Font MenuBoldFont = Theme.Font(9.5f, FontStyle.Bold);

    private readonly ReleaseCovers _covers;
    private List<ReleaseDay> _days = new();
    private readonly List<Section> _sections = new();
    private string _filter = "";
    private MetadataLanguage _language = MetadataLanguage.English;

    private int _scroll;
    private int _maxScroll;
    private int _contentHeight;
    private bool _dragging;
    private int _dragAnchorY;
    private int _dragThumbStartY;
    private bool _scrollbarHover;

    private int _cols = 1;
    private int _cardW;
    private int _cardH;
    private int _startX;
    private int _gap;
    private int _pad;
    private Rectangle _imageRect;

    private ReleaseItem? _hover;

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
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        _covers = new ReleaseCovers(this, coverDirectory);
        _covers.Loaded += _ => Invalidate();
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
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        float s = Theme.Scale(this);
        int R(float v) => (int)Math.Round(v * s);

        int side = R(26f), reserve = R(14f);
        _gap = R(16f);
        _pad = R(10f);
        int available = Math.Max(1, ClientSize.Width - side * 2 - reserve);
        int minW = R(210f), maxW = R(290f);
        _cols = Math.Max(1, (available + _gap) / (minW + _gap));
        _cardW = Math.Min(maxW, Math.Max(1, (available - _gap * (_cols - 1)) / _cols));
        int totalW = _cols * _cardW + (_cols - 1) * _gap;
        _startX = side + Math.Max(0, (available - totalW) / 2);

        int imageW = _cardW - _pad * 2;
        int imageH = (int)Math.Round(imageW * 0.75);
        _imageRect = new Rectangle(_pad, _pad, imageW, imageH);
        _cardH = _pad + imageH + R(8f) + R(36f) + R(18f) + R(20f) + _pad;

        var tokens = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _sections.Clear();
        int y = R(14f);
        int headerH = R(42f);
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
            y = section.Bottom + R(20f);
        }

        _contentHeight = _sections.Count == 0 ? 0 : y + R(10f);
        _maxScroll = Math.Max(0, _contentHeight - ClientSize.Height);
        if (resetScroll) _scroll = 0;
        _scroll = Math.Clamp(_scroll, 0, _maxScroll);
        _hover = null;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    public void ScrollByWheel(int delta)
    {
        if (_maxScroll <= 0) return;
        int step = (int)Math.Round(delta / 120f * 96f * Theme.Scale(this));
        int scroll = Math.Clamp(_scroll - step, 0, _maxScroll);
        if (scroll == _scroll) return;
        _scroll = scroll;
        Invalidate();
        UpdateHover(PointToClient(Cursor.Position));
    }

    public void ScrollToTop()
    {
        _scroll = 0;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollByWheel(e.Delta);
        base.OnMouseWheel(e);
    }

    private Rectangle CardBounds(Section section, int index)
    {
        int col = index % _cols, row = index / _cols;
        return new Rectangle(_startX + col * (_cardW + _gap),
            section.CardsY + row * (_cardH + _gap) - _scroll, _cardW, _cardH);
    }

    private ReleaseItem? ItemAt(Point point, out Rectangle bounds) => ItemAt(point, out bounds, out _);

    private ReleaseItem? ItemAt(Point point, out Rectangle bounds, out DateTime date)
    {
        bounds = Rectangle.Empty;
        date = default;
        int contentY = point.Y + _scroll;
        foreach (var section in _sections)
        {
            if (contentY < section.CardsY || contentY > section.Bottom) continue;
            int row = (contentY - section.CardsY) / (_cardH + _gap);
            int col = (point.X - _startX) / (_cardW + _gap);
            if (col < 0 || col >= _cols || point.X < _startX) return null;
            int index = row * _cols + col;
            if (index >= section.Items.Count) return null;
            bounds = CardBounds(section, index);
            date = section.Day.Date;
            return bounds.Contains(point) ? section.Items[index] : null;
        }
        return null;
    }

    // ---- painting ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, ClientRectangle);

        if (_sections.Count == 0)
        {
            DrawEmpty(g);
            return;
        }

        float s = Theme.Scale(this);
        int viewTop = _scroll, viewBottom = _scroll + ClientSize.Height;
        foreach (var section in _sections)
        {
            if (section.Bottom < viewTop || section.HeaderY > viewBottom) continue;
            DrawHeader(g, section, s);

            int firstRow = Math.Max(0, (viewTop - section.CardsY) / (_cardH + _gap));
            int lastRow = Math.Min(section.Rows - 1, (viewBottom - section.CardsY) / (_cardH + _gap));
            for (int row = firstRow; row <= lastRow; row++)
            {
                for (int col = 0; col < _cols; col++)
                {
                    int index = row * _cols + col;
                    if (index >= section.Items.Count) break;
                    var bounds = CardBounds(section, index);
                    if (!bounds.IntersectsWith(e.ClipRectangle)) continue;
                    var state = g.Save();
                    g.TranslateTransform(bounds.X, bounds.Y);
                    DrawCard(g, section.Items[index], s);
                    g.Restore(state);
                }
            }
        }

        if (_maxScroll > 0) DrawScrollbar(g, s);
    }

    private static string DayLabel(DateTime date)
    {
        var today = ReleaseStore.TodayJst();
        string name = date.ToString("ddd, MMM d", CultureInfo.GetCultureInfo("en-US"));
        if (date == today) return "Today \u00b7 " + name;
        if (date == today.AddDays(-1)) return "Yesterday \u00b7 " + name;
        return name;
    }

    private void DrawHeader(Graphics g, Section section, float s)
    {
        int y = section.HeaderY - _scroll;
        string label = DayLabel(section.Day.Date);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        var size = TextRenderer.MeasureText(g, label, HeaderFont, new Size(int.MaxValue, int.MaxValue), flags);
        var rect = new Rectangle(_startX, y, size.Width + 2, (int)(30 * s));
        TextRenderer.DrawText(g, label, HeaderFont, rect, Theme.Text, flags);

        int total = section.Day.Items.Count;
        string count = section.Items.Count == total
            ? $"{total} {(total == 1 ? "game" : "games")}"
            : $"{section.Items.Count} of {total} games";
        TextRenderer.DrawText(g, count, HeaderCountFont,
            new Rectangle(rect.Right + (int)(10 * s), y, (int)(200 * s), rect.Height), Theme.TextDim, flags);

        int lineY = y + (int)(36 * s);
        using var pen = new Pen(Theme.Divider, Theme.IsLight ? 1.5f : 1f);
        g.DrawLine(pen, _startX, lineY, _startX + _cols * _cardW + (_cols - 1) * _gap, lineY);
    }

    private static readonly StringFormat TitleFormat = new()
    {
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.LineLimit
    };

    private void DrawCard(Graphics g, ReleaseItem item, float s)
    {
        bool hover = ReferenceEquals(item, _hover);
        float inset = Theme.BorderWidth / 2f;
        var card = new RectangleF(inset, inset, _cardW - Theme.BorderWidth, _cardH - Theme.BorderWidth);
        using (var path = Theme.Rounded(card, Math.Max(0f, Theme.CardRadius - 2f) * s))
        using (var fill = new SolidBrush(hover ? Theme.CardHover : Theme.Card))
        using (var border = new Pen(hover ? Theme.CardBorderHover : Theme.CardBorder, Theme.BorderWidth))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        var cover = _covers.Get(item, _imageRect.Size);
        using (var clip = Theme.Rounded(_imageRect, Math.Max(0f, Theme.ImageRadius - 1f) * s))
        {
            if (cover is not null)
            {
                var state = g.Save();
                g.SetClip(clip);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(cover, _imageRect);
                g.Restore(state);
            }
            else
            {
                using var placeholder = new LinearGradientBrush(_imageRect,
                    Theme.PlaceholderTop, Theme.PlaceholderBottom, 45f);
                g.FillPath(placeholder, clip);
                Theme.DrawText(g, item.ProductId, MakerFont, Theme.TextDim, _imageRect, StringAlignment.Center);
            }

            if (hover)
            {
                using var veil = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
                g.FillPath(veil, clip);
            }
        }

        float bx = _imageRect.Left + 7f * s, by = _imageRect.Top + 7f * s;
        if (item.DiscountPercent > 0)
            bx += DrawBadge(g, $"-{item.DiscountPercent}%", bx, by, Theme.Gold, Color.FromArgb(225, 24, 20, 12), s) + 5f * s;
        if (item.HasEnglish)
            DrawBadge(g, "EN", bx, by, Color.FromArgb(150, 200, 255), Color.FromArgb(225, 12, 18, 30), s);
        if (IsOwned?.Invoke(item.ProductId) == true)
        {
            float w = MeasureBadge(g, "IN LIBRARY", s);
            DrawBadge(g, "IN LIBRARY", _imageRect.Right - 7f * s - w, by, Theme.Success, Color.FromArgb(225, 9, 22, 14), s);
        }

        float textX = _pad;
        float textW = _cardW - _pad * 2;
        float y = _imageRect.Bottom + 8f * s;
        using (var brush = new SolidBrush(Theme.Text))
            g.DrawString(item.Title, TitleFont, brush, new RectangleF(textX, y, textW, 36f * s), TitleFormat);
        y += 36f * s;

        Theme.DrawText(g, item.MakerFor(_language), MakerFont, Theme.TextMuted, new RectangleF(textX, y, textW, 18f * s));
        y += 18f * s;

        string price = item.Price > 0 ? "¥" + item.Price.ToString("N0", CultureInfo.InvariantCulture) : "Free";
        string type = item.TypeFor(_language);
        var metaRect = new RectangleF(textX, y, textW, 20f * s);
        var metaFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping;
        var metaBox = Rectangle.Round(metaRect);
        // GDI+ (title, circle) indents text slightly; nudge the GDI line to match.
        metaBox.X += (int)Math.Round(2f * s);
        metaBox.Width -= (int)Math.Round(2f * s);
        int priceX = metaBox.X;
        if (!string.IsNullOrEmpty(type))
        {
            string prefix = type + " \u00b7 ";
            TextRenderer.DrawText(g, prefix, MetaFont, metaBox, Theme.TextDim, metaFlags);
            priceX += TextRenderer.MeasureText(g, prefix, MetaFont, metaBox.Size, metaFlags).Width;
        }
        TextRenderer.DrawText(g, price, MetaFont,
            new Rectangle(priceX, metaBox.Y, Math.Max(1, metaBox.Right - priceX), metaBox.Height),
            item.DiscountPercent > 0 ? Theme.Gold : Theme.TextMuted, metaFlags);
        if (item.Rating is { } rating)
        {
            Theme.DrawText(g, $"★ {rating:0.0} ({item.RatingCount})", MetaFont, Theme.TextMuted, metaRect,
                StringAlignment.Far);
        }
    }

    private static float MeasureBadge(Graphics g, string text, float s) =>
        g.MeasureString(text, BadgeFont).Width + 8f * s;

    private static float DrawBadge(Graphics g, string text, float x, float y, Color fore, Color back, float s)
    {
        float w = MeasureBadge(g, text, s);
        var rect = new RectangleF(x, y, w, 17f * s);
        using (var path = Theme.Rounded(rect, 4f * s))
        using (var brush = new SolidBrush(back))
            g.FillPath(brush, path);
        Theme.DrawText(g, text, BadgeFont, fore, rect, StringAlignment.Center);
        return w;
    }

    private void DrawEmpty(Graphics g)
    {
        float s = Theme.Scale(this);
        float w = Math.Min(ClientSize.Width - 60f * s, 560f * s);
        float cx = ClientSize.Width / 2f, cy = ClientSize.Height / 2f - 30f * s;
        var blob = Loading ? Mascots.For(MascotSpot.ReleasesLoading) : Mascots.Enabled ? Mascots.Empty : null;
        if (blob is not null)
        {
            float h = 110f * s;
            Mascots.Draw(g, blob, cx - blob.Width * h / blob.Height / 2f, cy - 10f * s, h);
        }
        Theme.DrawText(g, EmptyTitle, EmptyFont, Theme.Text,
            new RectangleF(cx - w / 2f, cy, w, 30f * s), StringAlignment.Center);
        if (!string.IsNullOrEmpty(EmptyText))
            Theme.DrawText(g, EmptyText, EmptyTextFont, Theme.TextMuted,
                new RectangleF(cx - w / 2f, cy + 32f * s, w, 24f * s), StringAlignment.Center);
    }

    // ---- scrollbar ----

    private RectangleF ScrollThumb(float s)
    {
        float trackTop = 12f * s;
        float track = Math.Max(1f, ClientSize.Height - 24f * s);
        float thumbH = Math.Min(track, Math.Max(36f * s, track * ClientSize.Height / Math.Max(1f, _contentHeight)));
        float progress = _maxScroll <= 0 ? 0f : _scroll / (float)_maxScroll;
        return new RectangleF(ClientSize.Width - 12f * s, trackTop + (track - thumbH) * progress, 5f * s, thumbH);
    }

    private void DrawScrollbar(Graphics g, float s)
    {
        var thumb = ScrollThumb(s);
        using (var track = new SolidBrush(Theme.ScrollTrack))
        using (var path = Theme.Rounded(new RectangleF(thumb.Left, 12f * s, thumb.Width,
                   Math.Max(1f, ClientSize.Height - 24f * s)), thumb.Width / 2f))
            g.FillPath(track, path);
        using (var brush = new SolidBrush(_scrollbarHover || _dragging
                   ? Theme.ScrollThumbActive : Theme.ScrollThumb))
        using (var path = Theme.Rounded(thumb, thumb.Width / 2f))
            g.FillPath(brush, path);
    }

    private bool IsOverScrollbar(Point p)
    {
        if (_maxScroll <= 0) return false;
        float s = Theme.Scale(this);
        return p.X >= ClientSize.Width - 22f * s;
    }

    // ---- mouse ----

    private void UpdateHover(Point point)
    {
        var item = IsOverScrollbar(point) || !ClientRectangle.Contains(point) ? null : ItemAt(point, out _);
        if (!ReferenceEquals(item, _hover))
        {
            _hover = item;
            Invalidate();
        }
        Cursor = item is not null || IsOverScrollbar(point) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            float s = Theme.Scale(this);
            float trackTop = 12f * s;
            float travel = Math.Max(1f, ClientSize.Height - 24f * s - ScrollThumb(s).Height);
            float newY = Math.Clamp(_dragThumbStartY + (e.Y - _dragAnchorY), trackTop, trackTop + travel);
            int scroll = (int)Math.Round((newY - trackTop) / travel * _maxScroll);
            if (scroll != _scroll)
            {
                _scroll = scroll;
                Invalidate();
            }
            return;
        }

        bool over = IsOverScrollbar(e.Location);
        if (over != _scrollbarHover)
        {
            _scrollbarHover = over;
            Invalidate();
        }
        UpdateHover(e.Location);
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover is not null || _scrollbarHover)
        {
            _hover = null;
            _scrollbarHover = false;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && IsOverScrollbar(e.Location))
        {
            float s = Theme.Scale(this);
            var thumb = ScrollThumb(s);
            if (!thumb.Contains(e.Location))
            {
                float trackTop = 12f * s;
                float travel = Math.Max(1f, ClientSize.Height - 24f * s - thumb.Height);
                float progress = Math.Clamp((e.Y - trackTop - thumb.Height / 2f) / travel, 0f, 1f);
                _scroll = (int)Math.Round(progress * _maxScroll);
                thumb = ScrollThumb(s);
            }
            _dragAnchorY = e.Y;
            _dragThumbStartY = (int)thumb.Top;
            _dragging = true;
            Capture = true;
            Invalidate();
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Capture = false;
            Invalidate();
            return;
        }

        if (e.Button == MouseButtons.Right && ItemAt(e.Location, out _, out var date) is { } item)
            ShowMenu(item, date, e.Location);
        base.OnMouseUp(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && !_dragging && !IsOverScrollbar(e.Location) &&
            ItemAt(e.Location, out _) is { } item)
            OpenUrl(item.Url);
        base.OnMouseClick(e);
    }

    private void ShowMenu(ReleaseItem item, DateTime date, Point location)
    {
        var menu = Menus.Create();
        var open = Menus.Item("Open on DLsite", (_, _) => OpenUrl(item.Url));
        open.Font = MenuBoldFont;
        menu.Items.Add(open);
        menu.Items.Add(Menus.Item("Release info\u2026", (_, _) => InfoRequested?.Invoke(item, date)));
        menu.Items.Add(Menus.Item("Search Nepchan", (_, _) =>
            OpenUrl("https://nepchan.org/v?q=" + Uri.EscapeDataString(MetadataService.CleanTitle(item.Title)))));
        menu.Items.Add(Menus.Separator());
        menu.Items.Add(Menus.Item("Copy title", (_, _) => CopyText(item.Title)));
        menu.Items.Add(Menus.Item($"Copy product ID ({item.ProductId})", (_, _) => CopyText(item.ProductId)));
        Menus.Show(menu, this, location);
    }

    private static void CopyText(string text)
    {
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
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
        if (disposing) _covers.Dispose();
        base.Dispose(disposing);
    }
}
