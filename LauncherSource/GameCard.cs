namespace RpgLauncher;

/// <summary>Card-image actions offered in the right-click menu.</summary>
internal enum ImageAction
{
    Change,
    AdjustFraming,
    Reset,
    UseStoreImage,
    UseTitleImage
}

/// <summary>
/// One game tile. Not a control: <see cref="GameGrid"/> lays out, paints and routes input to cards so scrolling stays cheap.
/// </summary>
internal sealed class GameCard
{
    // Shared by all cards (there can be hundreds).
    private static readonly UiFont TitleFont = Look.Font(11.4, bold: true);
    private static readonly UiFont MetaFont = Look.Font(8.5);
    private static readonly UiFont RunningFont = Look.Font(7.2, bold: true);
    private static readonly UiFont InitialFont = Look.Font(46, bold: true);

    private const double Pad = 12;
    private const double StarGap = 3;

    private Metrics? _metrics;
    private Size _metricsSize;

    // The picture scaled once to the exact screen pixels it covers, so painting is a 1:1 copy, as the WinForms
    // version did; letting the renderer shrink the thumbnail on every paint softens it.
    private Bitmap? _artwork;
    private Bitmap? _artworkSource;
    private Rect _artworkFrom;
    private PixelSize _artworkSize;
    private double _artworkScale;

    private static readonly RenderOptions PixelCopy = new() { BitmapInterpolationMode = BitmapInterpolationMode.None };

    private bool _hover;
    private int _hoverStar = -1;
    private bool _hoverHeart;
    private bool _hoverMeta;

    private readonly Control _owner;
    private bool _visible;

    public GameEntry Game { get; set; }

    /// <summary>Position in the owning grid's coordinates.</summary>
    public Rect Bounds { get; set; }

