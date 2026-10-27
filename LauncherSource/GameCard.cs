using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;

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
internal sealed class GameCard : IDisposable
{
    // Shared by all cards (there can be hundreds).
    private static readonly Font TitleFont = Theme.Font(11.4f, FontStyle.Bold);
    private static readonly Font MetaFont = Theme.Font(8.5f);
    private static readonly Font RunningFont = Theme.Font(7.2f, FontStyle.Bold);
    private static readonly Font InitialFont = Theme.Font(46f, FontStyle.Bold);
    private static readonly Font MenuBoldFont = Theme.Font(9.5f, FontStyle.Bold);

    private Metrics? _metrics;
    private Size _metricsSize;
    private int _metricsDpi;

    // Artwork pre-rendered at the card's exact size (rounded corners, bottom shade) so painting is a 1:1 copy.
    private Bitmap? _artwork;
    private Bitmap? _artworkSource;
    private Size _artworkSize;
    private string? _artworkInitial;
    private bool _artworkSquare;

    private bool _hover;
    private int _hoverStar = -1;
    private bool _hoverHeart;
    private bool _hoverMeta;

    private readonly Control _owner;
    private bool _visible;

    public GameEntry Game { get; set; }

    /// <summary>Position in the owning grid's client coordinates.</summary>
    public Rectangle Bounds { get; set; }

