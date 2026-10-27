using System.Text;
using System.Text.RegularExpressions;

namespace RpgLauncher;

/// <summary>
/// Cleans the title a game gives itself (version numbers, edition notes like 製品版, key-binding lists) for cards
/// and store searches, falling back to the folder name when the title says nothing useful.
/// </summary>
internal static class GameTitles
{
    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Words naming an edition of the game rather than the game.
    private const string Editions =
        "製品版|体験版|完成版|開発用|修正版\\d*|最新版|正式版|無料版|試作版|デモ版|ベータ版|β版|α版|お試し版|DL版";

    private const string Decorations = "◆◇★☆■□●○♪※◎";

    // Bracketed notes: (製品版), 【ver1.04】, [Z: skip]. Matched on the Plain() copy, so full-width brackets count too.
    private static readonly Regex BracketRegex = new(
        @"\((?<in>[^()]*)\)|\[(?<in>[^\[\]]*)\]|【(?<in>[^【】]*)】|〔(?<in>[^〔〕]*)〕|《(?<in>[^《》]*)》|〈(?<in>[^〈〉]*)〉",
        Options);

    private static readonly Regex VersionOnlyRegex = new(
        @"^(?i:v(?:er(?:sion)?)?)?[\s._]*\d+(?:[._]\d+)*[a-z]?$",
        Options);

    private static readonly Regex EditionRegex = new(
        "(?:" + Editions + @")|(?<![A-Za-z])(?i:demo|trial)(?![A-Za-z])",
        Options);

    // Signs of a key-binding note: a key name, "T で…", "A:…", or a list separated by |.
    private static readonly Regex KeyNoteRegex = new(
        @"(?<![A-Za-z])(?i:F\d{1,2}|ctrl|shift|tab|esc|alt|enter|space|pageup|pagedown)(?![A-Za-z])" +
        @"|クリック|キー|スキップ|バックログ|全画面|再起動|ウィンドウ|ｳｨﾝﾄﾞｳ" +
        @"|(?<![A-Za-z])[A-Z]\s*(?:で|:)|\|",
        Options);

    private static readonly Regex StoreIdRegex = new(
        @"^(?i:RJ|VJ|BJ|RE)\d{6,8}$",
        Options);

    // Key bindings after the title: "…  F4:画面拡大 Ctrl:スキップ", "… 決定:Z ﾒﾆｭｰ:X".
    private static readonly Regex HintTailRegex = new(
        @"\s+(?<key>(?i:F\d{1,2}|ctrl|shift|tab|esc|alt|enter|space|pageup|pagedown)|決定|キャンセル|ｷｬﾝｾﾙ|メニュー|ﾒﾆｭｰ|右クリック|左クリック|[A-Za-z])[^\s:/]{0,10}[:/].*$",
        Options | RegexOptions.Singleline);

    // Key hints without colons after the title: "… スキップウィンドウ消去[A]バックログ".
    private static readonly Regex HintWordTailRegex = new(
        @"\s+\S*(?:スキップ|ウィンドウ消去|ウインドウ消去|バックログ|全画面|画面サイズ).*$",
        Options | RegexOptions.Singleline);

    // A bare version number at the very end: "Title 1.06".
    private static readonly Regex TrailingNumberRegex = new(@"\s+\d+\.\d{1,3}$", Options);

    private static readonly Regex HintPairRegex = new(
        @"(?:^|\s)[^\s:/]{1,12}[:/]",
        Options);

    // Ver1.07, ver.1.2, Version 1.5, TitleVer2.00, v1.00, v.1.00, and bare 0.6.1.7.
    private static readonly Regex VersionRegex = new(
        @"[\s_]*(?:(?<![A-Za-z])(?i:ver(?:sion)?)[\s._]*\d+(?:[._]\d+)*[a-z]?" +
        @"|(?<=[a-z])Ver[\s._]*\d+(?:\.\d+)+[a-z]?" +
        @"|(?<![A-Za-z])[vV]\.?\s?\d+(?:\.\d+)+[a-z]?" +
        @"|(?<![A-Za-z0-9.])\d+(?:\.\d+){2,})(?![A-Za-z0-9.])",
        Options);

    private static readonly Regex BuildRegex = new(
        @"[\s_\-]*(?<![A-Za-z])(?i:(?:alpha|beta)\s+)?(?i:build)\s*v?\.?\d.*$",
        Options | RegexOptions.Singleline);

    private static readonly Regex EditionWordRegex = new(
        @"[\s_]*[" + Decorations + "]*(?:" + Editions + ")[" + Decorations + "]*" +
        @"|[\s_]*(?<![A-Za-z])(?i:demo|trial)(?:\s+(?i:version|edition))?(?![A-Za-z])",
        Options);