    public double Width => Bounds.Width;
    public double Height => Bounds.Height;

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            // Off-screen cards free their scaled picture; it is made again on the next paint.
            if (!value)
            {
                ReleaseArtwork();
                ResetHover();
            }
        }
    }

    public event Action<GameEntry>? PlayRequested;
    /// <summary>Play with Cheats (MV/MZ, or showing the open cheat window), or test mode for other RPG Maker games.</summary>
    public event Action<GameEntry>? CheatsRequested;
    public event Action<GameEntry>? FavoriteToggled;
    public event Action<GameEntry, int>? RatingChanged;
    public event Action<GameEntry>? OpenFolderRequested;
    public event Action<GameEntry>? InfoRequested;
    public event Action<GameEntry>? MetadataRequested;
    public event Action<GameEntry>? StorePageRequested;
    public event Action<GameEntry>? TagsRequested;
    public event Action<GameEntry, ImageAction>? ImageActionRequested;

    public event Action<GameEntry>? RenameRequested;

    /// <summary>Raised to toggle the game's membership of a group; a null group asks for a new one.</summary>
    public event Action<GameEntry, string?>? GroupToggled;

    /// <summary>The user's groups, listed in the right-click menu.</summary>
    public static IReadOnlyList<string> GroupNames { get; set; } = Array.Empty<string>();

    public GameCard(Control owner, GameEntry game)
    {
        _owner = owner;
        Game = game;
    }

    public void Invalidate()
    {
        if (_visible) _owner.InvalidateVisual();
    }

    /// <summary>Square cards show the whole picture; otherwise a 16:9 crop.</summary>
    public static bool SquareCards { get; set; }

    private static double ImageAspect => SquareCards ? 1 : 9.0 / 16.0;

    public static double MeasureHeight(double width)
    {
        double imgHeight = Math.Round((width - Pad * 2) * ImageAspect);
        return Pad + imgHeight + 82;
    }

    private sealed record Metrics(
        Rect Image, Rect Heart, Rect Title, Rect Meta,
        Rect Row, Rect Stars, Rect Playtime, Rect Badge, Rect Running);

    private Metrics GetMetrics()
    {
        if (_metrics is not null && _metricsSize == Bounds.Size) return _metrics;
        _metrics = ComputeMetrics();
        _metricsSize = Bounds.Size;
        return _metrics;
    }

    private Metrics ComputeMetrics()
    {
        double imgHeight = Math.Round((Width - Pad * 2) * ImageAspect);
        var image = new Rect(Pad, Pad, Math.Max(1, Width - Pad * 2), Math.Max(1, imgHeight));

        const double heartSize = 28;
        var heart = new Rect(image.Right - 9 - heartSize, image.Top + 9, heartSize, heartSize);

        double titleTop = image.Bottom + 10;
        var title = new Rect(Pad, titleTop, image.Width, 22);
        var meta = new Rect(Pad, titleTop + 23, image.Width, 14);
        double rowTop = titleTop + 41;
        var row = new Rect(Pad, rowTop, image.Width, 19);

        const double starSize = 17;
        var stars = new Rect(Pad, rowTop + Math.Max(0, (row.Height - starSize) / 2),
            starSize * 5 + StarGap * 4, starSize);

        var playtime = new Rect(Pad + stars.Width + 6, rowTop,
            Math.Max(20, image.Width - stars.Width - 6), row.Height);

        const double badgeHeight = 18;
        var badge = new Rect(image.Left + 9, image.Top + 9, 38, badgeHeight);
        var running = new Rect(badge.Left, badge.Bottom + 5, 70, badgeHeight);

        return new Metrics(image, heart, title, meta, row, stars, playtime, badge, running);
    }

    private static Rect GetStarRect(Metrics m, int index)
    {
        double starSize = m.Stars.Height;
        return new Rect(m.Stars.Left + index * (starSize + StarGap), m.Stars.Top, starSize, starSize);
    }

    private int HitStar(Point point)
    {
        var m = GetMetrics();
        if (point.Y < m.Stars.Top - 3 || point.Y > m.Stars.Bottom + 3) return -1;
        for (int i = 0; i < 5; i++)
        {
            var r = GetStarRect(m, i);
            var hit = new Rect(r.X - 2, r.Y - 3, r.Width + 4, r.Height + 6);
            if (hit.Contains(point)) return i;
        }
        return -1;
    }

    public void MouseEnter()
    {
        _hover = true;
        Invalidate();
    }

    public void MouseLeave()
    {
        ResetHover();
        Invalidate();
    }

    private void ResetHover()
    {
        _hover = false;
        _hoverStar = -1;
        _hoverHeart = false;
        _hoverMeta = false;
    }

    /// <summary>Handles a move at a card-local point; returns true when the hand cursor applies.</summary>
    public bool MouseMove(Point location)
    {
        var m = GetMetrics();
        int star = HitStar(location);
        bool heart = m.Heart.Contains(location);
        bool meta = m.Image.Contains(location) || m.Title.Contains(location) ||
                    m.Meta.Contains(location);

        if (star != _hoverStar || heart != _hoverHeart || meta != _hoverMeta)
        {
            _hoverStar = star;
            _hoverHeart = heart;
            _hoverMeta = meta;
            Invalidate();
        }

        return star >= 0 || heart || meta;
    }

    /// <summary>Handles a left click at a card-local point.</summary>
    public void MouseClick(Point location)
    {
        var m = GetMetrics();
        if (m.Heart.Contains(location))
        {
            FavoriteToggled?.Invoke(Game);
            return;
        }

        int star = HitStar(location);
        if (star >= 0)
        {
            int newRating = (Game.Rating == star + 1) ? 0 : star + 1;
            RatingChanged?.Invoke(Game, newRating);
            return;
        }

        PlayRequested?.Invoke(Game);
    }

    /// <summary>Shows the right-click menu at a card-local point.</summary>
    public void ShowContextMenu(Point location)
    {
        var menu = Menus.Create();

        var play = Menus.Item("Play", (_, _) => PlayRequested?.Invoke(Game));
        play.Bold = true;
        menu.Items.Add(play);
        if (Game.Family == EngineFamily.MvMz)
            menu.Items.Add(Menus.Item(Game.CheatsOn ? "Show Cheats" : "Play with Cheats", (_, _) => CheatsRequested?.Invoke(Game)));
        else if (Game.Family != EngineFamily.Wolf)
            menu.Items.Add(Menus.Item("Play in Test Mode", (_, _) => CheatsRequested?.Invoke(Game)));

        menu.Items.Add(Menus.Item("Open game folder", (_, _) => OpenFolderRequested?.Invoke(Game)));

        if (!string.IsNullOrWhiteSpace(Game.ExecutablePath))
        {
            menu.Items.Add(Menus.Item("Show executable", (_, _) => ShowInFileManager(Game.ExecutablePath!)));
        }

        menu.Items.Add(Menus.Separator());

        var favorite = Menus.Item(Game.Favorite ? "Remove from favorites" : "Add to favorites",
            (_, _) => FavoriteToggled?.Invoke(Game));
        menu.Items.Add(favorite);

        var rating = Menus.Item("Rating");
        var noRating = Menus.Item("No rating", (_, _) => RatingChanged?.Invoke(Game, 0));
        noRating.Checked = Game.Rating == 0;
        rating.DropDownItems.Add(noRating);
        rating.DropDownItems.Add(Menus.Separator());
        for (int i = 1; i <= 5; i++)
        {
            int value = i;
            var item = Menus.Item(new string('★', i) + new string('☆', 5 - i),
                (_, _) => RatingChanged?.Invoke(Game, value));
            item.Checked = Game.Rating == value;
            rating.DropDownItems.Add(item);
        }
        Menus.StyleSubmenu(rating, menu, checks: true);
        menu.Items.Add(rating);

        var groups = Menus.Item("Groups");
        foreach (var name in GroupNames)
        {
            string group = name;
            var item = Menus.Item(MenuText(group), (_, _) => GroupToggled?.Invoke(Game, group));
            item.Checked = Game.Groups.Contains(group, StringComparer.CurrentCultureIgnoreCase);
            groups.DropDownItems.Add(item);
        }
        if (GroupNames.Count > 0) groups.DropDownItems.Add(Menus.Separator());
        groups.DropDownItems.Add(Menus.Item("New Group…", (_, _) => GroupToggled?.Invoke(Game, null)));
        Menus.StyleSubmenu(groups, menu, checks: true);
        menu.Items.Add(groups);

        menu.Items.Add(Menus.Separator());
        menu.Items.Add(Menus.Item("Rename…", (_, _) => RenameRequested?.Invoke(Game)));
        menu.Items.Add(Menus.Item("Change image…",
            (_, _) => ImageActionRequested?.Invoke(Game, ImageAction.Change)));
        var framing = Menus.Item("Adjust image framing…",
            (_, _) => ImageActionRequested?.Invoke(Game, ImageAction.AdjustFraming));
        framing.Enabled = Game.HasFramableImage;
        menu.Items.Add(framing);
        if (Game.CustomImagePath is not null || Game.Framing is not null)
        {
            menu.Items.Add(Menus.Item(Game.CustomImagePath is not null ? "Remove custom image" : "Reset image framing",
                (_, _) => ImageActionRequested?.Invoke(Game, ImageAction.Reset)));
        }
        if (Game.CustomImagePath is null && !string.IsNullOrWhiteSpace(Game.Metadata?.ImageUrl))
        {
            if (!Game.WantsStoreImage)
            {
                menu.Items.Add(Menus.Item("Use DLsite image",
                    (_, _) => ImageActionRequested?.Invoke(Game, ImageAction.UseStoreImage)));
            }
            else if (Game.Title1Path is not null)
            {
                menu.Items.Add(Menus.Item("Use title screen image",
                    (_, _) => ImageActionRequested?.Invoke(Game, ImageAction.UseTitleImage)));
            }
        }

        menu.Items.Add(Menus.Separator());
        if (Game.Metadata is { } metadata)
        {
            var info = Menus.Item("Game info…", (_, _) => InfoRequested?.Invoke(Game));
            info.Bold = true;
            menu.Items.Add(info);
            menu.Items.Add(Menus.Item("Add Tags…", (_, _) => TagsRequested?.Invoke(Game)));
            menu.Items.Add(Menus.Item("Open DLsite page", (_, _) => OpenUrl(metadata.UrlFor(Game.MetadataLanguage))));
            menu.Items.Add(Menus.Item("Search Nepchan", (_, _) => SearchNepchan()));
            menu.Items.Add(Menus.Item("Refresh metadata", (_, _) => MetadataRequested?.Invoke(Game)));
        }
        else
        {
            menu.Items.Add(Menus.Item("Add Tags…", (_, _) => TagsRequested?.Invoke(Game)));
            menu.Items.Add(Menus.Item("Look up metadata", (_, _) => MetadataRequested?.Invoke(Game)));
            menu.Items.Add(Menus.Item("Search Nepchan", (_, _) => SearchNepchan()));
        }
        menu.Items.Add(Menus.Item("Set DLsite page…", (_, _) => StorePageRequested?.Invoke(Game)));

        Menus.Show(menu, _owner, new Point(Bounds.X + location.X, Bounds.Y + location.Y));
    }

    /// <summary>Menu text for user-typed names: a lone "&" would otherwise underline the next letter.</summary>
    public static string MenuText(string text) => text.Replace("&", "&&");

    private void SearchNepchan()
    {
        // Nepchan has an anti-bot challenge, so the search opens in the browser.
        string query = Uri.EscapeDataString(MetadataService.CleanTitle(Game.Title));
        OpenUrl("https://nepchan.org/v?q=" + query);
    }

    private static void OpenUrl(string? url)
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

    private static void ShowInFileManager(string path)
    {
        try
        {
            Shell.ShowInFileManager(path);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Paints the card; the caller translates <paramref name="g"/> to the card's origin.</summary>
    public void Paint(DrawingContext g)
    {
        var m = GetMetrics();

        double inset = Look.BorderWidth / 2;
        var cardRect = new Rect(inset, inset, Width - Look.BorderWidth, Height - Look.BorderWidth);
        g.DrawRectangle(Look.Brush(_hover ? Look.CardHover : Look.Card),
            Look.Pen(_hover ? Look.CardBorderHover : Look.CardBorder, Look.BorderWidth),
            Look.Rounded(cardRect, Look.CardRadius));

        DrawArtwork(g, m.Image);

        if (_hover) DrawHoverOverlay(g, m.Image);

        if (_hover) DrawHeart(g, m.Heart);

        Look.DrawText(g, Game.CardTitle, TitleFont, Look.Text, m.Title);
        if (Game.Metadata is not null)
            Look.DrawText(g, Game.MetadataLine, MetaFont, Look.TextDim, m.Meta);

        DrawStars(g, m.Stars);
        DrawPlaytime(g, m.Playtime);

        if (Game.IsRunning) DrawRunning(g, m.Running);
    }

    /// <summary>The picture with rounded corners and a dark shade along its bottom; a placeholder while there is none.</summary>
    private void DrawArtwork(DrawingContext g, Rect rect)
    {
        bool square = SquareCards;
        // Square cards show the regular thumbnail whole until the full picture loads, or when there is none.
        var thumbnail = square ? Game.FullThumbnail ?? Game.Thumbnail : Game.Thumbnail;

        using (g.PushClip(Look.Rounded(rect, Look.ImageRadius)))
        {
            if (thumbnail is not null)
            {
                var size = thumbnail.Size;
                if (square)
                {
                    g.FillRectangle(Look.Gradient(Look.PlaceholderTop, Look.PlaceholderBottom), rect);
                    double fit = Math.Min(rect.Width / size.Width, rect.Height / size.Height);
                    double w = size.Width * fit, h = size.Height * fit;
                    DrawPicture(g, thumbnail, new Rect(size),
                        new Rect(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h));
                }
                else
                {
                    double cover = Math.Max(rect.Width / size.Width, rect.Height / size.Height);
                    double w = rect.Width / cover, h = rect.Height / cover;
                    DrawPicture(g, thumbnail, new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h), rect);
                }
            }
            else
            {
                string initial = string.IsNullOrWhiteSpace(Game.CardTitle) ? "?" : Game.CardTitle[..1].ToUpperInvariant();
                g.FillRectangle(Look.Gradient(Look.PlaceholderTop, Look.PlaceholderBottom, 45), rect);
                Look.DrawText(g, initial, InitialFont, Look.Alpha(150, Look.Gold), rect);
            }

            double overlayHeight = Math.Min(rect.Height * 0.55, 70);
            var overlayRect = new Rect(rect.Left, rect.Bottom - overlayHeight, rect.Width, overlayHeight);
            g.FillRectangle(Look.Gradient(Color.FromArgb(0, 8, 8, 12), Color.FromArgb(135, 8, 8, 12)), overlayRect);
        }
    }

    /// <summary>Draws part of a picture into <paramref name="dest"/>, scaled once to the screen pixels it covers.</summary>
    private void DrawPicture(DrawingContext g, Bitmap picture, Rect from, Rect dest)
    {
        double scale = TopLevel.GetTopLevel(_owner)?.RenderScaling ?? 1;
        // The grid starts on a screen pixel; start the picture on one too (in grid coordinates), so the copy is 1:1.
        var size = new PixelSize(Math.Max(1, (int)Math.Round(dest.Width * scale)), Math.Max(1, (int)Math.Round(dest.Height * scale)));
        double left = Math.Round((Bounds.X + dest.X) * scale) / scale - Bounds.X;
        double top = Math.Round((Bounds.Y + dest.Y) * scale) / scale - Bounds.Y;

        if (_artwork is null || !ReferenceEquals(_artworkSource, picture) || _artworkFrom != from ||
            _artworkSize != size || _artworkScale != scale)
        {
            ReleaseArtwork();
            _artwork = ImageKit.Resample(picture, from, size, scale);
            _artworkSource = picture;
            _artworkFrom = from;
            _artworkSize = size;
            _artworkScale = scale;
        }
        using (g.PushRenderOptions(PixelCopy))
            g.DrawImage(_artwork, new Rect(_artwork.Size), new Rect(left, top, size.Width / scale, size.Height / scale));
    }

    /// <summary>Drops the scaled picture so the next paint makes it again (e.g. after a style change).</summary>
    public void ResetArtwork()
    {
        ReleaseArtwork();
        Invalidate();
    }

    private void ReleaseArtwork()
    {
        // The thumbnail itself belongs to the game; only a scaled copy made here is freed.
        if (_artwork is not null && !ReferenceEquals(_artwork, _artworkSource)) _artwork.Dispose();
        _artwork = null;
        _artworkSource = null;
    }

    private static void DrawHoverOverlay(DrawingContext g, Rect rect)
    {
        g.DrawRectangle(Look.Brush(Color.FromArgb(72, 8, 8, 12)), null, Look.Rounded(rect, Look.ImageRadius));

        const double radius = 26;
        var circle = new Rect(rect.Left + rect.Width / 2 - radius,
            rect.Top + rect.Height / 2 - radius, radius * 2, radius * 2);
        g.DrawEllipse(Look.Brush(Look.Alpha(238, Look.Gold)), null, circle);

        const double t = 10.5;
        double cx = circle.Left + circle.Width / 2, cy = circle.Top + circle.Height / 2;
        g.DrawGeometry(Look.Brush(Color.FromRgb(24, 20, 12)), null, Look.Lines(true,
            new Point(cx - t * 0.55, cy - t),
            new Point(cx - t * 0.55, cy + t),
            new Point(cx + t * 0.85, cy)));
    }

    private static void DrawRunning(DrawingContext g, Rect badge)
    {
        const string text = "RUNNING";
        var size = Look.Measure(text, RunningFont);
        var rect = new Rect(badge.Left, badge.Top, size.Width + 26, badge.Height);
        g.DrawRectangle(Look.Brush(Color.FromArgb(190, 9, 13, 11)), Look.Pen(Look.Alpha(90, Look.Success), 1),
            Look.Rounded(rect, rect.Height / 2));

        const double dot = 5;
        g.DrawEllipse(Look.Brush(Look.Success), null, new Rect(rect.Left + 8, rect.Top + (rect.Height - dot) / 2, dot, dot));
        Look.DrawText(g, text, RunningFont, Look.Success,
            new Rect(rect.Left + 17, rect.Top, rect.Width - 22, rect.Height));
    }

    private void DrawHeart(DrawingContext g, Rect circleRect)
    {
        bool favorite = Game.Favorite;
        if (favorite || _hoverHeart)
            g.DrawEllipse(Look.Brush(Color.FromArgb((byte)(favorite ? 210 : 150), 8, 8, 12)), null, circleRect);

        const double size = 13;
        var heartRect = new Rect(circleRect.Left + (circleRect.Width - size) / 2,
            circleRect.Top + (circleRect.Height - size) / 2, size, size);
        var heart = Look.Heart(heartRect);
        if (favorite)
        {
            g.DrawGeometry(Look.Brush(Look.Gold), null, heart);
        }
        else
        {
            g.DrawGeometry(null, Look.Pen(Color.FromArgb((byte)(_hoverHeart ? 250 : 205), 255, 255, 255),
                _hoverHeart ? 2 : 1.5, round: true), heart);
        }
    }

    private void DrawStars(DrawingContext g, Rect starsRect)
    {
        double size = starsRect.Height;
        for (int i = 0; i < 5; i++)
        {
            var r = new Rect(starsRect.Left + i * (size + StarGap), starsRect.Top, size, size);
            bool filled = i < Game.Rating;
            bool hover = _hoverStar >= i;
            Color color = filled
                ? (hover ? Look.StarFillBright : Look.StarFill)
                : (hover ? Look.Alpha(120, Look.StarFill) : Look.StarEmpty);
            g.DrawGeometry(Look.Brush(color), null, Look.Star(r));
        }
    }

    private void DrawPlaytime(DrawingContext g, Rect rect)
    {
        string text;
        if (Game.PlaySeconds > 0)
        {
            text = Look.FormatDuration(Game.PlaySeconds);
        }
        else if (Game.LastPlayedUtc is not null)
        {
            text = "played " + Look.LastPlayedText(Game.LastPlayedUtc);
        }
        else
        {
            return; // an empty corner reads better than "not played" on every card
        }

        Look.DrawText(g, text, MetaFont, Game.PlaySeconds > 0 ? Look.TextMuted : Look.TextDim, rect,
            TextAlignment.Right);
    }
}