    public int Width => Bounds.Width;
    public int Height => Bounds.Height;

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            // Off-screen cards free their scaled artwork; it is rebuilt on the next paint.
            if (!value)
            {
                ReleaseArtwork();
                ResetHover();
            }
        }
    }

    public event Action<GameEntry>? PlayRequested;
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

    private float Scale => Theme.Scale(_owner);

    public void Invalidate()
    {
        if (_visible) _owner.Invalidate(Bounds);
    }

    /// <summary>Square cards show the whole picture; otherwise a 16:9 crop.</summary>
    public static bool SquareCards { get; set; }

    private static float ImageAspect => SquareCards ? 1f : 9f / 16f;

    public static int MeasureHeight(int width, float scale)
    {
        int pad = (int)Math.Round(12f * scale);
        int imgHeight = (int)Math.Round((width - pad * 2) * ImageAspect);
        return pad + imgHeight + (int)Math.Round(82f * scale);
    }

    private sealed record Metrics(
        RectangleF Image, RectangleF Heart, RectangleF Title, RectangleF Meta,
        RectangleF Row, RectangleF Stars, RectangleF Playtime, RectangleF Badge, RectangleF Running);

    private Metrics GetMetrics()
    {
        if (_metrics is not null && _metricsSize == Bounds.Size && _metricsDpi == _owner.DeviceDpi) return _metrics;
        _metrics = ComputeMetrics();
        _metricsSize = Bounds.Size;
        _metricsDpi = _owner.DeviceDpi;
        return _metrics;
    }

    private Metrics ComputeMetrics()
    {
        float scale = Scale;
        int pad = (int)Math.Round(12f * scale);
        int imgHeight = (int)Math.Round((Width - pad * 2) * ImageAspect);
        var image = new RectangleF(pad, pad, Math.Max(1, Width - pad * 2), Math.Max(1, imgHeight));

        int heartSize = (int)Math.Round(28f * scale);
        var heart = new RectangleF((float)(image.Right - Math.Round(9f * scale) - heartSize),
            (float)(image.Top + Math.Round(9f * scale)), heartSize, heartSize);

        float titleTop = image.Bottom + (float)Math.Round(10f * scale);
        var title = new RectangleF(pad, titleTop, image.Width, (float)Math.Round(22f * scale));
        var meta = new RectangleF(pad, titleTop + (float)Math.Round(23f * scale), image.Width, (float)Math.Round(14f * scale));
        float rowTop = titleTop + (float)Math.Round(41f * scale);
        var row = new RectangleF(pad, rowTop, image.Width, (float)Math.Round(19f * scale));

        float starSize = Math.Max(12f, 17f * scale);
        float starGap = Math.Max(2f, 3f * scale);
        var stars = new RectangleF(pad, rowTop + Math.Max(0f, (row.Height - starSize) / 2f),
            starSize * 5f + starGap * 4f, starSize);

        var playtime = new RectangleF(pad + stars.Width + (float)Math.Round(6f * scale), rowTop,
            Math.Max(20f, image.Width - stars.Width - (float)Math.Round(6f * scale)), row.Height);

        float badgeHeight = (float)Math.Round(18f * scale);
        var badge = new RectangleF((float)(image.Left + Math.Round(9f * scale)), (float)(image.Top + Math.Round(9f * scale)),
            (float)Math.Round(38f * scale), badgeHeight);
        var running = new RectangleF(badge.Left, badge.Bottom + (float)Math.Round(5f * scale),
            (float)Math.Round(70f * scale), badgeHeight);

        return new Metrics(image, heart, title, meta, row, stars, playtime, badge, running);
    }

    private RectangleF GetStarRect(Metrics m, int index)
    {
        float starSize = m.Stars.Height;
        float gap = Math.Max(2f, 3f * Scale);
        return new RectangleF(m.Stars.Left + index * (starSize + gap), m.Stars.Top, starSize, starSize);
    }

    private int HitStar(Point point)
    {
        var m = GetMetrics();
        if (point.Y < m.Stars.Top - 3f || point.Y > m.Stars.Bottom + 3f) return -1;
        for (int i = 0; i < 5; i++)
        {
            var r = GetStarRect(m, i);
            var hit = new RectangleF(r.X - 2f, r.Y - 3f, r.Width + 4f, r.Height + 6f);
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
        play.Font = MenuBoldFont;
        menu.Items.Add(play);

        menu.Items.Add(Menus.Item("Open game folder", (_, _) => OpenFolderRequested?.Invoke(Game)));

        if (!string.IsNullOrWhiteSpace(Game.ExecutablePath))
        {
            menu.Items.Add(Menus.Item("Show executable", (_, _) => ShowInExplorer(Game.ExecutablePath!)));
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
            var item = Menus.Item(new string('\u2605', i) + new string('\u2606', 5 - i),
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
        groups.DropDownItems.Add(Menus.Item("New Group\u2026", (_, _) => GroupToggled?.Invoke(Game, null)));
        Menus.StyleSubmenu(groups, menu, checks: true);
        menu.Items.Add(groups);

        menu.Items.Add(Menus.Separator());
        menu.Items.Add(Menus.Item("Rename\u2026", (_, _) => RenameRequested?.Invoke(Game)));
        menu.Items.Add(Menus.Item("Change image\u2026",
            (_, _) => ImageActionRequested?.Invoke(Game, ImageAction.Change)));
        var framing = Menus.Item("Adjust image framing\u2026",
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
            var info = Menus.Item("Game info\u2026", (_, _) => InfoRequested?.Invoke(Game));
            info.Font = MenuBoldFont;
            menu.Items.Add(info);
            menu.Items.Add(Menus.Item("Add Tags\u2026", (_, _) => TagsRequested?.Invoke(Game)));
            menu.Items.Add(Menus.Item("Open DLsite page", (_, _) => OpenUrl(metadata.UrlFor(Game.MetadataLanguage))));
            menu.Items.Add(Menus.Item("Search Nepchan", (_, _) => SearchNepchan()));
            menu.Items.Add(Menus.Item("Refresh metadata", (_, _) => MetadataRequested?.Invoke(Game)));
        }
        else
        {
            menu.Items.Add(Menus.Item("Add Tags\u2026", (_, _) => TagsRequested?.Invoke(Game)));
            menu.Items.Add(Menus.Item("Look up metadata", (_, _) => MetadataRequested?.Invoke(Game)));
            menu.Items.Add(Menus.Item("Search Nepchan", (_, _) => SearchNepchan()));
        }
        menu.Items.Add(Menus.Item("Set DLsite page\u2026", (_, _) => StorePageRequested?.Invoke(Game)));

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
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // opening the browser is best effort
        }
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Paints the card; the caller translates <paramref name="g"/> to the card's origin.</summary>
    public void Paint(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        float scale = Scale;
        var m = GetMetrics();

        float inset = Theme.BorderWidth / 2f;
        var cardRect = new RectangleF(inset, inset, Width - Theme.BorderWidth, Height - Theme.BorderWidth);
        using (var cardPath = Theme.Rounded(cardRect, Theme.CardRadius * scale))
        using (var cardBrush = new SolidBrush(_hover ? Theme.CardHover : Theme.Card))
        {
            g.FillPath(cardBrush, cardPath);
            using var border = new Pen(_hover ? Theme.CardBorderHover : Theme.CardBorder, Theme.BorderWidth);
            g.DrawPath(border, cardPath);
        }

        var imageRect = Rectangle.Round(m.Image);
        var artwork = GetArtwork(imageRect.Size, scale);
        var previousInterpolation = g.InterpolationMode;
        var previousOffset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(artwork, imageRect);
        g.InterpolationMode = previousInterpolation;
        g.PixelOffsetMode = previousOffset;

        if (_hover) DrawHoverOverlay(g, m.Image, scale);

        if (_hover) DrawHeart(g, m.Heart, scale);

        Theme.DrawText(g, Game.CardTitle, TitleFont, Theme.Text, m.Title);
        if (Game.Metadata is not null)
            Theme.DrawText(g, Game.MetadataLine, MetaFont, Theme.TextDim, m.Meta);

        DrawStars(g, m.Stars, scale);
        DrawPlaytime(g, m.Playtime);

        if (Game.IsRunning) DrawRunning(g, m.Running, scale);
    }

    private Bitmap GetArtwork(Size size, float scale)
    {
        size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
        bool square = SquareCards;
        // Square cards show the regular thumbnail whole until the full picture loads, or when there is none.
        var thumbnail = square ? Game.FullThumbnail ?? Game.Thumbnail : Game.Thumbnail;
        string? initial = thumbnail is null
            ? (string.IsNullOrWhiteSpace(Game.CardTitle) ? "?" : Game.CardTitle[..1].ToUpperInvariant())
            : null;

        if (_artwork is not null && ReferenceEquals(_artworkSource, thumbnail) &&
            _artworkSize == size && _artworkInitial == initial && _artworkSquare == square)
            return _artwork;

        ReleaseArtwork();

        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            var rect = new RectangleF(0, 0, size.Width, size.Height);
            using var imagePath = Theme.Rounded(rect, Theme.ImageRadius * scale);
            g.SetClip(imagePath);

            if (thumbnail is not null)
            {
                if (square)
                {
                    using (var backdrop = new LinearGradientBrush(rect, Theme.PlaceholderTop, Theme.PlaceholderBottom, 90f))
                        g.FillRectangle(backdrop, rect);
                    Theme.DrawContain(g, thumbnail, rect);
                }
                else
                {
                    Theme.DrawCover(g, thumbnail, rect);
                }
            }
            else
            {
                using (var gradient = new LinearGradientBrush(rect,
                           Theme.PlaceholderTop, Theme.PlaceholderBottom, 45f))
                {
                    g.FillRectangle(gradient, rect);
                }
                Theme.DrawText(g, initial!, InitialFont, Color.FromArgb(150, Theme.Gold), rect);
            }

            float overlayHeight = Math.Min(rect.Height * 0.55f, 70f * scale);
            var overlayRect = new RectangleF(rect.Left, rect.Bottom - overlayHeight, rect.Width, overlayHeight);
            using var shade = new LinearGradientBrush(overlayRect,
                Color.FromArgb(0, 8, 8, 12), Color.FromArgb(135, 8, 8, 12), LinearGradientMode.Vertical);
            g.FillRectangle(shade, overlayRect);
        }

        _artwork = bitmap;
        _artworkSource = thumbnail;
        _artworkSize = size;
        _artworkInitial = initial;
        _artworkSquare = square;
        return bitmap;
    }

    /// <summary>Drops the cached artwork so the next paint redraws it (e.g. after a style change).</summary>
    public void ResetArtwork() => ReleaseArtwork();

    private void ReleaseArtwork()
    {
        _artwork?.Dispose();
        _artwork = null;
        _artworkSource = null;
    }

    private void DrawHoverOverlay(Graphics g, RectangleF rect, float scale)
    {
        using (var veilPath = Theme.Rounded(rect, Theme.ImageRadius * scale))
        using (var veil = new SolidBrush(Color.FromArgb(72, 8, 8, 12)))
            g.FillPath(veil, veilPath);

        float radius = 26f * scale;
        var circle = new RectangleF(rect.Left + rect.Width / 2f - radius,
            rect.Top + rect.Height / 2f - radius, radius * 2f, radius * 2f);
        using (var brush = new SolidBrush(Color.FromArgb(238, Theme.Gold)))
            g.FillEllipse(brush, circle);
        using (var brush = new SolidBrush(Color.FromArgb(24, 20, 12)))
        {
            float t = 10.5f * scale;
            var points = new[]
            {
                new PointF(circle.Left + circle.Width / 2f - t * 0.55f, circle.Top + circle.Height / 2f - t),
                new PointF(circle.Left + circle.Width / 2f - t * 0.55f, circle.Top + circle.Height / 2f + t),
                new PointF(circle.Left + circle.Width / 2f + t * 0.85f, circle.Top + circle.Height / 2f)
            };
            g.FillPolygon(brush, points);
        }
    }

    private void DrawRunning(Graphics g, RectangleF badge, float scale)
    {
        var text = "RUNNING";
        var size = g.MeasureString(text, RunningFont);
        var rect = new RectangleF(badge.Left, badge.Top, size.Width + 26f * scale, badge.Height);
        using (var path = Theme.Rounded(rect, rect.Height / 2f))
        using (var brush = new SolidBrush(Color.FromArgb(190, 9, 13, 11)))
        {
            g.FillPath(brush, path);
            using var border = new Pen(Color.FromArgb(90, Theme.Success), 1f);
            g.DrawPath(border, path);
        }

        float dot = 5f * scale;
        using (var brush = new SolidBrush(Theme.Success))
            g.FillEllipse(brush, rect.Left + 8f * scale, rect.Top + (rect.Height - dot) / 2f, dot, dot);
        Theme.DrawText(g, text, RunningFont, Theme.Success,
            new RectangleF(rect.Left + 17f * scale, rect.Top, rect.Width - 22f * scale, rect.Height));
    }

    private void DrawHeart(Graphics g, RectangleF circleRect, float scale)
    {
        bool favorite = Game.Favorite;
        if (favorite || _hoverHeart)
        {
            using var brush = new SolidBrush(Color.FromArgb(favorite ? 210 : 150, 8, 8, 12));
            g.FillEllipse(brush, circleRect);
        }

        float size = 13f * scale;
        var heartRect = new RectangleF(circleRect.Left + (circleRect.Width - size) / 2f,
            circleRect.Top + (circleRect.Height - size) / 2f, size, size);
        using var heart = Theme.Heart(heartRect);
        if (favorite)
        {
            using var brush = new SolidBrush(Theme.Gold);
            g.FillPath(brush, heart);
        }
        else
        {
            using var pen = new Pen(Color.FromArgb(_hoverHeart ? 250 : 205, 255, 255, 255),
                _hoverHeart ? 2f : 1.5f) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, heart);
        }
    }

    private void DrawStars(Graphics g, RectangleF starsRect, float scale)
    {
        float size = starsRect.Height;
        for (int i = 0; i < 5; i++)
        {
            var r = new RectangleF(starsRect.Left + i * (size + Math.Max(2f, 3f * scale)), starsRect.Top, size, size);
            using var path = Theme.Star(r);
            bool filled = i < Game.Rating;
            bool hover = _hoverStar >= i;
            Color color = filled
                ? (hover ? Theme.StarFillBright : Theme.StarFill)
                : (hover ? Color.FromArgb(120, Theme.StarFill) : Theme.StarEmpty);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
    }

    private void DrawPlaytime(Graphics g, RectangleF rect)
    {
        string text;
        if (Game.PlaySeconds > 0)
        {
            text = Theme.FormatDuration(Game.PlaySeconds);
        }
        else if (Game.LastPlayedUtc is not null)
        {
            text = "played " + Theme.LastPlayedText(Game.LastPlayedUtc);
        }
        else
        {
            return; // an empty corner reads better than "not played" on every card
        }

        Theme.DrawText(g, text, MetaFont, Game.PlaySeconds > 0 ? Theme.TextMuted : Theme.TextDim, rect,
            StringAlignment.Far);
    }

    public void Dispose() => ReleaseArtwork();
}
