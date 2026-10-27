namespace RpgLauncher;

/// <summary>
/// RPG Maker MV/MZ stock title backgrounds (img/titles1), which say nothing about a game as a card picture.
/// A file is stock only when its name and exact size both match, so a game's own art under a stock name isn't.
/// </summary>
internal static class StockTitles
{
    // Unencrypted sizes in bytes (encrypted .rpgmvp/.png_ copies add a 16-byte header);
    // several per name for MV, MZ and re-saved releases.
    private static readonly Dictionary<string, int[]> Sizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Book"] = new[] { 144369, 663901 },
        ["Castle"] = new[] { 222190, 605475, 1049553, 1344848 },
        ["CrossedSwords"] = new[] { 215801, 982154 },
        ["Crystal"] = new[] { 205356, 1099718 },
        ["DemonCastle"] = new[] { 236190, 1080504 },
        ["Devil"] = new[] { 131261, 497443 },
        ["Dragon"] = new[] { 279406, 1281399 },
        ["Fountain"] = new[] { 240385, 1213125 },
        ["Gates"] = new[] { 270380, 1228280 },
        ["Hexagram"] = new[] { 302289, 1107863 },
        ["Island"] = new[] { 180968, 917043 },
        ["Night"] = new[] { 204687, 206775, 1131768 },
        ["Plain"] = new[] { 224920, 1119895 },
        ["Sword"] = new[] { 210662, 307491, 1201782 },
        ["Tower1"] = new[] { 189911, 999751 },
        ["Tower2"] = new[] { 242036, 1251185 },
        ["Universe"] = new[] { 199462, 205172, 1092271 },
        ["Volcano"] = new[] { 211820, 1002375 },
        ["World"] = new[] { 239227, 1210586 },
        ["WorldMap"] = new[] { 270275, 1090428 },
        ["Beach"] = new[] { 178400 },
        ["Bigtree"] = new[] { 213921 },
        ["Canyon"] = new[] { 246828 },
        ["FlyingIsland"] = new[] { 218434 },
        ["Gate"] = new[] { 240220 },
        ["Gold"] = new[] { 306648 },
        ["Jungle"] = new[] { 354968 },
        ["Mansion"] = new[] { 294352 },
        ["Monument"] = new[] { 237441 },
        ["Mountain"] = new[] { 168240 },
        ["Oasis"] = new[] { 193867 },
        ["Ruins"] = new[] { 231889 },
        ["Sky"] = new[] { 203187 },
        ["Snow"] = new[] { 303697 },
        ["Town1"] = new[] { 211972 },
        ["Town2"] = new[] { 268491 },
        ["Wasteland"] = new[] { 196254 },
    };

    private const int EncryptionHeader = 16;

    // Sizes of RPG Maker's default icon/icon.png, which most games ship unchanged.
    private static readonly long[] IconSizes = { 72778, 4931 };

    /// <summary>True when the icon at <paramref name="path"/> is RPG Maker's default one.</summary>
    public static bool IsStockIcon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            return IconSizes.Contains(new FileInfo(path).Length);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the file at <paramref name="path"/> is one of the stock title backgrounds.</summary>
    public static bool IsStock(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        string file = Path.GetFileName(path);
        bool encrypted = file.EndsWith(".rpgmvp", StringComparison.OrdinalIgnoreCase) ||
                         file.EndsWith(".png_", StringComparison.OrdinalIgnoreCase);
        string name = Path.GetFileNameWithoutExtension(file);
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (!Sizes.TryGetValue(name, out var sizes)) return false;

        try
        {
            long length = new FileInfo(path).Length - (encrypted ? EncryptionHeader : 0);
            return sizes.Contains((int)length);
        }
        catch
        {
            return false;
        }
    }
}