    private static readonly Regex LoneGreekRegex = new(@"\s+[αβ]$", Options);

    private static readonly Regex EdgeDecorationRegex = new(
        "^[\\s" + Decorations + "]+|[\\s" + Decorations + "]+$",
        Options);

    private static readonly Regex ExtraSpaceRegex = new(@"(?<=\s)\s+", Options);

    // Separators left with nothing between them, e.g. "Title - -", "Title - - Windows" or "Title~~".
    private static readonly Regex EmptyPairRegex = new(@"\s*([\-–—~])\s*\1(?=\s|$)", Options);

    private static readonly Regex FolderNumberRegex = new(@"^(?:0\d*|\d{1,3}(?=[\s_.\-]))[\s_.\-]*", Options);

    // A plain x standing in for × between Japanese words.
    private static readonly Regex TimesRegex = new(
        @"(?<=[\u3040-\u30FF\u4E00-\u9FFF])[xX](?=[\u3040-\u30FF\u4E00-\u9FFF])",
        Options);

    // Words DLsite censors with 〇; its search only finds the censored form.
    private static readonly (string Word, string Shown)[] CensoredWords =
    {
        ("催眠", "催〇"), ("洗脳", "洗〇"), ("奴隷", "奴〇"), ("凌辱", "凌〇"), ("陵辱", "陵〇"),
        ("強姦", "強〇"), ("輪姦", "輪〇"), ("監禁", "監〇"), ("痴漢", "痴〇"), ("獣姦", "獣〇"),
        ("鬼畜", "鬼〇"), ("ロリ", "ロ〇")
    };

    private static readonly Regex GenericRegex = new(
        @"^(?:project|game|ゲーム|あたらしいゲーム|新しいゲーム|rpg|rpgmaker(?:mv|mz)?|untitled|newgame|test|sample|contents?|contens|本体|data|www|main|app)\d*$",
        Options);

    /// <summary>The title without version numbers, edition notes or key-binding lists.</summary>
    public static string Clean(string? raw)
    {
        string original = (raw ?? string.Empty).Trim();
        if (original.Length == 0) return original;
        string plain = Plain(original);

        // Notes in brackets; nested brackets are handled from the inside out.
        for (int pass = 0; pass < 3; pass++)
        {
            int before = plain.Length;
            Remove(ref original, ref plain, BracketRegex, m => IsNote(m.Groups["in"].Value));
            if (plain.Length == before) break;
        }

        var tail = HintTailRegex.Match(plain);
        if (tail.Success && (tail.Groups["key"].Length > 1 || HintPairRegex.Matches(tail.Value).Count >= 2))
        {
            original = original[..tail.Index];
            plain = plain[..tail.Index];
        }

        Remove(ref original, ref plain, HintWordTailRegex);
        Remove(ref original, ref plain, BuildRegex);
        Remove(ref original, ref plain, VersionRegex);
        Remove(ref original, ref plain, TrailingNumberRegex);
        Remove(ref original, ref plain, EditionWordRegex);
        Remove(ref original, ref plain, LoneGreekRegex);
        Remove(ref original, ref plain, EdgeDecorationRegex);
        Remove(ref original, ref plain, ExtraSpaceRegex);
        Remove(ref original, ref plain, EmptyPairRegex);
        TrimSeparators(ref original, ref plain);

        // Ideographic spaces read as gaps in a card title; store names use plain spaces.
        original = original.Replace('\u3000', ' ').Trim();
        return Key(original).Length > 0 ? original : (raw ?? string.Empty).Trim();
    }

    /// <summary>A folder name cleaned like a title, without a leading sort number such as "005".</summary>
    public static string CleanFolder(string? folder)
    {
        string name = FolderNumberRegex.Replace((folder ?? string.Empty).Trim(), string.Empty);
        return Clean(name.Replace('_', ' '));
    }

    /// <summary>
    /// The name to show when the store has none: the cleaned title, or the cleaned folder
    /// name when the title is a placeholder like "Project1" or is garbled.
    /// </summary>
    public static string LocalName(string title, string folder)
    {
        string name = Clean(title);
        if (Key(name).Length == 0 || IsGeneric(name) || LooksGarbled(name))
        {
            string fromFolder = CleanFolder(folder);
            if (Key(fromFolder).Length > 1 && !IsGeneric(fromFolder) && !LooksGarbled(fromFolder))
                return fromFolder;
        }
        return name;
    }

