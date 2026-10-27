using System.Text.Json.Serialization;

namespace RpgLauncher;

internal enum SortMode
{
    TitleAsc,
    TitleDesc,
    RatingDesc,
    RatingAsc,
    RecentlyPlayed,
    MostPlayed,
    // Shown as "Recently Downloaded" (folder creation date); not renamed because sort modes are saved by name.
    RecentlyAdded,
    FolderName,
    ReleaseNewest,
    ReleaseOldest
}

internal static class SortLabels
{
    public static string Label(SortMode mode) => mode switch
    {
        SortMode.TitleAsc => "Title  A\u2192Z",
        SortMode.TitleDesc => "Title  Z\u2192A",
        SortMode.RatingDesc => "Rating  \u2193",
        SortMode.RatingAsc => "Rating  \u2191",
        SortMode.RecentlyPlayed => "Last Played",
        SortMode.MostPlayed => "Most Played",
        SortMode.RecentlyAdded => "Recently Downloaded",
        SortMode.FolderName => "Folder Name",
        SortMode.ReleaseNewest => "Released  ↓",
        SortMode.ReleaseOldest => "Released  ↑",
        _ => "Title"
    };
}

/// <summary>A game discovered on disk.</summary>
internal sealed class GameEntry
{
    public required string Key { get; set; }
    public required string RootPath { get; init; }
    public string FolderName => Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public string? ExecutablePath { get; set; }
    public string? DataRoot { get; set; }
    public string Title { get; set; } = "";
    public string? CustomTitle { get; set; }
    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle) ? Title : CustomTitle!;

    private string? _localName;
    private string? _localNameFrom;

    /// <summary>The title without versions or edition notes, or the folder name if it is a placeholder like "Project1".</summary>
    [JsonIgnore]
    public string LocalName
    {
        get
        {
            if (_localName is null || !ReferenceEquals(_localNameFrom, Title))
            {
                _localName = GameTitles.LocalName(Title, FolderName);
                _localNameFrom = Title;
            }
            return _localName;
        }
    }
    public string Engine { get; set; } = "RPG Maker";
    public EngineFamily Family => EngineFamilies.Of(Engine);
    public string? Locale { get; set; }
    public string? Title1Path { get; set; }
    public string? Title2Path { get; set; }
    public string? IconPath { get; set; }
    public string? EncryptionKey { get; set; }
    public string? LoadError { get; set; }

    // User data (merged from launcher-data.json)
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public int Rating { get; set; }
    public bool Favorite { get; set; }
    public int PlayCount { get; set; }
    public long PlaySeconds { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public GameMetadata? Metadata { get; set; }
    public bool MetadataChecked { get; set; }
    public bool MetadataEnglishChecked { get; set; }

    /// <summary>User groups this game belongs to (right-click \u2192 Groups).</summary>
    [JsonIgnore] public List<string> Groups { get; set; } = new();

    /// <summary>Folder creation time, roughly when the game was downloaded or unpacked.</summary>
    public DateTime? FolderCreatedUtc { get; set; }

    /// <summary>Full path of the user-chosen card image, or null.</summary>
    public string? CustomImagePath { get; set; }

    /// <summary>User-adjusted crop of the card image, or null for the default.</summary>
    public ImageFraming? Framing { get; set; }

    /// <summary>The DLsite main image, once downloaded into launcher-images/dlsite.</summary>
    [JsonIgnore] public string? StoreImagePath { get; set; }

    /// <summary>User's pick for this card: true = DLsite image, false = title screen, null = follow the setting.</summary>
    [JsonIgnore] public bool? StoreImageChoice { get; set; }

    /// <summary>⋯ → Card pictures: cards show the DLsite image whenever the game has one.</summary>
    public static bool StoreImagesFirst { get; set; } = true;

    private bool? _titleIsStock;

    /// <summary>True when the title screen is one of RPG Maker's stock backgrounds (<see cref="StockTitles"/>).</summary>
    [JsonIgnore] public bool TitleIsStock => _titleIsStock ??= StockTitles.IsStock(Title1Path);

    /// <summary>Show the DLsite image instead of the title screen (user's pick, the setting, or no usable title picture).</summary>
    [JsonIgnore]
    public bool WantsStoreImage => CustomImagePath is null &&
        (Title1Path is null || (StoreImageChoice ?? (StoreImagesFirst || TitleIsStock)));

    [JsonIgnore] public bool UsesStoreImage => WantsStoreImage && StoreImagePath is not null;

    /// <summary>Show a placeholder rather than a stock title background, unless the user picked the title screen.</summary>
    [JsonIgnore]
    public bool HidesStockTitle => CustomImagePath is null && TitleIsStock && StoreImageChoice != false && !UsesStoreImage;

    /// <summary>True when the card shows a real picture (custom, store or title screen) that can be framed.</summary>
    public bool HasFramableImage => CustomImagePath is not null || UsesStoreImage || (Title1Path is not null && !HidesStockTitle);

    /// <summary>Sizes and times of the game's key files at the last scan (<see cref="GameScanner.Signature"/>).</summary>
    [JsonIgnore] public string? Signature { get; set; }

    // Runtime only
    [JsonIgnore] public Bitmap? Thumbnail { get; set; }
    [JsonIgnore] public int RunningCount { get; set; }
    /// <summary>Started with Play with Cheats and still running.</summary>
    [JsonIgnore] public bool CheatsOn { get; set; }
    [JsonIgnore] public bool ThumbnailRequested { get; set; }

    /// <summary>The whole card picture, uncropped; loaded only for square cards.</summary>
    [JsonIgnore] public Bitmap? FullThumbnail { get; set; }
    [JsonIgnore] public bool FullThumbnailRequested { get; set; }
    [JsonIgnore] public MetadataLanguage MetadataLanguage { get; set; } = MetadataLanguage.English;
    public bool IsRunning => RunningCount > 0;
    public string MetadataLine => Metadata?.ShortLineFor(MetadataLanguage) ?? "";

    public string CardTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomTitle)) return CustomTitle!;
            if (!string.IsNullOrWhiteSpace(Metadata?.TitleFor(MetadataLanguage)))
                return Metadata!.TitleFor(MetadataLanguage).Trim();
            return LocalName;
        }
    }

    /// <summary>Tags added by hand (right-click → Add Tags…), in whichever language they were picked.</summary>
    [JsonIgnore] public List<string> AddedTags { get; set; } = new();

    [JsonIgnore] public IReadOnlyList<string> StoreTags => Metadata?.TagsFor(MetadataLanguage) ?? Array.Empty<string>();

    /// <summary>DLsite tags plus hand-added ones, translated where <see cref="TagNames"/> knows the other name.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Tags =>
        AddedTags.Count == 0
            ? StoreTags
            : StoreTags.Concat(AddedTags.Select(tag => TagNames.For(tag, MetadataLanguage)))
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();

    public bool HasTag(string tag) =>
        Tags.Any(value => string.Equals(value, tag, StringComparison.CurrentCultureIgnoreCase));
}

