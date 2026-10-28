using System.Diagnostics;
using System.Reflection;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace RpgLauncher;

internal sealed class MainWindow : Window
{
    private readonly string _root;
    private readonly string _libraryPath;
    private readonly LauncherStore _store;
    private readonly HeaderPanel _header;
    private readonly GameGrid _grid;
    private readonly StatusBar _status;
    private readonly SearchBox _search;
    private readonly PillButton _sortButton;
    private readonly PillButton _tagsButton;
    private readonly PillButton _favoritesButton;
    private readonly PillButton _rescanButton;
    private readonly PillButton _moreButton;
    private readonly ViewToggle _viewToggle;
    private readonly ReleasesView _releases;
    private readonly ReleaseStore _releaseStore;
    private readonly ManualPanel _body = new();
    private readonly string? _screenshot;

    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(5200) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Dictionary<string, int> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Process> _processes = new();

    /// <summary>MV/MZ games started with Play with Cheats, by game key, while they run.</summary>
    private readonly Dictionary<string, CheatLaunch> _cheatLaunches = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CheatLaunch
    {
        public CheatLaunch(int port, string secret, string pluginsFile)
        {
            Port = port;
            Secret = secret;
            PluginsFile = pluginsFile;
        }

        public int Port { get; }
        public string Secret { get; }
        public string PluginsFile { get; }
        public CheatWindow? Window { get; set; }
        /// <summary>The game's process: the session ends when it exits, not when another copy of the game does.</summary>
        public Process? Process { get; set; }
    }

    private readonly SemaphoreSlim _metadataGate = new(1, 1);

    private List<GameEntry> _games = new();
    private bool _closed;
    // The window's place and outer size while not maximized, saved on close.
    private PixelPoint _normalPosition;
    private PixelSize _normalSize;
    private TagFilterPopup? _tagPicker;
    private Task? _storeImageTask;
    private bool _storeImagesAgain;
    private CancellationTokenSource? _loadCts;
    private Task? _metadataTask;
    private bool _loading;
    // Folder scan (F5 / Rescan) in progress, and games found so far.
    private bool _scanning;
    private int _scanFound;
    private int _visibleCount;
    private int _page;
    private int _pageCount = 1;
    private readonly TagFilter _tagFilter = new();
    private TagFilterPopup? _tagPopup;
    private DateTime? _tagPopupClosedUtc;

    // New-releases view state
    private bool _showingReleases;
    private bool _releasesLoaded;
    private bool _releasesFetching;
    private readonly TagFilter _releaseTagFilter = new();
    private int _releaseProgress;
    private int _releaseTotal;
    private string? _releaseError;
    private CancellationTokenSource? _releaseCts;
    private HashSet<string> _ownedProductIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lets screenshot mode wait for the metadata pass.</summary>
    public bool MetadataLookupsRunning => _metadataTask is { IsCompleted: false };

    public MainWindow(string root, string? screenshot = null)
    {
        _root = root;
        _screenshot = screenshot;
        _store = new LauncherStore(Path.Combine(root, "launcher-data.json"));
        _store.Load();
        // Apply the style before any control picks up its colors.
        Look.Apply(_store.Data.Style);
        GameCard.SquareCards = _store.Data.CardShape == CardShape.Square;
        GameEntry.StoreImagesFirst = _store.Data.CardPicture == CardPicture.StoreImage;
        GameCard.GroupNames = _store.Data.Groups;
        Shell.WineCommand = _store.Data.WineCommand ?? "wine";
        _libraryPath = Path.Combine(root, "launcher-library.json");
        GameImageLoader.CacheDirectory = Path.Combine(root, "launcher-thumbnails");
        _releaseStore = new ReleaseStore(Path.Combine(root, "launcher-dlsite"));
        string? proxyWarning = ApplyDlsiteProxy();

        Title = "RPG Launcher";
        Icon = App.WindowIcon;
        Background = Look.Brush(Look.Bg);
        Foreground = Look.Brush(Look.Text);
        var font = Look.Font(9);
        FontFamily = font.Family;
        FontSize = font.Size;
        MinWidth = 1140;
        MinHeight = 620;

        _header = new HeaderPanel { Mascot = Look.Style == UiStyle.Blob ? Mascots.Header : null };
        _grid = new GameGrid();
        _status = new StatusBar();

        _search = new SearchBox();
        _sortButton = new PillButton { DrawChevron = true };
        _tagsButton = new PillButton { Text = "Tags", DrawChevron = true };
        _favoritesButton = new PillButton { Text = "Favorites", ShowHeart = true };
        _rescanButton = new PillButton { Icon = PillIcon.Refresh };
        _moreButton = new PillButton { Icon = PillIcon.More };
        _viewToggle = new ViewToggle("Library", "New on DLsite");
        _releases = new ReleasesView(_releaseStore.CoverDirectory)
        {
            IsVisible = false,
            IsOwned = id => _ownedProductIds.Contains(id),
            Tags = _releaseTagFilter
        };

        _header.Children.AddRange(new Control[]
        {
            _search, _sortButton, _tagsButton, _favoritesButton, _rescanButton, _moreButton, _viewToggle
        });
        _header.Layout = LayoutHeader;

        // The header across the top, the status bar along the bottom, the library or new releases between.
        _body.Children.AddRange(new Control[] { _grid, _releases, _status, _header });
        _body.Layout = size =>
        {
            const double headerHeight = 96, statusHeight = 32;
            double middle = Math.Max(0, size.Height - headerHeight - statusHeight);
            _body.Place(_header, 0, 0, size.Width, headerHeight);
            _body.Place(_grid, 0, headerHeight, size.Width, middle);
            _body.Place(_releases, 0, headerHeight, size.Width, middle);
            _body.Place(_status, 0, size.Height - statusHeight, size.Width, statusHeight);
        };
        Content = _body;

        Ui.Tip(_search, "Search by title or folder name  (Ctrl+F)");
        Ui.Tip(_tagsButton, "Filter games by a store tag");
        Ui.Tip(_favoritesButton, "Show only your favorite games");
        Ui.Tip(_rescanButton, "Rescan this folder for games  (F5)");
        Ui.Tip(_moreButton, "More options");
        Ui.Tip(_viewToggle, "Switch between your library and new game releases on DLsite");

        _favoritesButton.Checked = _store.Data.FavoritesOnly;
        UpdateSortButton();

        // Filter once typing pauses instead of on every keystroke.
        _search.TextValueChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            if (_showingReleases)
            {
                _releases.Filter = _search.TextValue;
                ResetStatus();
            }
            else
            {
                RefreshView(resetScroll: true);
            }
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _ = FlushSaveAsync();
        };
        _sortButton.Click += (_, _) => ShowSortMenu();
        _tagsButton.Click += (_, _) => ShowTagFilter();
        _favoritesButton.Click += (_, _) => ToggleFavoritesFilter();
        _rescanButton.Click += (_, _) => RefreshCurrentView();
        _moreButton.Click += (_, _) => ShowMoreMenu();

