namespace RpgLauncher;

/// <summary>Look of the whole launcher, chosen in the ⋯ menu.</summary>
internal enum UiStyle
{
    Default,
    Dark,
    Angelic,
    Blob
}

internal static class UiStyleLabels
{
    public static string Label(UiStyle style) => style switch
    {
        UiStyle.Dark => "Dark mode",
        UiStyle.Angelic => "Angelic Serenade",
        UiStyle.Blob => "Blob",
        _ => "Default"
    };
}

/// <summary>Every color and shape setting a style controls.</summary>
internal sealed record Palette
{
    public required Color Bg { get; init; }
    public required Color HeaderTop { get; init; }
    public required Color Card { get; init; }
    public required Color CardHover { get; init; }
    public required Color CardBorder { get; init; }
    public required Color CardBorderHover { get; init; }
    public required Color Field { get; init; }
    public required Color FieldHover { get; init; }
    public required Color FieldBorder { get; init; }
    public required Color MenuBg { get; init; }
    public required Color Text { get; init; }
    public required Color TextMuted { get; init; }
    public required Color TextDim { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentBright { get; init; }
    public required Color AccentDim { get; init; }
    public required Color AccentBorder { get; init; }
    public required Color AccentHover { get; init; }
    public required Color Star { get; init; }
    public required Color StarBright { get; init; }
    public required Color StarEmpty { get; init; }
    public required Color Success { get; init; }
    public required Color Danger { get; init; }
    public required Color DangerDim { get; init; }
    public required Color DangerBorder { get; init; }
    public required Color StatusBg { get; init; }
    public required Color Divider { get; init; }
    public required Color HeaderGlow { get; init; }
    public required Color ScrollTrack { get; init; }
    public required Color ScrollThumb { get; init; }
    public required Color ScrollThumbActive { get; init; }
    public required Color PlaceholderTop { get; init; }
    public required Color PlaceholderBottom { get; init; }
    public required Color Canvas { get; init; }
    public required Color TitleBar { get; init; }
    public required Color TitleBorder { get; init; }
    public bool Light { get; init; }
    public double BorderWidth { get; init; } = 1;
    public double CardRadius { get; init; } = 14;
    public double ControlRadius { get; init; }

    public static Palette For(UiStyle style) => style switch
    {
        UiStyle.Dark => Dark,
        UiStyle.Angelic => Angelic,
        UiStyle.Blob => Blob,
        _ => Default
    };

    private static Color C(int r, int g, int b) => Color.FromRgb((byte)r, (byte)g, (byte)b);
    private static Color C(int a, int r, int g, int b) => Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b);
    private static Color Hex(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Deep navy with gold.</summary>
    public static readonly Palette Default = new()
    {
        Bg = C(13, 13, 18),
        HeaderTop = C(22, 21, 32),
        Card = C(25, 25, 35),
        CardHover = C(33, 33, 47),
        CardBorder = C(42, 42, 58),
        CardBorderHover = C(92, 84, 138),
        Field = C(30, 30, 42),
        FieldHover = C(37, 37, 52),
        FieldBorder = C(49, 49, 65),
        MenuBg = C(27, 27, 38),
        Text = C(240, 240, 247),
        TextMuted = C(158, 158, 176),
        TextDim = C(112, 112, 132),
        Accent = C(240, 197, 94),
        AccentBright = C(253, 222, 143),
        AccentDim = C(49, 43, 27),
        AccentBorder = C(110, 90, 44),
        AccentHover = C(61, 53, 31),
        Star = C(240, 197, 94),
        StarBright = C(253, 222, 143),
        StarEmpty = C(63, 63, 82),
        Success = C(104, 211, 145),
        Danger = C(240, 120, 120),
        DangerDim = C(58, 24, 30),
        DangerBorder = C(140, 56, 64),
        StatusBg = C(11, 11, 16),
        Divider = C(38, 38, 53),
        HeaderGlow = C(13, 240, 197, 94),
        ScrollTrack = C(22, 255, 255, 255),
        ScrollThumb = C(80, 200, 200, 225),
        ScrollThumbActive = C(135, 236, 236, 250),
        PlaceholderTop = C(52, 43, 77),
        PlaceholderBottom = C(23, 23, 33),
        Canvas = C(8, 8, 12),
        TitleBar = C(18, 18, 26),
        TitleBorder = C(42, 42, 58),
        CardRadius = 14f,
        ControlRadius = 0f
    };

    /// <summary>Neutral greys with a blue accent.</summary>
    public static readonly Palette Dark = new()
    {
        Bg = Hex(0x0e0e0f),
        HeaderTop = Hex(0x171718),
        Card = Hex(0x1a1a1b),
        CardHover = Hex(0x222224),
        CardBorder = Hex(0x2a2a2c),
        CardBorderHover = Hex(0x4a4a4e),
        Field = Hex(0x1c1c1e),
        FieldHover = Hex(0x262628),
        FieldBorder = Hex(0x343437),
        MenuBg = Hex(0x1c1c1e),
        Text = Hex(0xececec),
        TextMuted = Hex(0xa3a3a6),
        TextDim = Hex(0x6f6f73),
        Accent = Hex(0x5aa2f7),
        AccentBright = Hex(0x92c2ff),
        AccentDim = Hex(0x16263a),
        AccentBorder = Hex(0x2d5687),
        AccentHover = Hex(0x1c3150),
        Star = Hex(0xf0c55e),
        StarBright = Hex(0xffdf8e),
        StarEmpty = Hex(0x3a3a3d),
        Success = Hex(0x5cc98a),
        Danger = Hex(0xef7070),
        DangerDim = Hex(0x3a1c1e),
        DangerBorder = Hex(0x8a3a3e),
        StatusBg = Hex(0x0b0b0c),
        Divider = Hex(0x29292b),
        HeaderGlow = C(0, 0, 0, 0),
        ScrollTrack = C(20, 255, 255, 255),
        ScrollThumb = C(70, 220, 220, 220),
        ScrollThumbActive = C(130, 240, 240, 240),
        PlaceholderTop = Hex(0x2a2a2e),
        PlaceholderBottom = Hex(0x18181a),
        Canvas = Hex(0x080808),
        TitleBar = Hex(0x141415),
        TitleBorder = Hex(0x2a2a2c),
        CardRadius = 10f,
        ControlRadius = 6f
    };

    /// <summary>Navy on near-black, sky-blue accent, square panels.</summary>
    public static readonly Palette Angelic = new()
    {
        Bg = Hex(0x051523),
        HeaderTop = Hex(0x0d2741),
        Card = Hex(0x071c30),
        CardHover = Hex(0x0d2741),
        CardBorder = Hex(0x17375a),
        CardBorderHover = Hex(0x3355aa),
        Field = Hex(0x001122),
        FieldHover = Hex(0x0d2741),
        FieldBorder = Hex(0x225588),
        MenuBg = Hex(0x071c30),
        Text = Hex(0xdddddd),
        TextMuted = Hex(0x9eaebd),
        TextDim = Hex(0x5a7fa8),
        Accent = Hex(0x77bbdd),
        AccentBright = Hex(0xa8d8f0),
        AccentDim = Hex(0x0f2d4d),
        AccentBorder = Hex(0x3355aa),
        AccentHover = Hex(0x153a63),
        Star = Hex(0x77bbdd),
        StarBright = Hex(0xb5e2f7),
        StarEmpty = Hex(0x1d3a58),
        Success = Hex(0x33cc66),
        Danger = Hex(0xee4444),
        DangerDim = Hex(0x351420),
        DangerBorder = Hex(0x993344),
        StatusBg = Hex(0x112244),
        Divider = Hex(0x225588),
        HeaderGlow = C(18, 119, 187, 221),
        ScrollTrack = C(30, 119, 187, 221),
        ScrollThumb = C(110, 51, 85, 170),
        ScrollThumbActive = C(170, 119, 187, 221),
        PlaceholderTop = Hex(0x0d2741),
        PlaceholderBottom = Hex(0x051523),
        Canvas = Hex(0x000000),
        TitleBar = Hex(0x112244),
        TitleBorder = Hex(0x225588),
        CardRadius = 2f,
        ControlRadius = 0f
    };

    /// <summary>Light colors from the blob drawings: cream, white cards with brown outlines, periwinkle accent.</summary>
    public static readonly Palette Blob = new()
    {
        Bg = Hex(0xfff7ec),
        HeaderTop = Hex(0xffe3cc),
        Card = Hex(0xffffff),
        CardHover = Hex(0xfff3e6),
        CardBorder = Hex(0x6b4535),
        CardBorderHover = Hex(0x8a5a8e),
        Field = Hex(0xfffdf9),
        FieldHover = Hex(0xfff0e2),
        FieldBorder = Hex(0xb98c74),
        MenuBg = Hex(0xfffaf3),
        Text = Hex(0x4a2c20),
        TextMuted = Hex(0x7a5546),
        TextDim = Hex(0xa98574),
        Accent = Hex(0x6278cf),
        AccentBright = Hex(0x4a5fb8),
        AccentDim = Hex(0xdfe5fb),
        AccentBorder = Hex(0x93a6ea),
        AccentHover = Hex(0xd0d9fa),
        Star = Hex(0xf0b429),
        StarBright = Hex(0xf7c84d),
        StarEmpty = Hex(0xeadbd0),
        Success = Hex(0x3d9a5a),
        Danger = Hex(0xb8433b),
        DangerDim = Hex(0xfbe2dd),
        DangerBorder = Hex(0xe3a097),
        StatusBg = Hex(0xffeedd),
        Divider = Hex(0xe9d3c1),
        HeaderGlow = C(40, 255, 182, 193),
        ScrollTrack = C(40, 107, 69, 53),
        ScrollThumb = C(110, 185, 140, 116),
        ScrollThumbActive = C(170, 107, 69, 53),
        PlaceholderTop = Hex(0xffe9d6),
        PlaceholderBottom = Hex(0xf6d2b8),
        Canvas = Hex(0xfff1e3),
        TitleBar = Hex(0xffe3cc),
        TitleBorder = Hex(0x6b4535),
        Light = true,
        BorderWidth = 2f,
        CardRadius = 20f,
        ControlRadius = 12f
    };
}