internal sealed class GameRecord
{
    public string? CustomTitle { get; set; }
    public int Rating { get; set; }
    public bool Favorite { get; set; }
    public int PlayCount { get; set; }
    public long PlaySeconds { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public GameMetadata? Metadata { get; set; }
    public bool MetadataChecked { get; set; }
    public bool MetadataEnglishChecked { get; set; }

    /// <summary>The store search (<see cref="MetadataService.LookupRevision"/>) last used for this game.</summary>
    public int LookupRevision { get; set; }

    /// <summary>File name of a user-chosen image inside the launcher-images folder.</summary>
    public string? CustomImage { get; set; }
    public ImageFraming? Framing { get; set; }

    /// <summary>User's card picture pick: true = DLsite image, false = title screen, null = automatic.</summary>
    public bool? UseStoreImage { get; set; }

    /// <summary>Names of the user groups this game is in; null when none.</summary>
    public List<string>? Groups { get; set; }

    /// <summary>Tags the user added (right-click → Add Tags…); null when none.</summary>
    public List<string>? AddedTags { get; set; }
}

/// <summary>
/// Which part of a picture fills the 16:9 card: the largest 16:9 area that fits, shrunk by <see cref="Zoom"/>
/// and centered on (<see cref="CenterX"/>, <see cref="CenterY"/>) as fractions of the picture's width and height.
/// </summary>
internal sealed class ImageFraming
{
    public const double MaxZoom = 6.0;

    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.5;
    public double Zoom { get; set; } = 1.0;

    public bool IsDefault =>
        Math.Abs(Zoom - 1.0) < 0.001 && Math.Abs(CenterX - 0.5) < 0.0005 && Math.Abs(CenterY - 0.5) < 0.0005;

    public ImageFraming Clone() => new() { CenterX = CenterX, CenterY = CenterY, Zoom = Zoom };

    /// <summary>Stable text form used in thumbnail cache keys.</summary>
    public string Signature() => FormattableString.Invariant($"{CenterX:F4},{CenterY:F4},{Zoom:F4}");

