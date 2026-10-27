using System.Drawing.Drawing2D;

namespace RpgLauncher;

internal sealed class GameGrid : Panel
{
    private readonly Dictionary<string, GameCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private List<GameEntry> _all = new();
    private List<GameEntry> _visible = new();
    private HashSet<GameCard> _shown = new();
    private GameCard? _hoverCard;
    private Point _lastMouse = new(-1, -1);
    private readonly Font _emptyTitleFont = Theme.Font(15f, FontStyle.Bold);
    private readonly Font _emptyTextFont = Theme.Font(9.5f);

    private int _scroll;
    private int _maxScroll;
    private int _contentHeight;

    private bool _scrollbarHover;
    private bool _dragging;
    private int _dragAnchorY;
    private int _dragThumbStartY;

    // layout cache
    private int _cols = 1;
    private int _cardWidth;
    private int _cardHeight;
    private int _startX;
    private int _gap;
    private int _top;
    private int _side;

    public event Action<GameEntry>? PlayRequested;
    public event Action<GameEntry>? FavoriteToggled;
    public event Action<GameEntry, string?>? GroupToggled;
    public event Action<GameEntry>? RenameRequested;
    public event Action<GameEntry>? StorePageRequested;
    public event Action<GameEntry>? TagsRequested;
    public event Action<GameEntry, int>? RatingChanged;
    public event Action<GameEntry>? OpenFolderRequested;
    public event Action<GameEntry>? InfoRequested;
    public event Action<GameEntry>? MetadataRequested;
    public event Action<GameEntry, ImageAction>? ImageActionRequested;

