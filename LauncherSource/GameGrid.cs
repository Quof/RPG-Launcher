using Avalonia.Rendering;

namespace RpgLauncher;

/// <summary>The library: a scrolling grid of <see cref="GameCard"/>s, with its own scrollbar and empty state.</summary>
internal sealed class GameGrid : Control, ICustomHitTest
{
    private const double Side = 26;
    private const double Gap = 18;
    private const double Top = 22;
    private const double Bottom = 26;
    private const double Reserve = 14;

    private readonly Dictionary<string, GameCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private List<GameEntry> _all = new();
    private List<GameEntry> _visible = new();
    private HashSet<GameCard> _shown = new();
    private GameCard? _hoverCard;
    private Point _lastMouse = new(-1, -1);
    private readonly UiFont _emptyTitleFont = Look.Font(15, bold: true);
    private readonly UiFont _emptyTextFont = Look.Font(9.5);
    private readonly Cursor _hand = new(StandardCursorType.Hand);

    private double _scroll;
    private double _maxScroll;
    private double _contentHeight;

    private bool _scrollbarHover;
    private bool _dragging;
    private double _dragAnchorY;
    private double _dragThumbStartY;
    private bool _pressed;

    // layout cache
    private int _cols = 1;
    private double _cardWidth;
    private double _cardHeight;
    private double _startX;

    public event Action<GameEntry>? PlayRequested;
    public event Action<GameEntry>? CheatsRequested;
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
        // Cards scrolled partly out of view must not draw over the controls around the grid.
        ClipToBounds = true;
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
            card.Visible = false;
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
                card.CheatsRequested += g => CheatsRequested?.Invoke(g);
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
        InvalidateVisual();
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

