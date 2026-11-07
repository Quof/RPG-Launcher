using System.Text;

namespace RpgLauncher;

/// <summary>Text in games' files, which may be UTF-8, Shift-JIS or (games made outside Japan) Western European.</summary>
internal static class TextFiles
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding ShiftJis;

    static TextFiles()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ShiftJis = Encoding.GetEncoding(932);
        StrictShiftJis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static readonly Encoding StrictShiftJis;

    /// <summary>
    /// UTF-8 when the bytes are valid UTF-8; otherwise Shift-JIS, unless they make sense only as Western European
    /// text (Windows-1252: é, ó, ñ, ü…), as games made outside Japan have.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return StrictUtf8.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return Guess(bytes, Western).GetString(bytes);
        }
    }

    /// <summary>Windows' Western European code page, for text files; zip names use the DOS one (437) instead.</summary>
    public static Encoding Western => Encoding.GetEncoding(1252);

    // Latin letters around a single kanji, kana or full-width sign: what accented Western words become when read as
    // Shift-JIS ("Pokémon", "Canción"), never Japanese.
    private static readonly System.Text.RegularExpressions.Regex MisreadLatin =
        new(@"[A-Za-z][\u3000-\u9FFF\uFF00-\uFFEF][A-Za-z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// For bytes that aren't UTF-8: Shift-JIS, unless they read as Shift-JIS only as nonsense (broken sequences,
    /// user-defined characters, a lone kanji between Latin letters) and cleanly as <paramref name="western"/>.
    /// </summary>
    public static Encoding Guess(byte[] bytes, Encoding western)
    {
        string? japanese;
        try { japanese = StrictShiftJis.GetString(bytes); }
        catch (DecoderFallbackException) { japanese = null; }
        bool plausible = japanese is not null && !japanese.Any(c => c is >= '\uE000' and <= '\uF8FF') && !MisreadLatin.IsMatch(japanese);
        if (plausible) return ShiftJis;
        var strictWestern = Encoding.GetEncoding(western.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try
        {
            string latin = strictWestern.GetString(bytes);
            // Accented letters, not just symbols: a Japanese file read this way is mostly ‚ƒ„ and the like.
            if (latin.Any(c => c is >= '\u00C0' and <= '\u017F')) return western;
        }
        catch (DecoderFallbackException)
        {
        }
        return ShiftJis;
    }

    /// <summary>The value of <paramref name="key"/>= in an .ini file (any section), or null.</summary>
    public static string? IniValue(string path, string key)
    {
        try
        {
            if (!File.Exists(path)) return null;
            foreach (var raw in Decode(File.ReadAllBytes(path)).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length > key.Length && line[key.Length] == '=' &&
                    line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[(key.Length + 1)..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }
        }
        catch
        {
            // unreadable: treated as missing
        }
        return null;
    }
}