    public GameGrid()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoScroll = false;
        BackColor = Theme.Bg;
        DoubleBuffered = true;
    }

    public GameCard? GetCard(string key) => _cards.TryGetValue(key, out var card) ? card : null;

    public void SyncGames(IReadOnlyList<GameEntry> all)
    {
        _all = all.ToList();

        var keys = all.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _cards.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            var card = _cards[key];
            _cards.Remove(key);
            _shown.Remove(card);
            if (ReferenceEquals(_hoverCard, card)) _hoverCard = null;
            card.Dispose();
        }

        foreach (var game in all)
        {
            if (_cards.TryGetValue(game.Key, out var card))
            {
                card.Game = game;
                card.Invalidate();
            }
            else
            {
                // Cards start hidden; PositionCards shows the ones in view.
                card = new GameCard(this, game);
                card.PlayRequested += g => PlayRequested?.Invoke(g);
                card.FavoriteToggled += g => FavoriteToggled?.Invoke(g);
                card.RatingChanged += (g, rating) => RatingChanged?.Invoke(g, rating);
                card.OpenFolderRequested += g => OpenFolderRequested?.Invoke(g);
                card.InfoRequested += g => InfoRequested?.Invoke(g);
                card.MetadataRequested += g => MetadataRequested?.Invoke(g);
                card.ImageActionRequested += (g, action) => ImageActionRequested?.Invoke(g, action);
                card.GroupToggled += (g, group) => GroupToggled?.Invoke(g, group);
                card.RenameRequested += g => RenameRequested?.Invoke(g);
                card.StorePageRequested += g => StorePageRequested?.Invoke(g);
                card.TagsRequested += g => TagsRequested?.Invoke(g);
                _cards[game.Key] = card;
            }
        }
    }

    public void SetVisibleGames(IReadOnlyList<GameEntry> games, bool resetScroll = false)
    {
        _visible = games.ToList();
        Relayout(resetScroll);
    }

    /// <summary>Redraws every card from scratch, e.g. after the style changed.</summary>
    public void ResetArtwork()
    {
        foreach (var card in _cards.Values) card.ResetArtwork();
        Invalidate();
    }

    public void RefreshCard(GameEntry game)
    {
        if (_cards.TryGetValue(game.Key, out var card)) card.Invalidate();
    }

    /// <summary>Lays the cards out again (e.g. after the card shape changed).</summary>
    public void RelayoutCards() => Relayout();

    public void ScrollToTop()
    {
        _scroll = 0;
        Relayout();
    }

    public void ScrollByWheel(int delta)
    {
        if (_maxScroll <= 0) return;
        float scale = Theme.Scale(this);
        int step = (int)Math.Round(delta / 120f * 96f * scale);
        int scroll = Math.Clamp(_scroll - step, 0, _maxScroll);
        if (scroll == _scroll) return;
        _scroll = scroll;
        Relayout();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollByWheel(e.Delta);
        base.OnMouseWheel(e);
    }

    private void Relayout(bool resetScroll = false)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        float scale = Theme.Scale(this);
        _side = (int)Math.Round(26f * scale);
        _gap = (int)Math.Round(18f * scale);
        _top = (int)Math.Round(22f * scale);
        int bottom = (int)Math.Round(26f * scale);
        int reserve = (int)Math.Round(14f * scale);

        int available = Math.Max(1, ClientSize.Width - _side * 2 - reserve);
        // Square cards are narrower: four to a row in a default-size window.
        int minWidth = (int)Math.Round((GameCard.SquareCards ? 236f : 300f) * scale);
        int maxWidth = (int)Math.Round((GameCard.SquareCards ? 300f : 400f) * scale);

        _cols = Math.Max(1, (available + _gap) / Math.Max(1, minWidth + _gap));
        while (_cols > 1 && (available - _gap * (_cols - 1)) / _cols < minWidth) _cols--;
        _cardWidth = Math.Max(1, (available - _gap * (_cols - 1)) / _cols);
        _cardWidth = Math.Min(_cardWidth, maxWidth);
        _cardHeight = GameCard.MeasureHeight(_cardWidth, scale);

        int rows = _visible.Count == 0 ? 0 : (_visible.Count + _cols - 1) / _cols;
        _contentHeight = rows == 0
            ? 0
            : _top + rows * _cardHeight + Math.Max(0, rows - 1) * _gap + bottom;

        int totalWidth = _cols * _cardWidth + Math.Max(0, _cols - 1) * _gap;
        _startX = _side + Math.Max(0, (available - totalWidth) / 2);
        _maxScroll = Math.Max(0, _contentHeight - ClientSize.Height);

        if (resetScroll) _scroll = 0;
        _scroll = Math.Clamp(_scroll, 0, _maxScroll);

        PositionCards();
        Invalidate();
    }

    /// <summary>Positions the cards in view and hides those that left it; other cards are not touched.</summary>
    private void PositionCards()
    {
        var shown = new HashSet<GameCard>();
        int rowStride = _cardHeight + _gap;
        if (_visible.Count > 0 && rowStride > 0)
        {
            int firstRow = Math.Max(0, (_scroll - _top - _cardHeight) / rowStride);
            int lastRow = Math.Max(0, (_scroll + ClientSize.Height - _top) / rowStride);
            int first = firstRow * _cols;
            int last = Math.Min(_visible.Count - 1, (lastRow + 1) * _cols - 1);

            for (int i = first; i <= last; i++)
            {
                if (!_cards.TryGetValue(_visible[i].Key, out var card)) continue;
                int col = i % _cols;
                int row = i / _cols;
                int x = _startX + col * (_cardWidth + _gap);
                int y = _top + row * rowStride - _scroll;
                if (y + _cardHeight <= 0 || y >= ClientSize.Height) continue;
                card.Bounds = new Rectangle(x, y, _cardWidth, _cardHeight);
                card.Visible = true;
                shown.Add(card);
            }
        }

        foreach (var card in _shown)
        {
            if (!shown.Contains(card)) card.Visible = false;
        }
        _shown = shown;

        // Content moved under a stationary mouse; keep the hover state in sync.
        if (_hoverCard is not null && !_hoverCard.Visible) _hoverCard = null;
        if (_lastMouse.X >= 0 && !_dragging) UpdateHover(_lastMouse);
    }

    private GameCard? CardAt(Point point)
    {
        foreach (var card in _shown)
        {
            if (card.Bounds.Contains(point)) return card;
        }
        return null;
    }

    private void UpdateHover(Point point)
    {
        var card = IsOverScrollbar(point) ? null : CardAt(point);
        if (!ReferenceEquals(card, _hoverCard))
        {
            _hoverCard?.MouseLeave();
            _hoverCard = card;
            _hoverCard?.MouseEnter();
        }

        bool hand = card is not null &&
            card.MouseMove(new Point(point.X - card.Bounds.X, point.Y - card.Bounds.Y));
        Cursor = hand || IsOverScrollbar(point) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Relayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var brush = new SolidBrush(Theme.Bg)) g.FillRectangle(brush, ClientRectangle);

        foreach (var card in _shown)
        {
            if (!card.Bounds.IntersectsWith(e.ClipRectangle)) continue;
            var state = g.Save();
            g.TranslateTransform(card.Bounds.X, card.Bounds.Y);
            card.Paint(g);
            g.Restore(state);
        }

        if (_visible.Count == 0)
        {
            DrawEmptyState(g);
        }

        if (_maxScroll > 0)
        {
            DrawScrollbar(g);
        }
    }

    private void DrawBackdrop(Graphics g)
    {
        if (ClientSize.Width < 20 || ClientSize.Height < 20) return;
        using var brush = new SolidBrush(Theme.HeaderGlow);
        float width = Math.Min(ClientSize.Width * 0.7f, 900f);
        g.FillEllipse(brush, (ClientSize.Width - width) / 2f, -ClientSize.Height * 0.55f, width, ClientSize.Height * 0.75f);
    }

    private void DrawEmptyState(Graphics g)
    {
        float scale = Theme.Scale(this);
        bool filtered = _all.Count > 0;

        float cardW = Math.Min(ClientSize.Width - 80f * scale, 560f * scale);
        float centerX = ClientSize.Width / 2f;
        float centerY = ClientSize.Height / 2f - 40f * scale;

        var iconRect = new RectangleF(centerX - 34f * scale, centerY - 92f * scale, 68f * scale, 68f * scale);
        if (Theme.Style == UiStyle.Blob && Mascots.Empty is { } blob)
        {
            float h = 110f * scale;
            Mascots.Draw(g, blob, centerX - blob.Width * h / blob.Height / 2f, centerY - 8f * scale, h);
        }
        else
        {
        using (var iconPath = Theme.Rounded(iconRect, 19f * scale))
        using (var brush = new SolidBrush(Theme.Card))
        {
            g.FillPath(brush, iconPath);
            using var border = new Pen(Theme.CardBorder, 1f);
            g.DrawPath(border, iconPath);
        }

        using (var heart = Theme.Star(new RectangleF(iconRect.Left + 22f * scale, iconRect.Top + 22f * scale,
                   24f * scale, 24f * scale)))
        using (var brush = new SolidBrush(Theme.AccentBorder))
        {
            g.FillPath(brush, heart);
        }
        }

        string title = filtered ? "No games match your filters" : "No games found";
        string text = filtered
            ? "Try a different search, tag or group, or turn off the Favorites filter."
            : "Place your RPG Maker MV / MZ game folders in this directory, then press Rescan.";

        var titleRect = new RectangleF(centerX - cardW / 2f, centerY, cardW, 30f * scale);
        Theme.DrawText(g, title, _emptyTitleFont, Theme.Text, titleRect, StringAlignment.Center);

        var textRect = new RectangleF(centerX - cardW / 2f, centerY + 34f * scale, cardW, 46f * scale);
        using (var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.Word
        })
        using (var brush = new SolidBrush(Theme.TextMuted))
        {
            g.DrawString(text, _emptyTextFont, brush, textRect, format);
        }
    }

    private RectangleF GetScrollThumb()
    {
        float scale = Theme.Scale(this);
        float trackTop = 12f * scale;
        float trackHeight = Math.Max(1f, ClientSize.Height - 24f * scale);
        float thumbHeight = Math.Max(36f * scale, trackHeight * ClientSize.Height / Math.Max(1f, _contentHeight));
        thumbHeight = Math.Min(thumbHeight, trackHeight);
        float progress = _maxScroll <= 0 ? 0f : _scroll / (float)_maxScroll;
        float thumbY = trackTop + (trackHeight - thumbHeight) * progress;
        return new RectangleF(ClientSize.Width - 12f * scale, thumbY, 5f * scale, thumbHeight);
    }

    private void DrawScrollbar(Graphics g)
    {
        float scale = Theme.Scale(this);
        var thumb = GetScrollThumb();
        bool active = _scrollbarHover || _dragging;
        using (var track = new SolidBrush(Theme.ScrollTrack))
        {
            using var path = Theme.Rounded(new RectangleF(thumb.Left, 12f * scale, thumb.Width,
                Math.Max(1f, ClientSize.Height - 24f * scale)), thumb.Width / 2f);
            g.FillPath(track, path);
        }
        using (var brush = new SolidBrush(active
                   ? Theme.ScrollThumbActive
                   : Theme.ScrollThumb))
        using (var path = Theme.Rounded(thumb, thumb.Width / 2f))
        {
            g.FillPath(brush, path);
        }
    }

    private bool IsOverScrollbar(Point point)
    {
        if (_maxScroll <= 0) return false;
        float scale = Theme.Scale(this);
        var hit = new RectangleF(ClientSize.Width - 22f * scale, 0, 22f * scale, ClientSize.Height);
        return hit.Contains(point);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            float scale = Theme.Scale(this);
            float trackTop = 12f * scale;
            float trackHeight = Math.Max(1f, ClientSize.Height - 24f * scale);
            var thumb = GetScrollThumb();
            float travel = Math.Max(1f, trackHeight - thumb.Height);
            float newY = Math.Clamp(_dragThumbStartY + (e.Y - _dragAnchorY), trackTop, trackTop + travel);
            int scroll = (int)Math.Round((newY - trackTop) / travel * _maxScroll);
            if (scroll != _scroll)
            {
                _scroll = scroll;
                Relayout();
            }
            return;
        }

        _lastMouse = e.Location;
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
        _lastMouse = new Point(-1, -1);
        _hoverCard?.MouseLeave();
        _hoverCard = null;
        if (_scrollbarHover && !_dragging)
        {
            _scrollbarHover = false;
            Invalidate();
        }
        Cursor = Cursors.Default;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        // A press on the scrollbar also produces a click; it is not meant for a card.
        if (e.Button == MouseButtons.Left && !_dragging && !IsOverScrollbar(e.Location) &&
            CardAt(e.Location) is { } card)
        {
            card.MouseClick(new Point(e.X - card.Bounds.X, e.Y - card.Bounds.Y));
        }
        base.OnMouseClick(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && IsOverScrollbar(e.Location) && _maxScroll > 0)
        {
            var thumb = GetScrollThumb();
            float scale = Theme.Scale(this);
            if (thumb.Contains(e.Location))
            {
                _dragAnchorY = e.Y;
                _dragThumbStartY = (int)thumb.Top;
            }
            else
            {
                float trackTop = 12f * scale;
                float trackHeight = Math.Max(1f, ClientSize.Height - 24f * scale);
                float travel = Math.Max(1f, trackHeight - thumb.Height);
                float progress = Math.Clamp((e.Y - trackTop - thumb.Height / 2f) / travel, 0f, 1f);
                _scroll = (int)Math.Round(progress * _maxScroll);
                Relayout();
                _dragAnchorY = e.Y;
                _dragThumbStartY = (int)GetScrollThumb().Top;
            }
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
            UpdateHover(e.Location);
            return;
        }

        if (e.Button == MouseButtons.Right && CardAt(e.Location) is { } card)
            card.ShowContextMenu(new Point(e.X - card.Bounds.X, e.Y - card.Bounds.Y));
        base.OnMouseUp(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _emptyTitleFont.Dispose();
            _emptyTextFont.Dispose();
            foreach (var card in _cards.Values) card.Dispose();
            _cards.Clear();
        }
        base.Dispose(disposing);
    }
}