        _grid.PlayRequested += PlayGame;
        _grid.CheatsRequested += PlayWithCheats;
        _grid.FavoriteToggled += ToggleFavorite;
        _grid.RatingChanged += ChangeRating;
        _grid.GroupToggled += ToggleGroup;
        _grid.RenameRequested += RenameGame;
        _grid.OpenFolderRequested += game => OpenFolder(game.RootPath);
        _grid.InfoRequested += ShowGameInfo;
        _grid.MetadataRequested += game => _ = LookupMetadataManuallyAsync(game);
        _grid.StorePageRequested += game => _ = SetStorePageAsync(game);
        _grid.TagsRequested += ShowTagPicker;
        _grid.ImageActionRequested += OnImageAction;
        _status.PageRequested += GoToPage;
        _status.PageMenuRequested += ShowPageMenu;
        _viewToggle.SelectedChanged += index => SwitchView(index == 1);
        _releases.InfoRequested += ShowReleaseInfo;

        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            ResetStatus();
        };

        if (proxyWarning is not null) _store.LoadWarning ??= proxyWarning;

        // Shortcuts work wherever the focus is, as with the WinForms KeyPreview.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        PositionChanged += (_, _) => RememberNormalBounds();
        SizeChanged += (_, _) => RememberNormalBounds();

        ApplyInitialBounds();
        UpdateHeaderSubtitle();
        ResetStatus();
        if (_store.LoadWarning is not null)
        {
            SetStatus(_store.LoadWarning);
            _store.LoadWarning = null;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NativeMethods.StyleTitleBar(this);
        RememberNormalBounds();
        Look.PixelScale = RenderScaling;
        ScalingChanged += (_, _) => Look.PixelScale = RenderScaling;
        _ = LoadLibraryAsync();
        if (_screenshot is not null) _ = CaptureScreenshotAsync(_screenshot);
    }

    /// <summary>Development aid (--screenshot path): saves the window once the library has loaded, then closes.</summary>
    private async Task CaptureScreenshotAsync(string path)
    {
        await Task.Delay(2600);
        // Give metadata lookups a chance to finish so the capture shows them.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (MetadataLookupsRunning && DateTime.UtcNow < deadline)
            await Task.Delay(150);
        await Task.Delay(700);
        try
        {
            double scale = RenderScaling;
            var size = new PixelSize((int)Math.Round(Bounds.Width * scale), (int)Math.Round(Bounds.Height * scale));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(this);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bitmap.Save(path);
        }
        catch
        {
            // screenshot mode is a development aid only
        }
        Close();
    }

    private static readonly PixelSize LargeScreen = new(2560, 1440);
    private static readonly PixelSize LargeScreenWindow = new(1920, 1080);

    /// <summary>The frame around the window's inside on Windows, to turn saved outer sizes into inner ones.</summary>
    private static Size FrameAllowance => OperatingSystem.IsWindows() ? new Size(16, 39) : default;

    /// <summary>Centred LargeScreenWindow bounds when the screen is at least LargeScreen; otherwise null.</summary>
    internal static PixelRect? LargeScreenBounds(PixelRect screen, PixelRect working)
    {
        if (screen.Width < LargeScreen.Width || screen.Height < LargeScreen.Height) return null;
        int width = Math.Min(LargeScreenWindow.Width, working.Width);
        int height = Math.Min(LargeScreenWindow.Height, working.Height);
        return new PixelRect(working.X + (working.Width - width) / 2, working.Y + (working.Height - height) / 2,
            width, height);
    }

    /// <summary>Restores the saved place and outer size (screen pixels, as earlier versions saved them), or a default.</summary>
    private void ApplyInitialBounds()
    {
        var saved = _store.Data.Window;

        // Large screens ignore the saved size; the saved screen and maximized state still apply.
        var savedRect = new PixelRect(saved.X, saved.Y, saved.Width, saved.Height);
        bool savedUsable = saved.Valid && saved.Width > 500 && saved.Height > 400;
        var savedScreen = savedUsable ? Screens.All.FirstOrDefault(s => s.WorkingArea.Intersects(savedRect)) : null;
        var screen = savedScreen ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        double scale = screen.Scaling;
        var working = screen.WorkingArea;

        void UseOuterBounds(PixelRect rect)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = rect.Position;
            Width = Math.Max(MinWidth, rect.Width / scale - FrameAllowance.Width);
            Height = Math.Max(MinHeight, rect.Height / scale - FrameAllowance.Height);
        }

        if (LargeScreenBounds(screen.Bounds, working) is { } large)
        {
            UseOuterBounds(large);
        }
        else if (savedScreen is not null)
        {
            UseOuterBounds(savedRect);
        }
        else
        {
            Width = Math.Min(1280, working.Width / scale - 60);
            Height = Math.Min(820, working.Height / scale - 60);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (saved.Maximized) WindowState = Avalonia.Controls.WindowState.Maximized;
    }

    private void RememberNormalBounds()
    {
        if (WindowState != Avalonia.Controls.WindowState.Normal || !IsVisible) return;
        var outer = FrameSize ?? Bounds.Size + FrameAllowance;
        _normalPosition = Position;
        _normalSize = new PixelSize((int)Math.Round(outer.Width * RenderScaling), (int)Math.Round(outer.Height * RenderScaling));
    }

    private void LayoutHeader(Size size)
    {
        if (size.Width <= 0) return;
        const double height = 38, gap = 10;
        double right = size.Width - 28;
        double y = Math.Round((size.Height - height) / 2) + 3;

        var logo = _header.LogoBounds();
        const double toggleHeight = 30;
        double toggleWidth = _viewToggle.PreferredWidth();
        _header.Place(_viewToggle, logo.Right + 18, logo.Top + (logo.Height - toggleHeight) / 2, toggleWidth, toggleHeight);
        double leftLimit = logo.Right + 18 + toggleWidth + 20;

        void PlaceRight(Control control, double width)
        {
            _header.Place(control, right - width, y, width, height);
            right -= width + gap;
        }

        PlaceRight(_moreButton, 38);
        PlaceRight(_rescanButton, 38);
        if (!_showingReleases) PlaceRight(_favoritesButton, 126);
        PlaceRight(_tagsButton, 112);
        if (!_showingReleases) PlaceRight(_sortButton, 176);

        double searchWidth = 230;
        if (size.Width > 1320) searchWidth += Math.Min(110, (size.Width - 1320) * 0.22);
        // Give way to the view switch on narrow windows.
        searchWidth = Math.Max(140, Math.Min(searchWidth, right - leftLimit));
        _header.Place(_search, right - searchWidth, y, searchWidth, height);
    }

    private void RefreshCurrentView()
    {
        if (_showingReleases) _ = FetchReleasesAsync(refresh: true);
        else _ = RefreshGamesAsync();
    }

    /// <summary>Swaps between the library grid and the DLsite new-releases view.</summary>
    private void SwitchView(bool releases)
    {
        if (releases == _showingReleases) return;
        _showingReleases = releases;

        _sortButton.IsVisible = !releases;
        _favoritesButton.IsVisible = !releases;
        // Each view keeps its own tag filter.
        UpdateTagsButton();
        Ui.Tip(_tagsButton, releases ? "Filter new releases by a store tag" : "Filter games by a store tag");
        _grid.IsVisible = !releases;
        _releases.IsVisible = releases;
        Ui.Tip(_rescanButton, releases
            ? "Check DLsite for new releases  (F5)"
            : "Rescan this folder for games  (F5)");
        Ui.Tip(_search, releases
            ? "Search new releases by title, circle or tag  (Ctrl+F)"
            : "Search by title or folder name  (Ctrl+F)");
        _header.InvalidateMeasure();

        if (releases)
        {
            _status.SetPages(0, 1);
            _releases.Language = _store.Data.MetadataLanguage;
            _releases.Filter = _search.TextValue;
            if (!_releasesLoaded)
            {
                _releasesLoaded = true;
                _releaseStore.Load();
            }
            ShowReleaseDays();
            _ = FetchReleasesAsync(refresh: false);
        }
        else
        {
            // The search may have changed in the other view.
            RefreshView(resetScroll: false);
        }

        UpdateHeaderSubtitle();
        if (!_statusTimer.IsEnabled) ResetStatus();
    }

    private void ShowReleaseDays()
    {
        _ownedProductIds = _games
            .Select(game => game.Metadata?.ProductId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _releases.EmptyTitle = _releasesFetching
            ? "Checking DLsite for new games\u2026"
            : _releaseError is not null
                ? "Couldn't reach DLsite"
                : _search.TextValue.Trim().Length > 0 || !_releaseTagFilter.IsEmpty
                    ? "No new releases match your search or tag"
                    : "No new releases found";
        _releases.EmptyText = _releaseError is not null && !_releasesFetching
            ? "Check your connection, then press refresh (F5) to try again."
            : "";
        _releases.Loading = _releasesFetching;
        _releases.SetDays(_releaseStore.Days());
        if (_showingReleases) UpdateHeaderSubtitle();
    }

    /// <summary>Downloads missing or unfinished release days, one at a time with a short pause.</summary>
    private async Task FetchReleasesAsync(bool refresh)
    {
        if (_releasesFetching) return;
        bool english = _store.Data.MetadataLanguage == MetadataLanguage.English;
        var dates = _releaseStore.DaysToFetch(refresh, english);
        if (dates.Count == 0)
        {
            if (refresh) SetStatus("New releases are already up to date");
            return;
        }

        _releasesFetching = true;
        _releaseError = null;
        _releaseTotal = dates.Count;
        _releaseProgress = 0;
        // A cancelled run may still be unwinding; make sure it stops before this one starts.
        _releaseCts?.Cancel();
        _releaseCts?.Dispose();
        var cts = _releaseCts = new CancellationTokenSource();
        var token = cts.Token;
        ShowReleaseDays();

        int consecutiveFailures = 0;
        try
        {
            foreach (var (date, englishOnly) in dates)
            {
                if (!_statusTimer.IsEnabled) ResetStatus();
                try
                {
                    ReleaseDay day;
                    if (englishOnly && _releaseStore.Day(date) is { } cached)
                        day = await ReleaseStore.FetchEnglishAsync(cached, token);
                    else
                        day = await ReleaseStore.FetchDayAsync(date, english, token);
                    if (_closed) return;
                    // A cancelled run (e.g. the connection changed) must not write into the new cache.
                    token.ThrowIfCancellationRequested();
                    _releaseStore.Put(day);
                    consecutiveFailures = 0;
                    ShowReleaseDays();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _releaseError = ex.Message;
                    Diagnostics.Log($"Release day {date:yyyy-MM-dd} failed: {ex.Message}");
                    // Offline or blocked: stop instead of waiting on every remaining day.
                    if (++consecutiveFailures >= 2) break;
                }

                _releaseProgress++;
                if (_releaseProgress < dates.Count) await Task.Delay(700, token);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer run may have started since; it owns the flag.
            if (_releaseCts == cts) _releasesFetching = false;
            return;
        }

        if (_releaseCts != cts) return;
        _releasesFetching = false;
        if (_closed) return;
        await Task.Run(() =>
        {
            _releaseStore.Prune();
            _releaseStore.Save();
        });
        if (_closed) return;

        ShowReleaseDays();
        if (_releaseError is not null && _releases.TotalCount > 0)
            SetStatus("Couldn't load every day from DLsite \u00b7 showing what is cached \u00b7 F5 to try again");
        else if (!_statusTimer.IsEnabled)
            ResetStatus();
    }

    private string ReleaseStatus()
    {
        if (_releasesFetching)
            return $"Checking DLsite for new games \u00b7 day {Math.Min(_releaseProgress + 1, _releaseTotal)} of {_releaseTotal}\u2026";

        int total = _releases.TotalCount;
        if (total == 0)
            return _releaseError is not null ? "Couldn't reach DLsite \u00b7 " + _releaseError : "No new releases loaded";

        var days = _releaseStore.Days();
        var lastChecked = days.Count == 0 ? (DateTime?)null : days.Max(d => d.FetchedUtc).ToLocalTime();
        string checkedText = lastChecked is { } at
            ? (at.Date == DateTime.Today ? $"checked today at {at:HH:mm}" : $"checked {at:MMM d}")
            : "";

        int shown = _releases.ShownCount;
        string count = !_releaseTagFilter.IsEmpty
            ? $"{shown} of {total} new games {_releaseTagFilter.Describe()}"
            : shown == total
                ? $"{total} new {(total == 1 ? "game" : "games")} in the last {ReleaseStore.DaysBack} days"
                : $"{shown} of {total} new games match";
        return checkedText.Length > 0 ? $"{count} \u00b7 {checkedText}" : count;
    }

    /// <summary>Shows the cached library from the last scan; scans only when there is no usable cache.</summary>
    private async Task LoadLibraryAsync()
    {
        if (_loading) return;
        _loading = true;

        var extraDirectories = ExtraGameDirectories();
        List<GameEntry>? cached = null;
        try
        {
            cached = await Task.Run(() => LibraryCache.Load(_libraryPath, _root, extraDirectories));
        }
        catch
        {
            // fall back to a full scan
        }

        _loading = false;
        if (_closed) return;

        if (cached is null || cached.Count == 0)
        {
            await RefreshGamesAsync();
            return;
        }

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        ApplyLibrary(cached, cts);
        SetStatus($"Loaded {cached.Count} {(cached.Count == 1 ? "game" : "games")} \u00b7 Press F5 to look for new games");
    }

    private async Task RefreshGamesAsync()
    {
        if (_loading)
        {
            if (_scanning) SetStatus("Already scanning for games \u00b7 the library updates when it's done");
            return;
        }
        _loading = true;
        _scanning = true;
        _scanFound = 0;
        SetStatus(ScanStatus(), transient: false);

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        var progress = new Progress<int>(found =>
        {
            _scanFound = found;
            if (_scanning && !_statusTimer.IsEnabled) ResetStatus();
        });

        // Last scan's games by folder, so the scanner can reuse unchanged ones.
        var known = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in _games)
        {
            if (NormalizeDirectory(game.RootPath) is { } folder) known.TryAdd(folder, game);
        }
        GameEntry? Known(string dir) =>
            NormalizeDirectory(dir) is { } folder && known.TryGetValue(folder, out var game) ? game : null;

        List<GameEntry> scanned;
        try
        {
            var extraDirectories = ExtraGameDirectories();
            scanned = await Task.Run(
                () => ScanLibrary(_root, extraDirectories, cts.Token, progress, Known), cts.Token);
        }
        catch (OperationCanceledException)
        {
            _loading = _scanning = false;
            return;
        }
        catch (Exception ex)
        {
            _loading = _scanning = false;
            SetStatus("Scan failed: " + ex.Message);
            return;
        }

        _scanning = false;
        if (cts.IsCancellationRequested || _closed)
        {
            _loading = false;
            return;
        }

        try
        {
            ApplyLibrary(scanned, cts);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Showing the scanned library failed: " + ex);
            SetStatus("Scan failed: " + ex.Message);
            return;
        }
        finally
        {
            _loading = false;
        }

        // Cache this scan for the next launch and prune stale thumbnails.
        var cacheDirectories = ExtraGameDirectories();
        string libraryJson = LibraryCache.Serialize(_root, cacheDirectories, _games);
        var snapshot = _games.ToList();
        string libraryPath = _libraryPath;
        _ = Task.Run(() =>
        {
            LibraryCache.Write(libraryPath, libraryJson);
            GameImageLoader.PruneCache(snapshot);
        });
    }

    /// <summary>Shows a scanned or cached game list and starts thumbnails and metadata.</summary>
    private void ApplyLibrary(List<GameEntry> scanned, CancellationTokenSource cts)
    {
        var previous = _games;
        _games = scanned;
        ReuseThumbnails(previous, _games);
        MergeRecords(_games);
        TagNames.Learn(_games.Select(game => game.Metadata));
        _grid.SyncGames(_games);
        foreach (var old in previous)
        {
            old.Thumbnail?.Dispose();
            old.FullThumbnail?.Dispose();
        }
        foreach (var game in _games)
        {
            if (_running.TryGetValue(game.Key, out int running)) game.RunningCount = running;
            game.CheatsOn = _cheatLaunches.ContainsKey(game.Key);
        }

        bool tagCleared = ValidateTagFilter();
        _visibleCount = _games.Count;
        RefreshView(resetScroll: true);
        UpdateHeaderSubtitle();
        SaveData();

        _loading = false;
        ResetStatus();
        if (tagCleared) SetStatus("Removed tags from the filter that no game has any more");
        QueueThumbnails(cts.Token);
        if (_store.Data.CardShape == CardShape.Square) QueueFullThumbnails(cts.Token);
        QueueMetadata(cts.Token);
        QueueStoreImages(cts.Token);
    }

    /// <summary>
    /// Scans the launcher folder first, so games under it keep their relative keys, then added folders
    /// (keyed by full path). A game reachable from several roots is included once.
    /// </summary>
    private static List<GameEntry> ScanLibrary(
        string primaryRoot, IReadOnlyList<string> extraRoots, CancellationToken token, IProgress<int>? progress = null,
        Func<string, GameEntry?>? known = null)
    {
        var found = new List<GameEntry>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        void Found() => progress?.Report(++count);

        void AddGames(List<GameEntry> games, bool primary)
        {
            foreach (var game in games)
            {
                token.ThrowIfCancellationRequested();

                string rootPath;
                try { rootPath = Path.GetFullPath(game.RootPath); }
                catch { rootPath = game.RootPath; }

                if (!seenRoots.Add(rootPath)) continue;
                if (!primary) game.Key = rootPath;
                found.Add(game);
            }
        }

        AddGames(GameScanner.Scan(primaryRoot, Found, known), primary: true);

        foreach (string root in extraRoots)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                AddGames(GameScanner.Scan(root, Found, known), primary: false);
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"Scan skipped {root}: {ex.Message}");
            }
        }

        found.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    /// <summary>Returns added folders, normalized and without duplicates.</summary>
    private List<string> ExtraGameDirectories()
    {
        var directories = new List<string>();
        foreach (string stored in _store.Data.GameDirectories)
        {
            string? directory = NormalizeDirectory(stored);
            if (directory is null || IsSameDirectory(directory, _root)) continue;
            if (directories.Any(existing => IsSameDirectory(existing, directory))) continue;
            directories.Add(directory);
        }
        return directories;
    }

    private bool ContainsLibraryDirectory(string path) =>
        IsSameDirectory(path, _root) ||
        _store.Data.GameDirectories.Any(stored => IsSameDirectory(stored, path));

    /// <summary>Adds and saves a folder; returns false when it is invalid or already known.</summary>
    private bool AddGameDirectory(string path)
    {
        string? directory = NormalizeDirectory(path);
        if (directory is null || ContainsLibraryDirectory(directory)) return false;

        _store.Data.GameDirectories.Add(directory);
        SaveData();
        return true;
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSameDirectory(string? left, string? right)
    {
        if (left is null || right is null) return false;
        string? a = NormalizeDirectory(left);
        string? b = NormalizeDirectory(right);
        return a is not null && b is not null &&
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private void MergeRecords(List<GameEntry> games)
    {
        foreach (var game in games)
        {
            if (!_store.Data.Games.TryGetValue(game.Key, out var record))
            {
                record = new GameRecord { AddedUtc = DateTime.UtcNow };
                _store.Data.Games[game.Key] = record;
            }

            if (record.AddedUtc == default) record.AddedUtc = DateTime.UtcNow;
            game.AddedUtc = record.AddedUtc;
            game.CustomTitle = record.CustomTitle;
            game.Rating = Math.Clamp(record.Rating, 0, 5);
            game.Favorite = record.Favorite;
            game.PlayCount = record.PlayCount;
            game.PlaySeconds = record.PlaySeconds;
            game.LastPlayedUtc = record.LastPlayedUtc;
            game.Metadata = record.Metadata;
            game.MetadataChecked = record.MetadataChecked;
            // Games not found by an older search revision get one more try.
            if (record.Metadata is null && record.MetadataChecked && record.LookupRevision < MetadataService.LookupRevision)
                game.MetadataChecked = false;
            game.MetadataEnglishChecked = record.MetadataEnglishChecked;
            game.MetadataLanguage = _store.Data.MetadataLanguage;
            game.CustomImagePath = ResolveCustomImage(record.CustomImage);
            game.Framing = record.Framing;
            game.StoreImageChoice = record.UseStoreImage;
            game.StoreImagePath = ExistingStoreImage(game);
            game.Groups = record.Groups?.ToList() ?? new();
            game.AddedTags = record.AddedTags?.ToList() ?? new();
        }
    }

    /// <summary>Carries thumbnails over to rescanned entries whose images are unchanged.</summary>
    private static void ReuseThumbnails(List<GameEntry> previous, List<GameEntry> scanned)
    {
        var byKey = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var old in previous)
        {
            if (old.Thumbnail is not null) byKey[old.Key] = old;
        }
        if (byKey.Count == 0) return;

        foreach (var game in scanned)
        {
            if (!byKey.TryGetValue(game.Key, out var old)) continue;
            if (!string.Equals(old.Title1Path, game.Title1Path, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(old.Title2Path, game.Title2Path, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(old.IconPath, game.IconPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(old.EncryptionKey, game.EncryptionKey, StringComparison.OrdinalIgnoreCase))
                continue;

            game.Thumbnail = old.Thumbnail;
            game.ThumbnailRequested = true;
            old.Thumbnail = null;
            game.FullThumbnail = old.FullThumbnail;
            game.FullThumbnailRequested = old.FullThumbnail is not null;
            old.FullThumbnail = null;
        }
    }

    private void QueueThumbnails(CancellationToken token)
    {
        foreach (var game in _games)
        {
            if (game.Thumbnail is not null || game.ThumbnailRequested) continue;
            game.ThumbnailRequested = true;
            _ = LoadThumbnailAsync(game, token);
        }
    }

    private async Task LoadThumbnailAsync(GameEntry game, CancellationToken token)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await GameImageLoader.CreateThumbnailAsync(game, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            return;
        }

        if (token.IsCancellationRequested || _closed)
        {
            bitmap?.Dispose();
            return;
        }

        game.Thumbnail = bitmap;
        if (_grid.GetCard(game.Key) is { } card && ReferenceEquals(card.Game, game))
            card.Invalidate();
    }

    /// <summary>
    /// Queues store lookups for unchecked games and for missing English text or titles. Results,
    /// including "no match", are saved so the store isn't asked again.
    /// </summary>
    private void QueueMetadata(CancellationToken token)
    {
        var pending = _games.Where(g => g.Metadata is null && !g.MetadataChecked).ToList();
        var translations = _games.Where(g =>
            g.Metadata is not null && !g.MetadataEnglishChecked && g.Metadata.English is null).ToList();

        // English text may already be on a record whose flag wasn't saved.
        int marked = 0;
        foreach (var game in _games.Where(g =>
                     g.Metadata?.English is not null && !g.MetadataEnglishChecked))
        {
            game.MetadataEnglishChecked = true;
            if (_store.Data.Games.TryGetValue(game.Key, out var record))
                record.MetadataEnglishChecked = true;
            marked++;
        }
        if (marked > 0) SaveData();

        var englishTitles = _games.Where(g =>
            g.Metadata is { EnglishTitleChecked: false, English: not null } metadata && metadata.NeedsEnglishTitle()).ToList();

        if (pending.Count == 0 && translations.Count == 0 && englishTitles.Count == 0) return;
        _metadataTask = MetadataQueueAsync(pending, translations, englishTitles, token);
    }

    private async Task MetadataQueueAsync(
        List<GameEntry> pending,
        List<GameEntry> translations,
        List<GameEntry> englishTitles,
        CancellationToken token)
    {
        int englishTitled = 0;
        int added = 0;
        int missed = 0;
        int failed = 0;
        int translated = 0;
        int translationMissed = 0;
        int translationFailed = 0;
        // Offline or blocked: stop after a few failures in a row instead of trying every game.
        const int MaxFailuresInARow = 3;
        int failuresInARow = 0;

        try
        {
            for (int i = 0; i < pending.Count && failuresInARow < MaxFailuresInARow; i++)
            {
                token.ThrowIfCancellationRequested();
                var game = pending[i];
                SetStatus($"Looking up store metadata {i + 1} of {pending.Count} \u00b7 {game.CardTitle}\u2026",
                    transient: false);

                var result = await LookupMetadataAsync(game, token);
                if (result is null || token.IsCancellationRequested) return;

                if (result.Metadata is { } metadata)
                {
                    SaveMetadata(game, metadata);
                    added++;
                    failuresInARow = 0;
                }
                else if (result.NotFound)
                {
                    MarkMetadataChecked(game);
                    missed++;
                    failuresInARow = 0;
                }
                else
                {
                    failed++;
                    failuresInARow++;
                }

                if (i < pending.Count - 1 || translations.Count > 0)
                    await Task.Delay(1000, token);
            }

            for (int i = 0; i < translations.Count && failuresInARow < MaxFailuresInARow; i++)
            {
                token.ThrowIfCancellationRequested();
                var game = translations[i];
                if (game.Metadata is null) continue;

                SetStatus($"Fetching English store text {i + 1} of {translations.Count} \u00b7 {game.CardTitle}\u2026",
                    transient: false);

                var result = await LookupEnglishAsync(game, token);
                if (result is null || token.IsCancellationRequested) return;

                if (result.Translation is { } translation)
                {
                    SaveEnglish(game, translation);
                    translated++;
                    failuresInARow = 0;
                }
                else if (result.NotFound)
                {
                    MarkMetadataEnglishChecked(game);
                    translationMissed++;
                    failuresInARow = 0;
                }
                else
                {
                    translationFailed++;
                    failuresInARow++;
                }

                if (i < translations.Count - 1 || englishTitles.Count > 0)
                    await Task.Delay(1000, token);
            }

            // One English-edition lookup per product; copies of a game share the result.
            var editionTitles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < englishTitles.Count && failuresInARow < MaxFailuresInARow; i++)
            {
                token.ThrowIfCancellationRequested();
                var game = englishTitles[i];
                if (game.Metadata is not { English: { } english } metadata) continue;

                if (!editionTitles.TryGetValue(metadata.ProductId, out var title))
                {
                    SetStatus($"Looking for English titles {i + 1} of {englishTitles.Count} \u00b7 {game.CardTitle}\u2026",
                        transient: false);
                    var result = await LookupEnglishTitleAsync(metadata, token);
                    if (i < englishTitles.Count - 1) await Task.Delay(1000, token);
                    if (result is null)
                    {
                        failuresInARow++;
                        continue; // tried again next launch
                    }
                    failuresInARow = 0;
                    title = result.Value.Title;
                    editionTitles[metadata.ProductId] = title;
                }

                if (title is not null)
                {
                    english.Title = title;
                    englishTitled++;
                }
                metadata.EnglishTitleChecked = true;
                if (_store.Data.Games.TryGetValue(game.Key, out var record)) record.Metadata = metadata;
                _grid.RefreshCard(game);
                SaveData();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_closed) return;

        bool changed = added > 0 || translated > 0 || englishTitled > 0;
        bool tagCleared = changed && ValidateTagFilter();
        if (changed) RefreshView();
        ResetStatus();

        var parts = new List<string>();
        if (added > 0) parts.Add($"store metadata for {added} game{(added == 1 ? "" : "s")}");
        if (translated > 0) parts.Add($"English text for {translated} game{(translated == 1 ? "" : "s")}");
        if (englishTitled > 0) parts.Add($"English titles for {englishTitled} game{(englishTitled == 1 ? "" : "s")}");

        if (parts.Count > 0)
        {
            string summary = "Updated " + string.Join(" and ", parts);
            if (missed > 0) summary += $" \u00b7 {missed} not found";
            if (failed > 0) summary += $" \u00b7 {failed} failed";
            if (translationFailed > 0) summary += $" \u00b7 English failed for {translationFailed}";
            if (tagCleared) summary += " \u00b7 tag filter cleared";
            SetStatus(summary);
        }
        else if (translationFailed > 0)
        {
            SetStatus($"English store lookup failed for {translationFailed} game{(translationFailed == 1 ? "" : "s")} \u00b7 will retry next launch");
        }
        else if (missed > 0)
        {
            SetStatus($"No DLsite match found for {missed} game{(missed == 1 ? "" : "s")}");
        }
        else if (failed > 0)
        {
            SetStatus($"Metadata lookup failed for {failed} game{(failed == 1 ? "" : "s")} \u00b7 will retry next launch");
        }
        else if (translationMissed > 0)
        {
            SetStatus($"No English store text available for {translationMissed} game{(translationMissed == 1 ? "" : "s")}");
        }
    }

    /// <summary>
    /// A DLsite code in the game folder's name, or its parent's (downloads unpack into RJ01234567, sometimes
    /// with the game one level down). Codes in readmes (asset credits) or nearby zips (other works) aren't trusted.
    /// </summary>
    private static string? StoreIdFromNames(GameEntry game) =>
        MetadataService.ProductIdFrom(game.FolderName) ??
        MetadataService.ProductIdFrom(Path.GetFileName(Path.GetDirectoryName(game.RootPath.TrimEnd('\\', '/')) ?? ""));

    private async Task<MetadataLookupResult?> LookupMetadataAsync(GameEntry game, CancellationToken token)
    {
        await _metadataGate.WaitAsync(token);
        try
        {
            // A DLsite code in the folder name identifies the page exactly.
            if (StoreIdFromNames(game) is { } id &&
                await MetadataService.LookupProductAsync(id, token) is { Metadata: not null } named)
                return named;
            return await MetadataService.LookupAsync(GameTitles.SearchQueries(game.Title, game.FolderName), token,
                strict: game.Family is EngineFamily.RenPy or EngineFamily.Unity);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            return MetadataLookupResult.Fail(ex.Message);
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    private async Task<MetadataLookupResult?> LookupProductAsync(string productId, CancellationToken token)
    {
        await _metadataGate.WaitAsync(token);
        try
        {
            return await MetadataService.LookupProductAsync(productId, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            return MetadataLookupResult.Fail(ex.Message);
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    /// <summary>The English edition's title (null if none); the result itself is null when the lookup fails.</summary>
    private async Task<(string? Title, bool Done)?> LookupEnglishTitleAsync(GameMetadata metadata, CancellationToken token)
    {
        await _metadataGate.WaitAsync(token);
        try
        {
            return (await MetadataService.EnglishEditionTitleAsync(metadata.Site, metadata.ProductId, null, token), true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"English title for {metadata.ProductId} failed: {ex.Message}");
            return null;
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    private async Task<MetadataLookupResult?> LookupEnglishAsync(GameEntry game, CancellationToken token)
    {
        if (game.Metadata is null) return null;

        await _metadataGate.WaitAsync(token);
        try
        {
            return await MetadataService.LookupEnglishAsync(game.Metadata, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            return MetadataLookupResult.Fail(ex.Message);
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    private void SaveMetadata(GameEntry game, GameMetadata metadata)
    {
        // Keep the old English text if the refresh came back without any.
        if (metadata.English is null && game.Metadata?.English is { } english)
            metadata.English = english;

        game.Metadata = metadata;
        game.MetadataChecked = true;
        game.MetadataEnglishChecked = metadata.English is not null;
        if (_store.Data.Games.TryGetValue(game.Key, out var record))
        {
            record.Metadata = metadata;
            record.MetadataChecked = true;
            record.MetadataEnglishChecked = game.MetadataEnglishChecked;
            record.LookupRevision = MetadataService.LookupRevision;
        }
        _grid.RefreshCard(game);
        SaveData();
        TagNames.Learn(_games.Select(g => g.Metadata));

        // A new store page brings its own picture.
        string? oldCache = GameImageLoader.CachePathFor(game);
        string? picture = ExistingStoreImage(game);
        if (!string.Equals(picture, game.StoreImagePath, StringComparison.OrdinalIgnoreCase))
        {
            game.StoreImagePath = picture;
            _ = ReloadThumbnailAsync(game, oldCache);
        }
        if (game.WantsStoreImage && game.StoreImagePath is null)
            QueueStoreImages(_loadCts?.Token ?? CancellationToken.None);
    }

    private void SaveEnglish(GameEntry game, GameMetadataText translation)
    {
        if (game.Metadata is null) return;
        game.Metadata.English = translation;
        game.MetadataEnglishChecked = true;
        if (_store.Data.Games.TryGetValue(game.Key, out var record))
        {
            record.Metadata = game.Metadata;
            record.MetadataEnglishChecked = true;
        }
        _grid.RefreshCard(game);
        SaveData();
    }

    private void MarkMetadataChecked(GameEntry game)
    {
        game.MetadataChecked = true;
        if (_store.Data.Games.TryGetValue(game.Key, out var record))
        {
            record.MetadataChecked = true;
            record.LookupRevision = MetadataService.LookupRevision;
        }
        SaveData();
    }

    private void MarkMetadataEnglishChecked(GameEntry game)
    {
        game.MetadataEnglishChecked = true;
        if (_store.Data.Games.TryGetValue(game.Key, out var record))
            record.MetadataEnglishChecked = true;
        SaveData();
    }

    /// <summary>Points a game at a pasted DLsite page, for games the search can't find.</summary>
    private async Task SetStorePageAsync(GameEntry game)
    {
        var dialog = new TextPromptDialog("Set DLsite page",
            "Paste the game's DLsite page link or its RJ code:", game.Metadata?.ProductId ?? "", "Use this");
        if (!await dialog.ShowAsync(this)) return;
        string? id = MetadataService.ProductIdFrom(dialog.Value);
        if (id is null)
        {
            SetStatus("That isn't a DLsite page or RJ code");
            return;
        }
        await LookupMetadataManuallyAsync(game, id);
    }

    private async Task LookupMetadataManuallyAsync(GameEntry game, string? productId = null)
    {
        // A game with a store page is refreshed from that page, so a page set by hand stays.
        productId ??= game.Metadata?.ProductId;
        SetStatus($"Looking up store metadata for {game.CardTitle}\u2026", transient: false);
        var result = string.IsNullOrWhiteSpace(productId)
            ? await LookupMetadataAsync(game, CancellationToken.None)
            : await LookupProductAsync(productId, CancellationToken.None);
        if (_closed || result is null) return;

        if (result.Metadata is { } metadata)
        {
            SaveMetadata(game, metadata);
            bool tagCleared = ValidateTagFilter();
            RefreshView();
            string maker = string.IsNullOrWhiteSpace(metadata.MakerFor(game.MetadataLanguage))
                ? ""
                : " \u00b7 " + metadata.MakerFor(game.MetadataLanguage);
            SetStatus($"Metadata added for {game.CardTitle}{maker}" +
                (tagCleared ? " \u00b7 tag filter cleared" : ""));
        }
        else if (result.NotFound)
        {
            if (game.Metadata is null) MarkMetadataChecked(game);
            SetStatus($"No DLsite match found for {game.CardTitle}");
        }
        else
        {
            SetStatus("Metadata lookup failed: " + (result.Error ?? "unknown error"));
        }
    }

    private string ImagesDirectory => Path.Combine(_root, "launcher-images");

    private string StoreImagesDirectory => Path.Combine(ImagesDirectory, "dlsite");

    /// <summary>Where a game's DLsite main image is kept, or null when it has no store page.</summary>
    private string? StoreImageFile(GameEntry game) =>
        string.IsNullOrWhiteSpace(game.Metadata?.ProductId)
            ? null
            : Path.Combine(StoreImagesDirectory, Path.GetFileName(game.Metadata!.ProductId) + ".jpg");

    private string? ExistingStoreImage(GameEntry game) =>
        StoreImageFile(game) is { } path && File.Exists(path) ? path : null;

    /// <summary>Downloads DLsite main images for cards that should show one but don't have it yet.</summary>
    private void QueueStoreImages(CancellationToken token)
    {
        if (_storeImageTask is { IsCompleted: false })
        {
            _storeImagesAgain = true;
            return;
        }
        _storeImageTask = StoreImagesAsync(token);
    }

    private async Task StoreImagesAsync(CancellationToken token)
    {
        do
        {
            _storeImagesAgain = false;
            var wanted = _games.Where(g => g.WantsStoreImage && g.StoreImagePath is null &&
                                           StoreImageFile(g) is not null &&
                                           !string.IsNullOrWhiteSpace(g.Metadata?.ImageUrl)).ToList();
            foreach (var game in wanted)
            {
                if (token.IsCancellationRequested || _closed) return;
                string path = StoreImageFile(game)!;
                if (!File.Exists(path))
                {
                    try
                    {
                        string url = game.Metadata!.ImageUrl!;
                        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
                        if (!WebLinks.IsTrusted(url)) continue;
                        var bytes = await MetadataService.Client.GetByteArrayAsync(url, token);
                        using (var check = ImageKit.Decode(bytes))
                        {
                            if (check is null) throw new InvalidDataException("not a picture");
                        }

                        Directory.CreateDirectory(StoreImagesDirectory);
                        string temp = path + ".tmp";
                        await File.WriteAllBytesAsync(temp, bytes, token);
                        File.Move(temp, path, overwrite: true);
                        await Task.Delay(500, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Likely offline or blocked; the next launch tries again.
                        Diagnostics.Log($"DLsite image for {game.Metadata?.ProductId} failed: {ex.Message}");
                        return;
                    }
                }

                if (_closed || !_games.Contains(game) || !game.WantsStoreImage) continue;
                string? oldCache = GameImageLoader.CachePathFor(game);
                game.StoreImagePath = path;
                await ReloadThumbnailAsync(game, oldCache);
            }
        }
        while (_storeImagesAgain && !token.IsCancellationRequested && !_closed);
    }

    private void SetStoreImagePreference(GameEntry game, bool prefer)
    {
        // The old framing was chosen for the other picture.
        UpdateCardImage(game, record =>
        {
            record.UseStoreImage = prefer;
            record.Framing = null;
        });
        if (prefer && game.StoreImagePath is null)
        {
            QueueStoreImages(_loadCts?.Token ?? CancellationToken.None);
            SetStatus($"Getting the DLsite image for {game.CardTitle}\u2026");
            return;
        }
        SetStatus(prefer
            ? $"{game.CardTitle} uses its DLsite image"
            : $"{game.CardTitle} uses its title screen again");
    }

    private string? ResolveCustomImage(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        string path = Path.Combine(ImagesDirectory, Path.GetFileName(fileName));
        return File.Exists(path) ? path : null;
    }

    private void DeleteCustomImageFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        try
        {
            File.Delete(Path.Combine(ImagesDirectory, Path.GetFileName(fileName)));
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Could not delete old card image: " + ex.Message);
        }
    }

    private void OnImageAction(GameEntry game, ImageAction action)
    {
        switch (action)
        {
            case ImageAction.Change:
                ChangeImage(game);
                break;
            case ImageAction.AdjustFraming:
                AdjustFraming(game);
                break;
            case ImageAction.Reset:
                ResetImage(game);
                break;
            case ImageAction.UseStoreImage:
                SetStoreImagePreference(game, true);
                break;
            case ImageAction.UseTitleImage:
                SetStoreImagePreference(game, false);
                break;
        }
    }

    private async void ChangeImage(GameEntry game)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a card image for " + game.CardTitle,
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll, FilePickerFileTypes.All }
        });
        string? picked = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (picked is null) return;

        var image = GameImageLoader.LoadPicture(picked);
        if (image is null)
        {
            SetStatus("That file could not be opened as an image");
            return;
        }

        var framingDialog = new ImageFramingDialog(image, null, game.CardTitle);
        if (!await framingDialog.ShowAsync(this)) return;

        // Keep a private copy so the card survives the original being moved or deleted.
        string extension = Path.GetExtension(picked).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension)) extension = ".png";
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(game.Key.ToLowerInvariant())))[..12];
        string fileName = $"{hash}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}";
        try
        {
            Directory.CreateDirectory(ImagesDirectory);
            File.Copy(picked, Path.Combine(ImagesDirectory, fileName), overwrite: true);
        }
        catch (Exception ex)
        {
            SetStatus("Could not copy the image: " + ex.Message);
            return;
        }

        var framing = framingDialog.Framing;
        UpdateCardImage(game, record =>
        {
            DeleteCustomImageFile(record.CustomImage);
            record.CustomImage = fileName;
            record.Framing = framing.IsDefault ? null : framing;
        });
        SetStatus($"New image set for {game.CardTitle}");
    }

    private async void AdjustFraming(GameEntry game)
    {
        var source = GameImageLoader.LoadSourcePicture(game);
        if (source is null)
        {
            SetStatus("This game has no picture to frame \u00b7 use Change image\u2026 to pick one");
            return;
        }

        var dialog = new ImageFramingDialog(source, game.Framing ?? GameImageLoader.DefaultFraming(game), game.CardTitle);
        if (!await dialog.ShowAsync(this)) return;

        var framing = dialog.Framing;
        UpdateCardImage(game, record => record.Framing = framing.IsDefault ? null : framing);
        SetStatus($"Image framing updated for {game.CardTitle}");
    }

    private void ResetImage(GameEntry game)
    {
        bool hadCustom = game.CustomImagePath is not null;
        UpdateCardImage(game, record =>
        {
            DeleteCustomImageFile(record.CustomImage);
            record.CustomImage = null;
            record.Framing = null;
        });
        SetStatus(hadCustom
            ? $"Removed the custom image for {game.CardTitle}"
            : $"Image framing reset for {game.CardTitle}");
    }

    /// <summary>Applies an image change to the saved record and the card, then rebuilds the thumbnail.</summary>
    private void UpdateCardImage(GameEntry game, Action<GameRecord> change)
    {
        if (!_store.Data.Games.TryGetValue(game.Key, out var record))
        {
            record = new GameRecord { AddedUtc = game.AddedUtc };
            _store.Data.Games[game.Key] = record;
        }

        string? oldCache = GameImageLoader.CachePathFor(game);
        change(record);
        game.CustomImagePath = ResolveCustomImage(record.CustomImage);
        game.Framing = record.Framing;
        game.StoreImageChoice = record.UseStoreImage;
        SaveData();
        _ = ReloadThumbnailAsync(game, oldCache);
    }

    private async Task ReloadThumbnailAsync(GameEntry game, string? oldCache)
    {
        var token = _loadCts?.Token ?? CancellationToken.None;
        Bitmap? bitmap;
        try
        {
            bitmap = await GameImageLoader.CreateThumbnailAsync(game, token);
        }
        catch
        {
            return;
        }

        if (_closed || token.IsCancellationRequested || !_games.Contains(game))
        {
            bitmap?.Dispose();
            return;
        }

        // The old picture stays on the card until the new one is ready.
        var old = game.Thumbnail;
        game.Thumbnail = bitmap;
        game.ThumbnailRequested = true;
        _grid.RefreshCard(game);
        if (!ReferenceEquals(old, bitmap)) old?.Dispose();

        // Square cards' uncropped picture changes too.
        game.FullThumbnailRequested = false;
        if (_store.Data.CardShape == CardShape.Square)
        {
            game.FullThumbnailRequested = true;
            _ = LoadFullThumbnailAsync(game, token);
        }

        string? newCache = GameImageLoader.CachePathFor(game);
        if (oldCache is not null && !string.Equals(oldCache, newCache, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(oldCache); } catch { /* the next rescan prunes it */ }
        }
    }

    private async void ShowGameInfo(GameEntry game)
    {
        if (game.Metadata is null) return;
        await new GameInfoDialog(game).ShowAsync(this);
    }

    /// <summary>Rebuilds the visible list; a reset returns to page 1 unless <paramref name="keepPage"/>.</summary>
    private void RefreshView(bool resetScroll = false, bool keepPage = false)
    {
        var view = GetView();
        _visibleCount = view.Count;

        if (resetScroll && !keepPage) _page = 0;
        int size = _store.Data.PageSize;
        _pageCount = size > 0 ? Math.Max(1, (view.Count + size - 1) / size) : 1;
        _page = Math.Clamp(_page, 0, _pageCount - 1);
        if (_pageCount > 1)
        {
            int start = _page * size;
            view = view.GetRange(start, Math.Min(size, view.Count - start));
        }

        _status.SetPages(_page, _showingReleases ? 1 : _pageCount);
        _grid.SetVisibleGames(view, resetScroll);
        UpdateHeaderSubtitle();
        if (!_statusTimer.IsEnabled) ResetStatus();
    }

    private void GoToPage(int page)
    {
        page = Math.Clamp(page, 0, _pageCount - 1);
        if (page == _page) return;
        _page = page;
        RefreshView(resetScroll: true, keepPage: true);
    }

    /// <summary>First and last (one-based) positions shown on the current page.</summary>
    private (int First, int Last) PageRange()
    {
        int size = _store.Data.PageSize;
        if (_pageCount <= 1 || size <= 0) return (1, _visibleCount);
        int first = _page * size + 1;
        return (first, Math.Min(_visibleCount, first + size - 1));
    }

    private void ShowPageMenu(Point location)
    {
        int size = _store.Data.PageSize;
        if (_pageCount <= 1 || size <= 0) return;

        var menu = Menus.Create(checks: true);
        for (int i = 0; i < _pageCount; i++)
        {
            int page = i;
            int first = i * size + 1;
            int last = Math.Min(_visibleCount, first + size - 1);
            var item = Menus.Item($"Page {i + 1}   \u00b7   {first}\u2013{last}", (_, _) => GoToPage(page));
            item.Checked = i == _page;
            menu.Items.Add(item);
        }
        Menus.Show(menu, _status, location, MenuDirection.AboveRight);
    }

    private static readonly int[] PageSizePresets = { 24, 48, 96, 120 };

    private DarkMenuItem BuildPageSizeMenu(DarkMenu owner)
    {
        int current = _store.Data.PageSize;
        var root = Menus.Item(current > 0 ? $"Games per page: {current}" : "Games per page: all");

        void Add(int size, string label)
        {
            var item = Menus.Item(label, (_, _) => SetPageSize(size));
            item.Checked = current == size;
            root.DropDownItems.Add(item);
        }

        Add(0, "All on one page");
        root.DropDownItems.Add(Menus.Separator());
        foreach (int size in PageSizePresets) Add(size, $"{size} per page");
        root.DropDownItems.Add(Menus.Separator());

        bool custom = current > 0 && !PageSizePresets.Contains(current);
        var customItem = Menus.Item(custom ? $"Custom ({current})\u2026" : "Custom\u2026", (_, _) => ChoosePageSize());
        customItem.Checked = custom;
        root.DropDownItems.Add(customItem);

        Menus.StyleSubmenu(root, owner, checks: true);
        return root;
    }

    /// <summary>Applies the saved DLsite proxy; returns a warning when it's unusable (requests then go direct).</summary>
    private string? ApplyDlsiteProxy()
    {
        if (DlsiteConnection.Apply(_store.Data.DlsiteProxy, out var error)) return null;
        return "The saved DLsite proxy can't be used (" + error + ") \u00b7 connecting directly";
    }

    private async void ShowProxyDialog()
    {
        var dialog = new ProxyDialog(_store.Data.DlsiteProxy);
        if (!await dialog.ShowAsync(this)) return;

        var settings = dialog.Settings;
        _store.Data.DlsiteProxy = settings.IsSet ? settings : null;
        ApplyDlsiteProxy();
        SaveData();

        string message = settings.HasProxy ? "DLsite requests now go through the proxy"
            : settings.IsSet ? "DLsite requests now go out through the chosen network adapter"
            : "DLsite requests now connect normally";

        if (dialog.RedownloadReleases)
        {
            _releaseCts?.Cancel();
            _releasesFetching = false;
            _releaseStore.Clear();
            _releaseTagFilter.Clear();
            _releases.RefreshFilter();
            UpdateTagsButton();
            ShowReleaseDays();
            if (_showingReleases)
            {
                _ = FetchReleasesAsync(refresh: false);
                message += " \u00b7 re-downloading new releases";
            }
            else
            {
                message += " \u00b7 new releases will re-download when you open them";
            }
        }
        SetStatus(message);
    }

    private DarkMenuItem BuildStyleMenu(DarkMenu owner)
    {
        var root = Menus.Item($"Style: {UiStyleLabels.Label(Look.Style)}");
        foreach (var style in Enum.GetValues<UiStyle>())
        {
            var item = Menus.Item(UiStyleLabels.Label(style), (_, _) => ApplyStyle(style));
            item.Checked = style == Look.Style;
            root.DropDownItems.Add(item);
        }
        Menus.StyleSubmenu(root, owner, checks: true);
        return root;
    }

    private DarkMenuItem BuildCardShapeMenu(DarkMenu owner)
    {
        var current = _store.Data.CardShape;
        var root = Menus.Item($"Card shape: {(current == CardShape.Square ? "Square" : "Rectangular")}");
        foreach (var (shape, label) in new[] { (CardShape.Rectangular, "Rectangular"), (CardShape.Square, "Square") })
        {
            var item = Menus.Item(label, (_, _) => SetCardShape(shape));
            item.Checked = shape == current;
            root.DropDownItems.Add(item);
        }
        Menus.StyleSubmenu(root, owner, checks: true);
        return root;
    }

    private void SetCardShape(CardShape shape)
    {
        if (shape == _store.Data.CardShape) return;
        _store.Data.CardShape = shape;
        GameCard.SquareCards = shape == CardShape.Square;
        SaveData();
        _grid.ResetArtwork();
        _grid.RelayoutCards();
        if (shape == CardShape.Square) QueueFullThumbnails(_loadCts?.Token ?? CancellationToken.None);
        SetStatus(shape == CardShape.Square ? "Square cards" : "Rectangular cards");
    }

    private DarkMenuItem BuildCardPictureMenu(DarkMenu owner)
    {
        var current = _store.Data.CardPicture;
        var root = Menus.Item($"Card pictures: {(current == CardPicture.StoreImage ? "DLsite image" : "Title screen")}");
        foreach (var (choice, label) in new[] { (CardPicture.StoreImage, "DLsite image"), (CardPicture.TitleScreen, "Title screen") })
        {
            var item = Menus.Item(label, (_, _) => SetCardPicture(choice));
            item.Checked = choice == current;
            root.DropDownItems.Add(item);
        }
        Menus.StyleSubmenu(root, owner, checks: true);
        return root;
    }

    private void SetCardPicture(CardPicture choice)
    {
        if (choice == _store.Data.CardPicture) return;
        // Old thumbnails are kept, so switching back is instant; the next rescan prunes them.
        var before = _games.ToDictionary(game => game, game => GameImageLoader.CachePathFor(game));
        _store.Data.CardPicture = choice;
        GameEntry.StoreImagesFirst = choice == CardPicture.StoreImage;
        SaveData();

        foreach (var game in _games)
        {
            if (!string.Equals(before[game], GameImageLoader.CachePathFor(game), StringComparison.OrdinalIgnoreCase))
                _ = ReloadThumbnailAsync(game, null);
        }
        if (GameEntry.StoreImagesFirst) QueueStoreImages(_loadCts?.Token ?? CancellationToken.None);
        SetStatus(GameEntry.StoreImagesFirst
            ? "Cards show the DLsite image when a game has one"
            : "Cards show the title screen when a game has one");
    }

    /// <summary>Loads the uncropped pictures square cards show (cached after the first time).</summary>
    private void QueueFullThumbnails(CancellationToken token)
    {
        foreach (var game in _games)
        {
            if (game.FullThumbnail is not null || game.FullThumbnailRequested) continue;
            game.FullThumbnailRequested = true;
            _ = LoadFullThumbnailAsync(game, token);
        }
    }

    private async Task LoadFullThumbnailAsync(GameEntry game, CancellationToken token)
    {
        Bitmap? bitmap;
        try
        {
            bitmap = await GameImageLoader.CreateThumbnailAsync(game, token, full: true);
        }
        catch
        {
            return;
        }
        if (token.IsCancellationRequested || _closed || !_games.Contains(game))
        {
            bitmap?.Dispose();
            return;
        }
        var old = game.FullThumbnail;
        game.FullThumbnail = bitmap;
        if (!ReferenceEquals(old, bitmap)) old?.Dispose();
        _grid.RefreshCard(game);
    }

    /// <summary>Switches the whole window to another style without restarting.</summary>
    private void ApplyStyle(UiStyle style)
    {
        if (style == Look.Style) return;
        Look.Apply(style);
        _store.Data.Style = style;
        SaveData();

        Background = Look.Brush(Look.Bg);
        Foreground = Look.Brush(Look.Text);
        _header.Mascot = style == UiStyle.Blob ? Mascots.Header : null;
        NativeMethods.StyleTitleBar(this);
        _header.InvalidateMeasure();
        _grid.ResetArtwork();
        // Drawn controls pick up the new colors when they draw again.
        foreach (var visual in this.GetVisualDescendants()) visual.InvalidateVisual();
        SetStatus("Style: " + UiStyleLabels.Label(style));
    }

    private async void ChoosePageSize()
    {
        var dialog = new PageSizeDialog(_store.Data.PageSize);
        if (await dialog.ShowAsync(this)) SetPageSize(dialog.PageSize);
    }

    private void SetPageSize(int size)
    {
        size = Math.Clamp(size, 0, PageSizeDialog.MaxPageSize);
        _store.Data.PageSize = size;
        SaveData();
        RefreshView(resetScroll: true);
        SetStatus(size == 0
            ? "Showing every game on one page"
            : $"Showing {size} games per page \u00b7 Ctrl+PageUp / Ctrl+PageDown to turn pages");
    }

    private List<GameEntry> GetView()
    {
        IEnumerable<GameEntry> query = _games;

        if (_store.Data.FavoritesOnly)
            query = query.Where(g => g.Favorite);

        if (_store.Data.ActiveGroup is { } group)
            query = query.Where(g => g.Groups.Contains(group, StringComparer.CurrentCultureIgnoreCase));

        if (_store.Data.NoStorePageOnly)
            query = query.Where(g => g.Metadata is null);

        if (_store.Data.EngineFilter is { } engine)
            query = query.Where(g => g.Family == engine);

        if (!_tagFilter.IsEmpty)
        {
            var tagLanguage = _store.Data.MetadataLanguage;
            query = query.Where(g => _tagFilter.Matches(g.Tags));
        }

        var search = _search.TextValue.Trim();
        if (search.Length > 0)
        {
            bool MatchesMetadata(GameEntry game, string token)
            {
                bool Match(string? value) =>
                    !string.IsNullOrWhiteSpace(value) &&
                    value.Contains(token, StringComparison.CurrentCultureIgnoreCase);

                // tags added by hand, under either of their names
                if (game.AddedTags.Any(tag => Match(tag) || Match(TagNames.For(tag, MetadataLanguage.English)) ||
                                              Match(TagNames.For(tag, MetadataLanguage.Japanese))))
                    return true;

                if (game.Metadata is not { } metadata) return false;

                if (Match(metadata.Title) || Match(metadata.Maker) ||
                    metadata.Tags.Any(Match))
                    return true;

                if (metadata.English is { } english &&
                    (Match(english.Title) || Match(english.Maker) || english.Tags.Any(Match)))
                    return true;

                return false;
            }

            var tokens = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            query = query.Where(g =>
            {
                string cardTitle = g.CardTitle;
                string folderName = g.FolderName;
                return tokens.All(token =>
                    cardTitle.Contains(token, StringComparison.CurrentCultureIgnoreCase) ||
                    folderName.Contains(token, StringComparison.CurrentCultureIgnoreCase) ||
                    g.Title.Contains(token, StringComparison.CurrentCultureIgnoreCase) ||
                    MatchesMetadata(g, token));
            });
        }

        var list = query.ToList();
        var comparer = StringComparer.CurrentCultureIgnoreCase;

        // CardTitle is computed on each read; resolve it once per game for the sort.
        var titles = new Dictionary<GameEntry, string>(list.Count, ReferenceEqualityComparer.Instance);
        foreach (var game in list) titles[game] = game.CardTitle;

        int ByTitle(GameEntry a, GameEntry b) => comparer.Compare(titles[a], titles[b]);
        DateTime Played(GameEntry g) => g.LastPlayedUtc ?? DateTime.MinValue;
        DateTime Downloaded(GameEntry g) => g.FolderCreatedUtc ?? g.AddedUtc;

        // Games without a known release date go last in either direction.
        int ByRelease(GameEntry a, GameEntry b, bool newestFirst)
        {
            var left = a.Metadata?.Released;
            var right = b.Metadata?.Released;
            if (left is null || right is null) return (left is null).CompareTo(right is null);
            return newestFirst ? right.Value.CompareTo(left.Value) : left.Value.CompareTo(right.Value);
        }

        Comparison<GameEntry> comparison = _store.Data.Sort switch
        {
            SortMode.TitleAsc => (a, b) =>
            {
                int c = ByTitle(a, b);
                return c != 0 ? c : comparer.Compare(a.Key, b.Key);
            },
            SortMode.TitleDesc => (a, b) =>
            {
                int c = ByTitle(b, a);
                return c != 0 ? c : comparer.Compare(a.Key, b.Key);
            },
            SortMode.RatingDesc => (a, b) =>
            {
                int c = b.Rating.CompareTo(a.Rating);
                return c != 0 ? c : ByTitle(a, b);
            },
            SortMode.RatingAsc => (a, b) =>
            {
                int c = a.Rating.CompareTo(b.Rating);
                return c != 0 ? c : ByTitle(a, b);
            },
            SortMode.RecentlyPlayed => (a, b) =>
            {
                int c = Played(b).CompareTo(Played(a));
                return c != 0 ? c : ByTitle(a, b);
            },
            SortMode.MostPlayed => (a, b) =>
            {
                int c = b.PlaySeconds.CompareTo(a.PlaySeconds);
                return c != 0 ? c : ByTitle(a, b);
            },
            SortMode.RecentlyAdded => (a, b) =>
            {
                int c = Downloaded(b).CompareTo(Downloaded(a));
                return c != 0 ? c : ByTitle(a, b);
            },
            SortMode.ReleaseNewest => (a, b) =>
            {
                int c = ByRelease(a, b, newestFirst: true);
                return c != 0 ? c : ByTitle(a, b);
            },
            SortMode.ReleaseOldest => (a, b) =>
            {
                int c = ByRelease(a, b, newestFirst: false);
                return c != 0 ? c : ByTitle(a, b);
            },
            SortMode.FolderName => (a, b) =>
            {
                int c = comparer.Compare(a.FolderName, b.FolderName);
                return c != 0 ? c : ByTitle(a, b);
            },
            _ => (a, b) => ByTitle(a, b)
        };

        list.Sort(comparison);
        return list;
    }

    private void UpdateSortButton()
    {
        string sort = SortLabels.Label(_store.Data.Sort).Replace("  ", " ");
        if (_store.Data.ActiveGroup is { } group)
        {
            _sortButton.Text = group.Length > 16 ? group[..15].TrimEnd() + "\u2026" : group;
            _sortButton.Checked = true;
            Ui.Tip(_sortButton, $"Showing the group \u201c{group}\u201d \u00b7 sorted by {sort}");
        }
        else if (_store.Data.NoStorePageOnly)
        {
            _sortButton.Text = "No DLsite Page";
            _sortButton.Checked = true;
            Ui.Tip(_sortButton, $"Showing games without a DLsite page \u00b7 sorted by {sort}");
        }
        else
        {
            _sortButton.Text = SortLabels.Label(_store.Data.Sort);
            _sortButton.Checked = false;
            Ui.Tip(_sortButton, "Change the sort order or pick a group");
        }
        _sortButton.InvalidateVisual();
    }

    private void UpdateHeaderSubtitle()
    {
        if (_showingReleases)
        {
            int releases = _releases.TotalCount;
            _header.Subtitle = $"{releases} NEW {(releases == 1 ? "GAME" : "GAMES")} \u00b7 LAST {ReleaseStore.DaysBack} DAYS";
            return;
        }

        int favorites = _games.Count(g => g.Favorite);
        string text = $"{_games.Count} {(_games.Count == 1 ? "GAME" : "GAMES")}";
        if (favorites > 0) text += $" \u00b7 {favorites} FAVORITE{(favorites == 1 ? "" : "S")}";
        var families = _games.Select(g => g.Family).Distinct().ToList();
        if (_store.Data.EngineFilter is { } engine) text += " \u00b7 " + EngineFamilies.Label(engine).ToUpperInvariant();
        else if (families.Count == 1) text += " \u00b7 " + EngineFamilies.Label(families[0]).ToUpperInvariant();
        _header.Subtitle = text;
    }

    private string ScanStatus() => _scanFound > 0
        ? $"Scanning for games \u00b7 {_scanFound} found so far\u2026"
        : "Scanning for games\u2026";

    private void ResetStatus()
    {
        if (_showingReleases)
        {
            _status.LeftText = ReleaseStatus() + (_scanning ? " \u00b7 scanning the library\u2026" : "");
            return;
        }

        if (_scanning)
        {
            _status.LeftText = ScanStatus();
            return;
        }

        if (_games.Count == 0)
        {
            _status.LeftText = "No games detected \u00b7 Place game folders here, then press Rescan";
            return;
        }

        if (_pageCount > 1)
        {
            var (first, last) = PageRange();
            string scope = !_tagFilter.IsEmpty ? "games " + _tagFilter.Describe()
                : _visibleCount == _games.Count ? "games" : "matching games";
            _status.LeftText = $"Showing {first}\u2013{last} of {_visibleCount} {scope}";
            return;
        }

        if (!_tagFilter.IsEmpty)
        {
            _status.LeftText = $"{_visibleCount} of {_games.Count} games {_tagFilter.Describe()}";
            return;
        }

        if (_visibleCount != _games.Count)
        {
            _status.LeftText = $"{_visibleCount} of {_games.Count} games shown";
            return;
        }

        _status.LeftText = $"{_games.Count} {(_games.Count == 1 ? "game" : "games")}";
    }

    private void SetStatus(string text, bool transient = true)
    {
        _status.LeftText = text;
        _statusTimer.Stop();
        if (transient) _statusTimer.Start();
    }

    private void ShowSortMenu()
    {
        var menu = Menus.Create(checks: true);
        AddSortItem(menu, SortMode.TitleAsc, "Title A \u2192 Z");
        AddSortItem(menu, SortMode.TitleDesc, "Title Z \u2192 A");
        AddSortItem(menu, SortMode.FolderName, "Folder Name");
        menu.Items.Add(Menus.Separator());
        AddSortItem(menu, SortMode.RatingDesc, "Rating High \u2192 Low");
        AddSortItem(menu, SortMode.RatingAsc, "Rating Low \u2192 High");
        menu.Items.Add(Menus.Separator());
        AddSortItem(menu, SortMode.RecentlyPlayed, "Last Played");
        AddSortItem(menu, SortMode.MostPlayed, "Most Played");
        menu.Items.Add(Menus.Separator());
        AddSortItem(menu, SortMode.ReleaseNewest, "Release Date New \u2192 Old");
        AddSortItem(menu, SortMode.ReleaseOldest, "Release Date Old \u2192 New");
        AddSortItem(menu, SortMode.RecentlyAdded, "Recently Downloaded");

        menu.Items.Add(Menus.Separator());
        AddEngineItem(menu, null, "All Engines");
        // Engines with no games are left out, unless the library is narrowed to one.
        foreach (var family in EngineFamilies.All)
        {
            int count = _games.Count(g => g.Family == family);
            if (count > 0 || _store.Data.EngineFilter == family) AddEngineItem(menu, family, $"{EngineFamilies.Label(family)}  ({count})");
        }

        // Groups narrow the library; the sort order above still applies inside a group.
        menu.Items.Add(Menus.Separator());
        AddGroupItem(menu, null, "All Games");
        // Games without a store page, to fix with Set DLsite page… or a rename.
        var noStore = Menus.Item($"No DLsite Page  ({_games.Count(g => g.Metadata is null)})", (_, _) => SelectNoStorePage());
        noStore.Checked = _store.Data.NoStorePageOnly;
        menu.Items.Add(noStore);
        var groupItems = new List<DarkMenuItem>();
        foreach (var name in _store.Data.Groups)
        {
            int count = _games.Count(g => g.Groups.Contains(name, StringComparer.CurrentCultureIgnoreCase));
            groupItems.Add(AddGroupItem(menu, name, $"{GameCard.MenuText(name)}  ({count})"));
        }

        var manage = Menus.Item("Manage Groups");
        manage.DropDownItems.Add(Menus.Item("New Group\u2026", (_, _) => _ = CreateGroupAsync()));
        if (_store.Data.Groups.Count > 0)
        {
            var rename = Menus.Item("Rename");
            var delete = Menus.Item("Delete");
            foreach (var name in _store.Data.Groups)
            {
                string group = name;
                rename.DropDownItems.Add(Menus.Item(GameCard.MenuText(group) + "\u2026", (_, _) => RenameGroup(group)));
                delete.DropDownItems.Add(Menus.Item(GameCard.MenuText(group) + "\u2026", (_, _) => DeleteGroup(group)));
            }
            Menus.StyleSubmenu(rename, menu, checks: true);
            Menus.StyleSubmenu(delete, menu, checks: true);
            manage.DropDownItems.Add(rename);
            manage.DropDownItems.Add(delete);
        }
        Menus.StyleSubmenu(manage, menu, checks: true);
        menu.Items.Add(manage);
        // Groups can be dragged up or down to reorder them.
        menu.EnableReorder(groupItems, () => SaveGroupOrder(groupItems));

        Menus.Show(menu, _sortButton, new Point(0, _sortButton.Bounds.Height + 4));
    }

    private DarkMenuItem AddGroupItem(DarkMenu menu, string? group, string text)
    {
        // The group is read from Tag when clicked, because reordering swaps the items' contents.
        // (Releasing the mouse after a drag isn't a click; the menu sees to that.)
        var item = Menus.Item(text, (sender, _) => SelectGroup((sender as DarkMenuItem)?.Tag as string));
        item.Tag = group;
        item.Checked = !_store.Data.NoStorePageOnly &&
                       string.Equals(_store.Data.ActiveGroup, group, StringComparison.CurrentCultureIgnoreCase);
        menu.Items.Add(item);
        return item;
    }

    private void SaveGroupOrder(List<DarkMenuItem> items)
    {
        var order = items.Select(item => item.Tag).OfType<string>().ToList();
        _store.Data.Groups.Clear();
        _store.Data.Groups.AddRange(order);
        SaveData();
        SetStatus("Group order saved");
    }

    private void AddEngineItem(DarkMenu menu, EngineFamily? family, string text)
    {
        var item = Menus.Item(text, (_, _) => SelectEngine(family));
        item.Checked = _store.Data.EngineFilter == family;
        menu.Items.Add(item);
    }

    private void SelectEngine(EngineFamily? family)
    {
        _store.Data.EngineFilter = family;
        SaveData();
        RefreshView(resetScroll: true);
        UpdateHeaderSubtitle();
        SetStatus(family is { } shown ? $"Showing {EngineFamilies.Label(shown)} games" : "Showing games from every engine");
    }

    private void SelectNoStorePage()
    {
        _store.Data.ActiveGroup = null;
        _store.Data.NoStorePageOnly = true;
        UpdateSortButton();
        SaveData();
        RefreshView(resetScroll: true);
        SetStatus("Showing games without a DLsite page \u00b7 right-click one \u2192 Set DLsite page\u2026 to fix it");
    }

    private void SelectGroup(string? group)
    {
        _store.Data.NoStorePageOnly = false;
        _store.Data.ActiveGroup = group;
        UpdateSortButton();
        SaveData();
        RefreshView(resetScroll: true);
        SetStatus(group is null ? "Showing all games" : $"Showing the group \u201c{group}\u201d");
    }

    /// <summary>Asks for a group name; null when cancelled or left empty.</summary>
    private async Task<string?> AskGroupNameAsync(string title, string caption, string current, string okText)
    {
        var dialog = new TextPromptDialog(title, caption, current, okText);
        if (!await dialog.ShowAsync(this)) return null;
        string name = dialog.Value;
        return name.Length == 0 ? null : name;
    }

    /// <summary>The saved spelling of an existing group, or null if none.</summary>
    private string? FindGroup(string name) =>
        _store.Data.Groups.FirstOrDefault(g => string.Equals(g, name, StringComparison.CurrentCultureIgnoreCase));

    private async Task<string?> CreateGroupAsync()
    {
        string? name = await AskGroupNameAsync("New group", "Name the new group:", "", "Create");
        if (name is null) return null;
        if (FindGroup(name) is { } existing) return existing;
        _store.Data.Groups.Add(name);
        SaveData();
        SetStatus($"Created the group \u201c{name}\u201d \u00b7 right-click games to add them");
        return name;
    }

    private async void RenameGame(GameEntry game)
    {
        var dialog = new TextPromptDialog("Rename game",
            "Name to show (empty for the automatic name):", game.CardTitle, "Rename");
        if (!await dialog.ShowAsync(this)) return;

        // An empty name, or the automatic one typed back in, restores the automatic name.
        string? previous = game.CustomTitle;
        game.CustomTitle = null;
        string name = dialog.Value;
        if (name.Length > 0 && name != game.CardTitle) game.CustomTitle = name;
        if (game.CustomTitle == previous) return;

        if (_store.Data.Games.TryGetValue(game.Key, out var record)) record.CustomTitle = game.CustomTitle;
        SaveData();
        _grid.RefreshCard(game);
        if (_store.Data.Sort is SortMode.TitleAsc or SortMode.TitleDesc) RefreshView(keepPage: true);
        SetStatus(game.CustomTitle is null
            ? $"{game.CardTitle} uses its automatic name again"
            : $"Renamed to {game.CardTitle}");
    }

    private async void ToggleGroup(GameEntry game, string? group)
    {
        bool add;
        if (group is null)
        {
            group = await CreateGroupAsync();
            if (group is null) return;
            add = true;
        }
        else
        {
            add = !game.Groups.Contains(group, StringComparer.CurrentCultureIgnoreCase);
        }

        game.Groups.RemoveAll(g => string.Equals(g, group, StringComparison.CurrentCultureIgnoreCase));
        if (add) game.Groups.Add(group);
        if (_store.Data.Games.TryGetValue(game.Key, out var record))
            record.Groups = game.Groups.Count > 0 ? game.Groups.ToList() : null;
        SaveData();

        if (_store.Data.ActiveGroup is not null) RefreshView(keepPage: true);
        SetStatus(add
            ? $"Added {game.CardTitle} to \u201c{group}\u201d"
            : $"Removed {game.CardTitle} from \u201c{group}\u201d");
    }

    /// <summary>Renames a group everywhere, or removes it when <paramref name="replacement"/> is null.</summary>
    private void ReplaceGroup(string group, string? replacement)
    {
        void Fix(List<string> list)
        {
            int index = list.FindIndex(g => string.Equals(g, group, StringComparison.CurrentCultureIgnoreCase));
            if (index < 0) return;
            list.RemoveAt(index);
            if (replacement is not null && FindIn(list, replacement) < 0) list.Insert(index, replacement);
        }
        static int FindIn(List<string> list, string name) =>
            list.FindIndex(g => string.Equals(g, name, StringComparison.CurrentCultureIgnoreCase));

        Fix(_store.Data.Groups);
        foreach (var record in _store.Data.Games.Values)
        {
            if (record.Groups is null) continue;
            Fix(record.Groups);
            if (record.Groups.Count == 0) record.Groups = null;
        }
        foreach (var game in _games) Fix(game.Groups);

        if (string.Equals(_store.Data.ActiveGroup, group, StringComparison.CurrentCultureIgnoreCase))
            _store.Data.ActiveGroup = replacement;
        UpdateSortButton();
        SaveData();
        RefreshView(keepPage: true);
    }

    private async void RenameGroup(string group)
    {
        string? name = await AskGroupNameAsync("Rename group", $"New name for \u201c{group}\u201d:", group, "Rename");
        if (name is null || name == group) return;
        if (FindGroup(name) is { } existing && !string.Equals(existing, group, StringComparison.CurrentCultureIgnoreCase))
        {
            await MessageDialog.ShowAsync(this, $"There is already a group called \u201c{existing}\u201d.", "Rename group");
            return;
        }
        ReplaceGroup(group, name);
        SetStatus($"Renamed \u201c{group}\u201d to \u201c{name}\u201d");
    }

    private async void DeleteGroup(string group)
    {
        int count = _games.Count(g => g.Groups.Contains(group, StringComparer.CurrentCultureIgnoreCase));
        bool delete = await MessageDialog.ShowAsync(this,
            $"Delete the group \u201c{group}\u201d?\n\nIts {count} {(count == 1 ? "game stays" : "games stay")} in your library; only the group goes away.",
            "Delete group", MessageButtons.OkCancel);
        if (!delete) return;
        ReplaceGroup(group, null);
        SetStatus($"Deleted the group \u201c{group}\u201d");
    }

    private void AddSortItem(DarkMenu menu, SortMode mode, string text)
    {
        var item = Menus.Item(text, (_, _) =>
        {
            _store.Data.Sort = mode;
            UpdateSortButton();
            SaveData();
            RefreshView(resetScroll: true);
            SetStatus("Sorted by " + SortLabels.Label(mode).Replace("  ", " "));
        });
        item.Checked = _store.Data.Sort == mode;
        menu.Items.Add(item);
    }

    /// <summary>Tag picker for one game: clicking a tag adds or removes it; DLsite tags stay locked.</summary>
    private void ShowTagPicker(GameEntry game)
    {
        _tagPicker?.Close();
        var locked = new HashSet<string>(game.StoreTags, StringComparer.CurrentCultureIgnoreCase);
        var picked = new TagFilter();
        foreach (var tag in game.Tags) picked.Include.Add(tag);

        var popup = new TagFilterPopup(BuildTagGroups(), picked, _games.Count, _store.Data.MetadataLanguage,
            new TagPicking(game.CardTitle, locked));
        popup.FilterChanged += () =>
        {
            var added = picked.Include.Where(tag => !locked.Contains(tag)).ToList();
            game.AddedTags = added;
            if (_store.Data.Games.TryGetValue(game.Key, out var record)) record.AddedTags = added.Count > 0 ? added : null;
            SaveData();
            // a newly typed tag appears in the list with its count
            popup.UpdateGroups(BuildTagGroups());
            RefreshView(keepPage: true);
        };
        popup.FavoriteToggled += tag =>
        {
            ToggleFavoriteTag(tag);
            popup.UpdateGroups(BuildTagGroups());
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_tagPicker, popup)) _tagPicker = null;
            int count = game.AddedTags.Count;
            SetStatus(count == 0
                ? $"No tags added to {game.CardTitle}"
                : $"{count} tag{(count == 1 ? "" : "s")} added to {game.CardTitle}");
        };
        _tagPicker = popup;
        popup.ShowCentered(this);
    }

    private void ShowTagFilter()
    {
        if (_tagPopup is not null)
        {
            _tagPopup.Close();
            return;
        }

        // The click that closed the popup (by deactivating it) must not reopen it.
        if (_tagPopupClosedUtc is { } closed &&
            (DateTime.UtcNow - closed).TotalMilliseconds < 220)
            return;

        List<TagGroupInfo> groups;
        if (_showingReleases)
        {
            var language = _store.Data.MetadataLanguage;
            groups = BuildTagGroups(_releaseStore.Days().SelectMany(day => day.Items).Select(item => item.TagsFor(language)));
            if (groups.Count == 0)
            {
                SetStatus(_releasesFetching ? "Tags appear once releases have loaded" : "No release tags yet");
                return;
            }
        }
        else
        {
            ValidateTagFilter();
            groups = BuildTagGroups();
            if (groups.Count == 0)
            {
                SetStatus(_games.Count == 0
                    ? "No games detected yet"
                    : "No store tags yet \u00b7 Right-click a game and choose Look up metadata");
                return;
            }
        }

        var popup = new TagFilterPopup(groups,
            _showingReleases ? _releaseTagFilter : _tagFilter,
            _showingReleases ? _releases.TotalCount : _games.Count,
            _store.Data.MetadataLanguage);
        popup.FilterChanged += OnTagFilterChanged;
        popup.FavoriteToggled += tag =>
        {
            ToggleFavoriteTag(tag);
            var language = _store.Data.MetadataLanguage;
            popup.UpdateGroups(_showingReleases
                ? BuildTagGroups(_releaseStore.Days().SelectMany(day => day.Items).Select(item => item.TagsFor(language)))
                : BuildTagGroups());
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_tagPopup, popup)) _tagPopup = null;
            _tagPopupClosedUtc = DateTime.UtcNow;
        };
        _tagPopup = popup;
        popup.ShowBelow(_tagsButton);
    }

    private List<TagGroupInfo> BuildTagGroups()
    {
        var language = _store.Data.MetadataLanguage;
        return BuildTagGroups(_games.Select(game => game.Tags));
    }

    /// <summary>Groups tags by theme with a count of how many entries carry each.</summary>
    private List<TagGroupInfo> BuildTagGroups(IEnumerable<IReadOnlyList<string>?> tagLists)
    {
        var language = _store.Data.MetadataLanguage;
        var counts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var tags in tagLists)
        {
            if (tags is null) continue;

            var unique = tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.CurrentCultureIgnoreCase);

            foreach (var tag in unique)
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
        }

        if (counts.Count == 0) return new List<TagGroupInfo>();

        // Tags sort alphabetically, not by count, so the list stays predictable as the library grows.
        var favorites = new HashSet<string>(_store.Data.FavoriteTags ?? new List<string>(),
            StringComparer.CurrentCultureIgnoreCase);
        var groups = new List<TagGroupInfo>();
        var pinned = counts
            .Where(pair => favorites.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair => new TagChipInfo(pair.Key, pair.Value, Favorite: true))
            .ToList();
        if (pinned.Count > 0)
            groups.Add(new TagGroupInfo(language == MetadataLanguage.Japanese ? "\u2605 \u304a\u6c17\u306b\u5165\u308a" : "\u2605 FAVORITES", pinned));

        groups.AddRange(counts
            .Where(pair => !favorites.Contains(pair.Key))
            .GroupBy(pair => TagCategories.Classify(pair.Key))
            .OrderBy(group => (int)group.Key)
            .Select(group => new TagGroupInfo(
                TagCategories.Label(group.Key, language),
                group
                    .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(pair => new TagChipInfo(pair.Key, pair.Value))
                    .ToList())));
        return groups;
    }

    private void ToggleFavoriteTag(string tag)
    {
        var favorites = _store.Data.FavoriteTags ??= new List<string>();
        int index = favorites.FindIndex(t => string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase));
        if (index >= 0)
        {
            favorites.RemoveAt(index);
            SetStatus($"Unpinned \u201c{tag}\u201d from favorite tags");
        }
        else
        {
            favorites.Add(tag);
            SetStatus($"Pinned \u201c{tag}\u201d to the top of the tag list");
        }
        SaveData();
    }

    /// <summary>Applies the current view's tag filter after a chip click or Clear in the Tags popup.</summary>
    private void OnTagFilterChanged()
    {
        if (_showingReleases)
        {
            _releases.RefreshFilter();
            ShowReleaseDays();
        }
        else
        {
            RefreshView(resetScroll: true);
        }
        UpdateTagsButton();
        _statusTimer.Stop();
        ResetStatus();
    }

    /// <summary>Shows how many tags the current view's filter uses on the Tags button.</summary>
    private void UpdateTagsButton()
    {
        var filter = _showingReleases ? _releaseTagFilter : _tagFilter;
        _tagsButton.Checked = !filter.IsEmpty;
        _tagsButton.Text = filter.IsEmpty ? "Tags" : $"Tags \u00b7 {filter.Count}";
        _tagsButton.InvalidateVisual();
    }

    /// <summary>Shows the same details panel as a library game, built from the cached listing.</summary>
    private async void ShowReleaseInfo(ReleaseItem item, DateTime released)
    {
        var language = _store.Data.MetadataLanguage;
        var metadata = new GameMetadata
        {
            ProductId = item.ProductId,
            Url = item.Url,
            Title = item.Title,
            Maker = item.Maker,
            WorkType = item.WorkTypeName,
            WorkTypeName = item.WorkTypeName,
            Released = released,
            Tags = item.Tags.ToList(),
            Description = item.Description,
            English = item.English is { } en
                ? new GameMetadataText
                {
                    Title = item.Title,
                    Maker = en.Maker,
                    WorkType = en.WorkTypeName,
                    WorkTypeName = en.WorkTypeName,
                    Tags = en.Tags.ToList()
                }
                : null
        };
        var entry = new GameEntry
        {
            Key = item.ProductId,
            RootPath = "",
            Title = item.Title,
            Metadata = metadata,
            MetadataLanguage = language
        };

        var rows = new List<(string, string)>();
        string price = item.Price > 0 ? "\u00a5" + item.Price.ToString("N0") : "Free";
        if (item.DiscountPercent > 0)
            price += $"  ({item.DiscountPercent}% off, normally \u00a5{item.OfficialPrice:N0})";
        rows.Add(("PRICE", price));
        if (item.Rating is { } rating)
            rows.Add(("RATING", $"\u2605 {rating:0.0} from {item.RatingCount:N0} {(item.RatingCount == 1 ? "rating" : "ratings")}"));
        if (item.Sales is { } sales) rows.Add(("SOLD", sales.ToString("N0")));
        var languages = item.LanguageNames().ToList();
        if (languages.Count > 0) rows.Add(("LANGUAGES", string.Join(", ", languages)));
        rows.Add(("PRODUCT", item.ProductId + (IsOwnedRelease(item) ? "  \u00b7  in your library" : "")));

        await new GameInfoDialog(entry, rows).ShowAsync(this);
    }

    private bool IsOwnedRelease(ReleaseItem item) => _ownedProductIds.Contains(item.ProductId);

    /// <summary>Drops filter tags that no game carries any more after a rescan or update.</summary>
    private bool ValidateTagFilter()
    {
        if (_tagFilter.IsEmpty) return false;
        var language = _store.Data.MetadataLanguage;
        var available = _games.SelectMany(game => game.Tags).ToList();
        if (!_tagFilter.RemoveMissing(available)) return false;
        UpdateTagsButton();
        return true;
    }

    private void ToggleMetadataLanguage()
    {
        var next = _store.Data.MetadataLanguage == MetadataLanguage.English
            ? MetadataLanguage.Japanese
            : MetadataLanguage.English;
        _store.Data.MetadataLanguage = next;
        ApplyMetadataLanguage();

        bool hadFilter = !_tagFilter.IsEmpty || !_releaseTagFilter.IsEmpty;
        _tagFilter.Clear();
        _releaseTagFilter.Clear();
        _releases.Language = next;
        _releases.RefreshFilter();
        UpdateTagsButton();
        // Switching to English fills in English tags for cached releases (one page per day).
        if (_showingReleases && next == MetadataLanguage.English) _ = FetchReleasesAsync(refresh: false);

        SaveData();
        RefreshView(resetScroll: true);
        SetStatus($"Metadata language: {MetadataLanguageLabels.Label(next)}" +
            (hadFilter ? " \u00b7 tag filter cleared" : ""));
    }

    private void ApplyMetadataLanguage()
    {
        foreach (var game in _games)
            game.MetadataLanguage = _store.Data.MetadataLanguage;
    }

    private void ToggleFavoritesFilter()
    {
        _store.Data.FavoritesOnly = !_store.Data.FavoritesOnly;
        _favoritesButton.Checked = _store.Data.FavoritesOnly;
        SaveData();
        RefreshView(resetScroll: true);
        SetStatus(_store.Data.FavoritesOnly ? "Showing favorites only" : "Showing all games");
    }

    private void ShowMoreMenu()
    {
        var menu = Menus.Create();
        menu.Items.Add(Menus.Item("Rescan for games", (_, _) => _ = RefreshGamesAsync()));
        menu.Items.Add(Menus.Item("Add directory\u2026", (_, _) => _ = AddDirectoryFromDialog()));
        menu.Items.Add(Menus.Item("Open game folder", (_, _) => OpenFolder(_root)));
        menu.Items.Add(Menus.Separator());
        menu.Items.Add(BuildPageSizeMenu(menu));
        var connection = _store.Data.DlsiteProxy;
        string connectionLabel = connection is null || !connection.IsSet ? ""
            : connection.HasProxy ? ": proxy"
            : connection.Adapter == DlsiteConnection.AnyVpnAdapter ? ": VPN" : ": " + connection.Adapter;
        menu.Items.Add(Menus.Item($"DLsite connection{connectionLabel}\u2026", (_, _) => ShowProxyDialog()));
        // The games are Windows programs; elsewhere they run through Wine or a Proton wrapper.
        if (!OperatingSystem.IsWindows())
            menu.Items.Add(Menus.Item($"Wine command: {Shell.WineCommand}\u2026", (_, _) => ChooseWineCommand()));
        menu.Items.Add(Menus.Item($"Language: {MetadataLanguageLabels.Label(_store.Data.MetadataLanguage)}",
            (_, _) => ToggleMetadataLanguage()));
        menu.Items.Add(BuildStyleMenu(menu));
        menu.Items.Add(BuildCardShapeMenu(menu));
        menu.Items.Add(BuildCardPictureMenu(menu));
        menu.Items.Add(Menus.Separator());
        menu.Items.Add(Menus.Item("Clear all ratings", (_, _) => ClearAllRatings()));
        menu.Items.Add(Menus.Item("About RPG Launcher", (_, _) => ShowAbout()));

        Menus.Show(menu, _moreButton, new Point(0, _moreButton.Bounds.Height + 4));
    }

    private async void ChooseWineCommand()
    {
        var dialog = new TextPromptDialog("Wine command",
            "Command that runs the games (for example wine, or flatpak run org.winehq.Wine):", Shell.WineCommand, "Save");
        if (!await dialog.ShowAsync(this)) return;
        string command = dialog.Value.Length == 0 ? "wine" : dialog.Value;
        Shell.WineCommand = command;
        _store.Data.WineCommand = command == "wine" ? null : command;
        SaveData();
        SetStatus($"Games now start with {command}");
    }

    private async Task AddDirectoryFromDialog()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder containing RPG Maker games. Games in the folder and its subfolders will be added.",
            AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_root)
        });
        string? picked = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (picked is null) return;

        string? directory = NormalizeDirectory(picked);
        if (directory is null)
        {
            SetStatus("That folder path is not valid");
            return;
        }
        if (ContainsLibraryDirectory(directory))
        {
            SetStatus("That folder is already in the library");
            return;
        }

        if (!AddGameDirectory(directory)) return;

        // A running scan uses the old folder list: wait for it, then rescan.
        if (_loading)
        {
            SetStatus("Folder added \u00b7 waiting for the current scan\u2026", transient: false);
            while (_loading && !_closed) await Task.Delay(120);
            if (_closed) return;
        }

        int before = _games.Count;
        await RefreshGamesAsync();
        if (_closed) return;

        int added = Math.Max(0, _games.Count - before);
        string name = Path.GetFileName(directory);
        if (string.IsNullOrWhiteSpace(name)) name = directory;

        SetStatus(added switch
        {
            0 => $"Folder added \u00b7 no new games found in {name}",
            1 => $"Folder added \u00b7 added 1 game from {name}",
            _ => $"Folder added \u00b7 added {added} games from {name}"
        });
    }

    private async void ClearAllRatings()
    {
        if (_games.Count == 0) return;
        bool clear = await MessageDialog.ShowAsync(this, "Clear every rating in this collection?", "RPG Launcher",
            MessageButtons.YesNo, defaultNo: true);
        if (!clear) return;

        foreach (var game in _games)
        {
            game.Rating = 0;
            if (_store.Data.Games.TryGetValue(game.Key, out var record)) record.Rating = 0;
            _grid.RefreshCard(game);
        }
        SaveData();
        RefreshView();
        SetStatus("All ratings cleared");
    }

    private async void ShowAbout()
    {
        string locations = string.Join("\n  ", new[] { _root }.Concat(ExtraGameDirectories()));
        string message =
            "RPG Launcher\n\n" +
            "A cover-flow library for RPG Maker, Wolf RPG Editor, Ren'Py, TyranoBuilder and Unity games.\n\n" +
            $"\u2022 {_games.Count} games detected in:\n  {locations}\n\n" +
            "\u2022 Click a title screen to launch the game\n" +
            "\u2022 Click the stars to rate a game (1\u20135)\n" +
            "\u2022 Click the heart to add a favorite\n" +
            "\u2022 Right-click a card for more options\n" +
            "\u2022 Press F5 to rescan\n" +
            "\u2022 Use \u22ef \u2192 Add directory\u2026 for games in another folder\n\n" +
            $"Ratings are stored in launcher-data.json\nVersion {Version}";

        await MessageDialog.ShowAsync(this, message, "About RPG Launcher");
    }

    private static string Version =>
        (typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+')[0];

    private void ToggleFavorite(GameEntry game)
    {
        game.Favorite = !game.Favorite;
        if (_store.Data.Games.TryGetValue(game.Key, out var record)) record.Favorite = game.Favorite;
        SaveData();
        UpdateHeaderSubtitle();
        _grid.RefreshCard(game);

        if (_store.Data.FavoritesOnly) RefreshView();
        SetStatus(game.Favorite
            ? $"Added {game.CardTitle} to favorites"
            : $"Removed {game.CardTitle} from favorites");
    }

    private void ChangeRating(GameEntry game, int rating)
    {
        rating = Math.Clamp(rating, 0, 5);
        game.Rating = rating;
        if (_store.Data.Games.TryGetValue(game.Key, out var record)) record.Rating = rating;
        SaveData();
        _grid.RefreshCard(game);

        if (_store.Data.Sort is SortMode.RatingAsc or SortMode.RatingDesc) RefreshView();
        SetStatus(rating == 0
            ? $"Cleared rating for {game.CardTitle}"
            : $"Rated {game.CardTitle}  {rating} \u2605");
    }

    private void PlayGame(GameEntry game)
    {
        // A cheat script left behind by a launcher that didn't close normally.
        if (game.Family == EngineFamily.MvMz && game.DataRoot is { } root && !_cheatLaunches.ContainsKey(game.Key))
        {
            try { CheatBridge.Uninstall(CheatBridge.PluginsFile(root)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* it expires on its own */ }
        }
        StartGame(game, null, out _);
    }

    /// <summary>
    /// MV/MZ: starts the game with the cheat script and opens the cheat window (or shows the open one).
    /// Other RPG Maker games start in test mode, which has the engine's own noclip and debug menu.
    /// </summary>
    private void PlayWithCheats(GameEntry game)
    {
        if (game.Family != EngineFamily.MvMz && !EngineFamilies.HasTestMode(game.Family)) return;
        if (game.Family != EngineFamily.MvMz)
        {
            string arguments = game.Family switch
            {
                EngineFamily.Xp => "debug",
                EngineFamily.Rm2k => "TestPlay Window",
                _ => "test"
            };
            if (StartGame(game, arguments, out _))
                SetStatus($"{game.CardTitle} is in test mode \u00b7 hold Ctrl to walk through walls \u00b7 F9 opens the debug menu (switches and variables)");
            return;
        }

        if (_cheatLaunches.TryGetValue(game.Key, out var running))
        {
            ShowCheatWindow(game, running);
            return;
        }
        if (game.IsRunning)
        {
            _ = MessageDialog.ShowAsync(this,
                $"{game.CardTitle} is already running.\n\nClose it first: cheats are added when the launcher starts the game.",
                "RPG Launcher");
            return;
        }

        string? plugins = game.DataRoot is { } root ? CheatBridge.PluginsFile(root) : null;
        if (plugins is null || !File.Exists(plugins))
        {
            _ = MessageDialog.ShowAsync(this, $"Cheats need the game's js/plugins.js, which {game.CardTitle} doesn't have.",
                "RPG Launcher");
            return;
        }

        var launch = new CheatLaunch(CheatBridge.FreePort(), CheatBridge.NewToken(), plugins);
        try
        {
            CheatBridge.Install(plugins, launch.Port, launch.Secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = MessageDialog.ShowAsync(this, $"Couldn't add cheats to {game.CardTitle}: its js/plugins.js can't be changed.\n\n{ex.Message}",
                "RPG Launcher");
            return;
        }

        if (!StartGame(game, null, out var process))
        {
            RemoveCheatScript(launch);
            return;
        }
        launch.Process = process;
        _cheatLaunches[game.Key] = launch;
        game.CheatsOn = true;
        _grid.RefreshCard(game);
        ShowCheatWindow(game, launch);
    }

    private void ShowCheatWindow(GameEntry game, CheatLaunch launch)
    {
        if (launch.Window is { } open)
        {
            if (open.WindowState == Avalonia.Controls.WindowState.Minimized) open.WindowState = Avalonia.Controls.WindowState.Normal;
            open.Activate();
            return;
        }
        var window = new CheatWindow(game.CardTitle, launch.Port, launch.Secret, Icon, () => RemoveCheatScript(launch));
        window.Closed += (_, _) => { if (launch.Window == window) launch.Window = null; };
        launch.Window = window;
        window.Show();
    }

    /// <summary>Takes the cheat script out of plugins.js once the game has loaded it (or never will).</summary>
    private static void RemoveCheatScript(CheatLaunch launch)
    {
        try
        {
            CheatBridge.Uninstall(launch.PluginsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It does nothing after a few minutes, and the next Play removes it.
            Diagnostics.Log("Cheat script not removed: " + ex.Message);
        }
    }

    /// <summary>Starts the game and tracks it; false if it couldn't be started.</summary>
    private bool StartGame(GameEntry game, string? arguments, out Process? started)
    {
        started = null;
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
        {
            _ = MessageDialog.ShowAsync(this,
                $"No program to start was found for:\n{game.CardTitle}\n\nin:\n{game.RootPath}",
                "RPG Launcher");
            return false;
        }

        try
        {
            // On Linux a Ren'Py game starts with its own Linux build when it has one.
            string program = (game.Family == EngineFamily.RenPy ? Shell.RenPyLinuxLauncher(game.ExecutablePath) : null) ?? game.ExecutablePath;
            var process = Process.Start(Shell.GameStartInfo(program, game.RootPath, arguments));
            started = process;

            game.PlayCount++;
            game.LastPlayedUtc = DateTime.UtcNow;
            if (_store.Data.Games.TryGetValue(game.Key, out var record))
            {
                record.PlayCount++;
                record.LastPlayedUtc = game.LastPlayedUtc;
            }
            SaveData();

            SetStatus($"Launching {game.CardTitle}\u2026");

            if (process is not null)
            {
                _processes.Add(process);
                game.RunningCount++;
                _running[game.Key] = game.RunningCount;
                _grid.RefreshCard(game);

                var stopwatch = Stopwatch.StartNew();
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    long seconds = (long)stopwatch.Elapsed.TotalSeconds;
                    try
                    {
                        Dispatcher.UIThread.Post(() => OnGameExited(game.Key, process, seconds));
                    }
                    catch
                    {
                        // window already closed
                    }
                };
            }

            if (_store.Data.Sort is SortMode.RecentlyPlayed or SortMode.MostPlayed)
                RefreshView();
            return true;
        }
        catch (Exception ex)
        {
            _ = MessageDialog.ShowAsync(this,
                $"Could not start the game:\n{ex.Message}\n\n{game.ExecutablePath}",
                "RPG Launcher");
            return false;
        }
    }

    private void OnGameExited(string key, Process process, long seconds)
    {
        _processes.Remove(process);
        if (_cheatLaunches.TryGetValue(key, out var launch) && (launch.Process is null || launch.Process == process))
        {
            _cheatLaunches.Remove(key);
            RemoveCheatScript(launch);
            launch.Window?.Close();
        }

        if (_store.Data.Games.TryGetValue(key, out var record))
            record.PlaySeconds += Math.Max(0, seconds);

        var game = _games.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
        if (game is not null)
        {
            game.PlaySeconds += Math.Max(0, seconds);
            game.RunningCount = Math.Max(0, game.RunningCount - 1);
            _running[key] = game.RunningCount;
            game.CheatsOn = _cheatLaunches.ContainsKey(key);
            _grid.RefreshCard(game);
            SetStatus($"{game.CardTitle} closed \u00b7 played {Look.FormatDuration(Math.Max(1, seconds))}");
            if (_store.Data.Sort is SortMode.RecentlyPlayed or SortMode.MostPlayed) RefreshView();
        }

        SaveData();
    }

    private void OpenFolder(string path)
    {
        try
        {
            Shell.OpenFolder(path);
        }
        catch (Exception ex)
        {
            SetStatus("Could not open folder: " + ex.Message);
        }
    }

    /// <summary>Schedules a debounced save; the file is written off the UI thread.</summary>
    private void SaveData()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task FlushSaveAsync()
    {
        string json;
        long version;
        try
        {
            (json, version) = _store.Snapshot();
        }
        catch
        {
            SetStatus("Could not save launcher-data.json next to the launcher", transient: false);
            return;
        }

        bool saved = await Task.Run(() => _store.Write(json, version));
        if (!saved && !_closed)
            SetStatus("Could not save launcher-data.json next to the launcher", transient: false);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            RefreshCurrentView();
        }
        else if (control && e.Key is Key.PageDown or Key.PageUp && _pageCount > 1 && !_showingReleases)
        {
            e.Handled = true;
            GoToPage(_page + (e.Key == Key.PageDown ? 1 : -1));
        }
        else if (control && e.Key == Key.F)
        {
            e.Handled = true;
            _search.Focus();
        }
        else if (e.Key == Key.Escape && _search.TextValue.Length > 0)
        {
            e.Handled = true;
            _search.TextValue = "";
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _saveTimer.Stop();
        try
        {
            // The outer bounds in screen pixels while not maximized, as earlier versions saved them.
            RememberNormalBounds();
            _store.Data.Window = new WindowState
            {
                X = _normalPosition.X,
                Y = _normalPosition.Y,
                Width = _normalSize.Width,
                Height = _normalSize.Height,
                Maximized = WindowState == Avalonia.Controls.WindowState.Maximized,
                Valid = _normalSize.Width > 0
            };
            _store.Save();
        }
        catch
        {
            // closing should never be blocked by save problems
        }

        foreach (var launch in _cheatLaunches.Values)
        {
            RemoveCheatScript(launch);
            launch.Window?.Close();
        }

        _loadCts?.Cancel();
        _releaseCts?.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _statusTimer.Stop();
        _saveTimer.Stop();
        _searchTimer.Stop();
        _tagPopup?.Close();
        _tagPicker?.Close();
        foreach (var game in _games)
        {
            game.Thumbnail?.Dispose();
            game.FullThumbnail?.Dispose();
        }
        _games.Clear();
        base.OnClosed(e);
    }
}
