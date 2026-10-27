using System.Text;

namespace RpgLauncher;

/// <summary>Spells a romanized name in katakana (Sakura → サクラ) to match store titles that write it so.</summary>
internal static class Romaji
{
    private static readonly Dictionary<string, string> Syllables = new()
    {
        ["kya"] = "キャ", ["kyu"] = "キュ", ["kyo"] = "キョ", ["sha"] = "シャ", ["shu"] = "シュ", ["sho"] = "ショ",
        ["cha"] = "チャ", ["chu"] = "チュ", ["cho"] = "チョ", ["nya"] = "ニャ", ["nyu"] = "ニュ", ["nyo"] = "ニョ",
        ["hya"] = "ヒャ", ["hyu"] = "ヒュ", ["hyo"] = "ヒョ", ["mya"] = "ミャ", ["myu"] = "ミュ", ["myo"] = "ミョ",
        ["rya"] = "リャ", ["ryu"] = "リュ", ["ryo"] = "リョ", ["gya"] = "ギャ", ["gyu"] = "ギュ", ["gyo"] = "ギョ",
        ["bya"] = "ビャ", ["byu"] = "ビュ", ["byo"] = "ビョ", ["pya"] = "ピャ", ["pyu"] = "ピュ", ["pyo"] = "ピョ",
        ["shi"] = "シ", ["chi"] = "チ", ["tsu"] = "ツ",
        ["ja"] = "ジャ", ["ju"] = "ジュ", ["jo"] = "ジョ", ["je"] = "ジェ", ["ji"] = "ジ", ["fu"] = "フ",
        ["a"] = "ア", ["i"] = "イ", ["u"] = "ウ", ["e"] = "エ", ["o"] = "オ",
    };

    private const string Vowels = "aiueo";

    // Consonant rows, in vowel order a i u e o.
    private static readonly Dictionary<char, string> Rows = new()
    {
        ['k'] = "カキクケコ", ['s'] = "サシスセソ", ['t'] = "タチツテト", ['n'] = "ナニヌネノ", ['h'] = "ハヒフヘホ",
        ['m'] = "マミムメモ", ['y'] = "ヤイユエヨ", ['r'] = "ラリルレロ", ['l'] = "ラリルレロ", ['w'] = "ワイウエヲ",
        ['g'] = "ガギグゲゴ", ['z'] = "ザジズゼゾ", ['d'] = "ダヂヅデド", ['b'] = "バビブベボ", ['p'] = "パピプペポ",
        ['v'] = "バビブベボ", ['f'] = "ファフィフフェフォ", ['c'] = "カシクセコ",
    };

    /// <summary>The katakana spelling, or null when <paramref name="word"/> isn't letters only.</summary>
    public static string? ToKatakana(string word)
    {
        string text = word.ToLowerInvariant();
        if (text.Length == 0 || !text.All(ch => ch is >= 'a' and <= 'z')) return null;

        var result = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            // doubled consonant: a small tsu
            if (i + 1 < text.Length && ch == text[i + 1] && ch != 'n' && !Vowels.Contains(ch))
            {
                result.Append('ッ');
                i++;
                continue;
            }
            // n before a consonant or at the end
            if (ch == 'n' && (i + 1 == text.Length || (!Vowels.Contains(text[i + 1]) && text[i + 1] != 'y')))
            {
                result.Append('ン');
                i++;
                continue;
            }

            string? match = null;
            for (int length = 3; length >= 1 && match is null; length--)
            {
                if (i + length <= text.Length && Syllables.TryGetValue(text.Substring(i, length), out var kana))
                {
                    match = kana;
                    i += length;
                }
            }
            if (match is not null)
            {
                result.Append(match);
                continue;
            }

            if (Rows.TryGetValue(ch, out var row))
            {
                if (i + 1 < text.Length && Vowels.IndexOf(text[i + 1]) is var vowel and >= 0)
                {
                    result.Append(ch == 'f' ? FRow(vowel) : row[vowel].ToString());
                    i += 2;
                }
                else
                {
                    // a consonant on its own takes u (o after t and d)
                    result.Append(ch is 't' or 'd' ? row[4] : ch == 'f' ? 'フ' : row[2]);
                    i++;
                }
                continue;
            }

            result.Append(ch == 'x' ? "クス" : ch == 'q' ? "ク" : "");
            i++;
        }
        return result.ToString();

        static string FRow(int vowel) => vowel switch { 0 => "ファ", 1 => "フィ", 2 => "フ", 3 => "フェ", _ => "フォ" };
    }
}