    /// <summary>Scrolls by a wheel delta in WinForms units: 120 per notch.</summary>
    public void ScrollByWheel(int delta)
    {
        if (_maxScroll <= 0) return;
        double step = Math.Round(delta / 120.0 * 96);
        double scroll = Math.Clamp(_scroll - step, 0, _maxScroll);
        if (scroll == _scroll) return;
        _scroll = scroll;
        Relayout();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ScrollByWheel((int)Math.Round(e.Delta.Y * 120));
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    private void Relayout(bool resetScroll = false)
    {
        double width = Bounds.Width, height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        double available = Math.Max(1, width - Side * 2 - Reserve);
        // Square cards are narrower: four to a row in a default-size window.
        double minWidth = GameCard.SquareCards ? 236 : 300;
        double maxWidth = GameCard.SquareCards ? 300 : 400;

        _cols = Math.Max(1, (int)((available + Gap) / (minWidth + Gap)));
        while (_cols > 1 && (available - Gap * (_cols - 1)) / _cols < minWidth) _cols--;
        _cardWidth = Math.Max(1, Math.Floor((available - Gap * (_cols - 1)) / _cols));
        _cardWidth = Math.Min(_cardWidth, maxWidth);
        _cardHeight = GameCard.MeasureHeight(_cardWidth);

        int rows = _visible.Count == 0 ? 0 : (_visible.Count + _cols - 1) / _cols;
        _contentHeight = rows == 0
            ? 0
            : Top + rows * _cardHeight + Math.Max(0, rows - 1) * Gap + Bottom;

        double totalWidth = _cols * _cardWidth + Math.Max(0, _cols - 1) * Gap;
        _startX = Side + Math.Max(0, Math.Floor((available - totalWidth) / 2));
        _maxScroll = Math.Max(0, _contentHeight - height);

        if (resetScroll) _scroll = 0;
        _scroll = Math.Clamp(_scroll, 0, _maxScroll);

        PositionCards();
        InvalidateVisual();
    }

    /// <summary>Positions the cards in view and hides those that left it; other cards are not touched.</summary>
    private void PositionCards()
    {
        var shown = new HashSet<GameCard>();
        double rowStride = _cardHeight + Gap;
        double height = Bounds.Height;
        if (_visible.Count > 0 && rowStride > 0)
        {
            int firstRow = Math.Max(0, (int)((_scroll - Top - _cardHeight) / rowStride));
            int lastRow = Math.Max(0, (int)((_scroll + height - Top) / rowStride));
            int first = firstRow * _cols;
            int last = Math.Min(_visible.Count - 1, (lastRow + 1) * _cols - 1);

            for (int i = first; i <= last; i++)
            {
                if (!_cards.TryGetValue(_visible[i].Key, out var card)) continue;
                int col = i % _cols;
                int row = i / _cols;
                double x = _startX + col * (_cardWidth + Gap);
                double y = Top + row * rowStride - _scroll;
                if (y + _cardHeight <= 0 || y >= height) continue;
                card.Bounds = new Rect(x, y, _cardWidth, _cardHeight);
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
        Cursor = hand || IsOverScrollbar(point) ? _hand : Cursor.Default;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout();
    }

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public override void Render(DrawingContext g)
    {
        g.FillRectangle(Look.Brush(Look.Bg), new Rect(Bounds.Size));

        foreach (var card in _shown)
        {
            using (g.PushTransform(Matrix.CreateTranslation(card.Bounds.X, card.Bounds.Y)))
                card.Paint(g);
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

    private void DrawBackdrop(DrawingContext g)
    {
        double width = Bounds.Width, height = Bounds.Height;
        if (width < 20 || height < 20) return;
        double w = Math.Min(width * 0.7, 900);
        g.DrawEllipse(Look.Brush(Look.HeaderGlow), null, new Rect((width - w) / 2, -height * 0.55, w, height * 0.75));
    }

    private void DrawEmptyState(DrawingContext g)
    {
        double width = Bounds.Width, height = Bounds.Height;
        bool filtered = _all.Count > 0;

        double cardW = Math.Min(width - 80, 560);
        double centerX = width / 2;
        double centerY = height / 2 - 40;

        var iconRect = new Rect(centerX - 34, centerY - 92, 68, 68);
        if (Look.Style == UiStyle.Blob && Mascots.Empty is { } blob)
        {
            double h = 110;
            Mascots.Draw(g, blob, centerX - Mascots.WidthAt(blob, h) / 2, centerY - 8, h);
        }
        else
        {
            g.DrawRectangle(Look.Brush(Look.Card), Look.Pen(Look.CardBorder, 1), Look.Rounded(iconRect, 19));
            g.DrawGeometry(Look.Brush(Look.AccentBorder), null,
                Look.Star(new Rect(iconRect.Left + 22, iconRect.Top + 22, 24, 24)));
        }

        string title = filtered ? "No games match your filters" : "No games found";
        string text = filtered
            ? "Try a different search, tag or group, or turn off the Favorites filter."
            : "Place your game folders in this directory (or add a folder from ⋯), then press Rescan.";

        var titleRect = new Rect(centerX - cardW / 2, centerY, cardW, 30);
        Look.DrawText(g, title, _emptyTitleFont, Look.Text, titleRect, TextAlignment.Center);

        var textRect = new Rect(centerX - cardW / 2, centerY + 34, cardW, 46);
        Look.DrawText(g, text, _emptyTextFont, Look.TextMuted, textRect, TextAlignment.Center, TextLine.Top, wrap: true);
    }

    private Rect GetScrollThumb()
    {
        double height = Bounds.Height;
        double trackTop = 12;
        double trackHeight = Math.Max(1, height - 24);
        double thumbHeight = Math.Max(36, trackHeight * height / Math.Max(1, _contentHeight));
        thumbHeight = Math.Min(thumbHeight, trackHeight);
        double progress = _maxScroll <= 0 ? 0 : _scroll / _maxScroll;
        double thumbY = trackTop + (trackHeight - thumbHeight) * progress;
        return new Rect(Bounds.Width - 12, thumbY, 5, thumbHeight);
    }

    private void DrawScrollbar(DrawingContext g)
    {
        var thumb = GetScrollThumb();
        bool active = _scrollbarHover || _dragging;
        g.DrawRectangle(Look.Brush(Look.ScrollTrack), null,
            Look.Rounded(new Rect(thumb.Left, 12, thumb.Width, Math.Max(1, Bounds.Height - 24)), thumb.Width / 2));
        g.DrawRectangle(Look.Brush(active ? Look.ScrollThumbActive : Look.ScrollThumb), null,
            Look.Rounded(thumb, thumb.Width / 2));
    }

    private bool IsOverScrollbar(Point point)
    {
        if (_maxScroll <= 0) return false;
        var hit = new Rect(Bounds.Width - 22, 0, 22, Bounds.Height);
        return hit.Contains(point);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_dragging)
        {
            double trackTop = 12;
            double trackHeight = Math.Max(1, Bounds.Height - 24);
            var thumb = GetScrollThumb();
            double travel = Math.Max(1, trackHeight - thumb.Height);
            double newY = Math.Clamp(_dragThumbStartY + (point.Y - _dragAnchorY), trackTop, trackTop + travel);
            double scroll = Math.Round((newY - trackTop) / travel * _maxScroll);
            if (scroll != _scroll)
            {
                _scroll = scroll;
                Relayout();
            }
            return;
        }

        _lastMouse = point;
        bool over = IsOverScrollbar(point);
        if (over != _scrollbarHover)
        {
            _scrollbarHover = over;
            InvalidateVisual();
        }
        UpdateHover(point);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _lastMouse = new Point(-1, -1);
        _hoverCard?.MouseLeave();
        _hoverCard = null;
        if (_scrollbarHover && !_dragging)
        {
            _scrollbarHover = false;
            InvalidateVisual();
        }
        Cursor = Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        // The second press of a double click is no click (WinForms raised DoubleClick instead): a game starts once.
        _pressed = e.ClickCount % 2 == 1;
        if (IsOverScrollbar(point) && _maxScroll > 0)
        {
            var thumb = GetScrollThumb();
            if (thumb.Contains(point))
            {
                _dragAnchorY = point.Y;
                _dragThumbStartY = thumb.Top;
            }
            else
            {
                double trackTop = 12;
                double trackHeight = Math.Max(1, Bounds.Height - 24);
                double travel = Math.Max(1, trackHeight - thumb.Height);
                double progress = Math.Clamp((point.Y - trackTop - thumb.Height / 2) / travel, 0, 1);
                _scroll = Math.Round(progress * _maxScroll);
                Relayout();
                _dragAnchorY = point.Y;
                _dragThumbStartY = GetScrollThumb().Top;
            }
            _dragging = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetPosition(this);
        if (_dragging)
        {
            _dragging = false;
            _pressed = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
            UpdateHover(point);
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            // A click is a left press and release inside the grid, for the card under the release.
            // A release on the scrollbar is not meant for a card.
            bool click = _pressed;
            _pressed = false;
            if (click && new Rect(Bounds.Size).Contains(point) && !IsOverScrollbar(point) && CardAt(point) is { } card)
                card.MouseClick(new Point(point.X - card.Bounds.X, point.Y - card.Bounds.Y));
        }
        else if (e.InitialPressMouseButton == MouseButton.Right && CardAt(point) is { } card)
        {
            card.ShowContextMenu(new Point(point.X - card.Bounds.X, point.Y - card.Bounds.Y));
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_dragging) return;
        _dragging = false;
        InvalidateVisual();
    }
}