    /// <summary>
    /// Names to search the store for, best first: the cleaned title, then the folder name if the title is unusable
    /// or the folder holds a Japanese name the title lacks (often the full store title). Romanized or English folder
    /// names are otherwise skipped: they rarely match and could match the wrong work.
    /// </summary>
    public static List<string> SearchQueries(string title, string folder)
    {
        var queries = new List<string>();
        bool Usable(string query) => Key(query).Length >= 2 && !IsGeneric(query) && !LooksGarbled(query);

        string fromTitle = Clean(title);
        if (Usable(fromTitle)) queries.Add(fromTitle);

        string fromFolder = CleanFolder(folder);
        if (Usable(fromFolder) &&
            (queries.Count == 0 || (HasJapanese(fromFolder) && !Key(fromTitle).Contains(Key(fromFolder)))))
            queries.Add(fromFolder);

        // Store titles write × where a game's own title often has a plain x.
        foreach (string query in queries.ToList())
        {
            if (TimesRegex.IsMatch(query)) queries.Add(TimesRegex.Replace(query, "\u00d7"));
        }

        foreach (string query in queries.ToList())
        {
            string shown = query;
            foreach (var (word, replacement) in CensoredWords) shown = shown.Replace(word, replacement);
            if (shown != query) queries.Add(shown);
        }
        return queries;
    }

    private static bool HasJapanese(string text) =>
        text.Any(ch => ch is >= '\u3040' and <= '\u30FF' or >= '\u4E00' and <= '\u9FFF');

    /// <summary>True for names that say nothing about the game, like "Project1", "Game" or "本体".</summary>
    public static bool IsGeneric(string name) => GenericRegex.IsMatch(Key(name));

    /// <summary>True for text that was decoded with the wrong code page (common in folder names from zips).</summary>
    public static bool LooksGarbled(string text)
    {
        int latin = 0;
        foreach (char ch in text)
        {
            if (ch is >= '\uE000' and <= '\uF8FF' or '\uFFFD') return true;
            // accented Latin letters, not × or ÷
            if (ch is >= '\u00C0' and <= '\u00FF' and not '\u00D7' and not '\u00F7') latin++;
        }
        return latin >= 2;
    }

    /// <summary>Lower-case letters and digits only, with full-width forms folded, for comparing names.</summary>
    public static string Key(string value)
    {
        string form = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(form.Length);
        foreach (char ch in form)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
        }
        return builder.ToString();
    }

    private static bool IsNote(string inner)
    {
        string text = Plain(inner).Trim();
        // a lone key letter such as [A]
        return text.Length == 0 || (text.Length == 1 && char.IsAsciiLetter(text[0])) || VersionOnlyRegex.IsMatch(text) ||
               EditionRegex.IsMatch(text) || KeyNoteRegex.IsMatch(text) || StoreIdRegex.IsMatch(text);
    }

    /// <summary>
    /// A same-length copy with full-width ASCII and ideographic spaces made plain, so one set of patterns covers
    /// both and match indexes map back to the original.
    /// </summary>
    private static string Plain(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char ch = chars[i];
            if (ch is >= '\uFF01' and <= '\uFF5E') chars[i] = (char)(ch - 0xFEE0);
            else if (ch == '\u3000') chars[i] = ' ';
        }
        return new string(chars);
    }

    /// <summary>Removes the matches found in <paramref name="plain"/> from both strings.</summary>
    private static void Remove(ref string original, ref string plain, Regex regex, Func<Match, bool>? when = null)
    {
        var matches = regex.Matches(plain);
        if (matches.Count == 0) return;

        var keptOriginal = new StringBuilder(original.Length);
        var keptPlain = new StringBuilder(plain.Length);
        int last = 0;
        foreach (Match match in matches)
        {
            if (match.Length == 0 || (when is not null && !when(match))) continue;
            keptOriginal.Append(original, last, match.Index - last);
            keptPlain.Append(plain, last, match.Index - last);
            last = match.Index + match.Length;
        }
        keptOriginal.Append(original, last, original.Length - last);
        keptPlain.Append(plain, last, plain.Length - last);
        original = keptOriginal.ToString();
        plain = keptPlain.ToString();
    }

    /// <summary>Trims separators from both ends, keeping a dash or tilde that closes a pair ("-Subtitle-").</summary>
    private static void TrimSeparators(ref string original, ref string plain)
    {
        const string always = " _:/|・";
        const string paired = "-–—~";
        bool changed = true;
        while (changed && plain.Length > 0)
        {
            changed = false;
            char first = plain[0], last = plain[^1];
            if (always.Contains(last) || (paired.Contains(last) && plain.Count(c => c == last) % 2 == 1))
            {
                original = original[..^1];
                plain = plain[..^1];
                changed = true;
            }
            else if (always.Contains(first) || (paired.Contains(first) && plain.Count(c => c == first) % 2 == 1))
            {
                original = original[1..];
                plain = plain[1..];
                changed = true;
            }
        }
    }
}
