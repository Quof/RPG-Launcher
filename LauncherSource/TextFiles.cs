using System.Text;

namespace RpgLauncher;

/// <summary>Text in Japanese games' files, which may be UTF-8 or Shift-JIS.</summary>
internal static class TextFiles
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding ShiftJis;

    static TextFiles()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ShiftJis = Encoding.GetEncoding(932);
    }

    /// <summary>UTF-8 when the bytes are valid UTF-8, otherwise Shift-JIS (code page 932).</summary>
    public static string Decode(byte[] bytes)
    {
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return StrictUtf8.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return ShiftJis.GetString(bytes);
        }
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