    /// <summary>The source rectangle (in picture pixels) that this framing shows.</summary>
    public static RectangleF CropRect(SizeF image, ImageFraming? framing)
    {
        const float aspect = 16f / 9f;
        float baseW = image.Width, baseH = image.Width / aspect;
        if (baseH > image.Height)
        {
            baseH = image.Height;
            baseW = image.Height * aspect;
        }

        float zoom = (float)Math.Clamp(framing?.Zoom ?? 1.0, 1.0, MaxZoom);
        float w = baseW / zoom, h = baseH / zoom;
        float cx = (float)(framing?.CenterX ?? 0.5) * image.Width;
        float cy = (float)(framing?.CenterY ?? 0.5) * image.Height;
        float x = Math.Clamp(cx - w / 2f, 0f, Math.Max(0f, image.Width - w));
        float y = Math.Clamp(cy - h / 2f, 0f, Math.Max(0f, image.Height - h));
        return new RectangleF(x, y, w, h);
    }
}

/// <summary>Supported engines, grouped as the engine filter lists them.</summary>
internal enum EngineFamily
{
    MvMz,
    Vx,
    Xp,
    Rm2k,
    Wolf
}

internal static class EngineFamilies
{
    public static readonly EngineFamily[] All =
        { EngineFamily.MvMz, EngineFamily.Vx, EngineFamily.Xp, EngineFamily.Rm2k, EngineFamily.Wolf };

    public static string Label(EngineFamily family) => family switch
    {
        EngineFamily.Vx => "RPG Maker VX / VXA",
        EngineFamily.Xp => "RPG Maker XP",
        EngineFamily.Rm2k => "RPG Maker 2000/2003",
        EngineFamily.Wolf => "Wolf RPG Editor",
        _ => "RPG Maker MV / MZ"
    };

    /// <summary>The family of an engine name such as "RPG Maker VX Ace" (see GameScanner).</summary>
    public static EngineFamily Of(string engine) =>
        engine.Contains("Wolf", StringComparison.OrdinalIgnoreCase) ? EngineFamily.Wolf
        : engine.Contains("2000", StringComparison.Ordinal) || engine.Contains("2003", StringComparison.Ordinal) ? EngineFamily.Rm2k
        : engine.Contains("VX", StringComparison.OrdinalIgnoreCase) ? EngineFamily.Vx
        : engine.Contains("XP", StringComparison.OrdinalIgnoreCase) ? EngineFamily.Xp
        : EngineFamily.MvMz;
}

/// <summary>Preferred card picture when a game has both a DLsite image and a title screen.</summary>
internal enum CardPicture
{
    StoreImage,
    TitleScreen
}

/// <summary>Library card layout: wide cards with a cropped 16:9 picture, or square pictures shown whole.</summary>
internal enum CardShape
{
    Rectangular,
    Square
}

internal sealed class WindowState
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public bool Valid { get; set; }
}

internal sealed class LauncherData
{
    public int Version { get; set; } = 1;
    public SortMode Sort { get; set; } = SortMode.TitleAsc;
    public bool FavoritesOnly { get; set; }

    /// <summary>Games shown per page; 0 shows the whole library on one scrolling page.</summary>
    public int PageSize { get; set; }

    /// <summary>Shape of library cards (⋯ → Card shape).</summary>
    public CardShape CardShape { get; set; } = CardShape.Rectangular;

    /// <summary>Which picture cards show first (⋯ → Card pictures).</summary>
    public CardPicture CardPicture { get; set; } = CardPicture.StoreImage;

    /// <summary>Look of the launcher (⋯ → Style).</summary>
    public UiStyle Style { get; set; } = UiStyle.Default;

    /// <summary>User-made groups, in the order they were created.</summary>
    public List<string> Groups { get; set; } = new();

    /// <summary>The group the library is narrowed to, or null to show every game.</summary>
    public string? ActiveGroup { get; set; }

    /// <summary>The one engine the library is narrowed to, or null for every engine (sort menu).</summary>
    public EngineFamily? EngineFilter { get; set; }

    /// <summary>Shows only games without a DLsite page (sort menu → No DLsite Page).</summary>
    public bool NoStorePageOnly { get; set; }

    /// <summary>Tags pinned to the top of the Tags picker (library and new releases).</summary>
    public List<string> FavoriteTags { get; set; } = new();

    /// <summary>Optional proxy that every DLsite request goes through.</summary>
    public ProxySettings? DlsiteProxy { get; set; }
    public MetadataLanguage MetadataLanguage { get; set; } = MetadataLanguage.English;
    public WindowState Window { get; set; } = new();
    public List<string> GameDirectories { get; set; } = new();
    public Dictionary<string, GameRecord> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
