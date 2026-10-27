namespace RpgLauncher;

/// <summary>Broad kind of a store tag, used to group the Tags popup.</summary>
internal enum TagCategory
{
    Protagonist,
    Characters,
    Clothing,
    Appearance,
    Situation,
    Play,
    Other
}

/// <summary>Categorizes DLsite tags, which come without one; unknown tags are <see cref="TagCategory.Other"/>.</summary>
internal static class TagCategories
{
    private static readonly Dictionary<string, TagCategory> Exact = BuildExact();

    // Substring rules, checked in order after the exact map; more specific groups come first.
    private static readonly (TagCategory Category, string[] Words)[] Rules =
    {
        (TagCategory.Protagonist, new[]
        {
            "protagonist", "主人公"
        }),
        (TagCategory.Clothing, new[]
        {
            "clothed", "clothing", "uniform", "swimsuit", "bikini", "underwear",
            "lingerie", "panties", "stocking", "cosplay", "kimono", "apron", "leotard",
            "着衣", "服装", "制服", "水着", "下着", "ニーソ", "ストッキング", "コスプレ",
            "着物", "和服", "浴衣", "エプロン", "レオタード", "メガネ", "ピアス"
        }),
        (TagCategory.Appearance, new[]
        {
            "breast", "nipple", "areola", "hips", "butt", "slim", "slender", "curvy",
            "chubby", "muscular", "tanned", "blonde", "hair", "巨乳", "爆乳", "貧乳",
            "微乳", "おっぱい", "乳首", "乳輪", "お尻", "ヒップ", "スレンダー",
            "ムチムチ", "筋肉", "褐色", "日焼け", "金髪", "黒髪", "ヘア", "ネコミミ",
            "獣耳", "フタナリ", "巨根", "陰毛"
        }),
        (TagCategory.Play, new[]
        {
            "cumshot", "creampie", "ejaculat", "bukkake", "facial", "fellatio", "paizuri",
            "handjob", "footjob", "anal", "cunnilingus", "squirting", "gushing",
            "urination", "enema", "scat", "training", "bondage", "restraint",
            "humiliation", "coercion", "compulsion", "rape", "gangbang", "orgy",
            "threesome", "masturbation", "vibrator", "sex toy", "fisting", "milking",
            "clitoris", "dirty talk", "teasing", "tickling", "molestation", "voyeurism",
            "public sex", "sleep sex", "tentacle", "bestiality", "spanking", "groping",
            "urethra", "torture", "guro", "body modification", "aphrodisiac",
            "中出し", "射精", "ぶっかけ", "顔射", "フェラ", "パイズリ", "手コキ", "足コキ",
            "アナル", "クンニ", "潮吹き", "放尿", "おしっこ", "おもらし", "浣腸", "スカトロ",
            "調教", "拘束", "緊縛", "羞恥", "恥辱", "陵辱", "屈辱", "強制", "無理矢理",
            "レイプ", "輪姦", "乱交", "オナニー", "バイブ", "おもちゃ", "異物", "フィスト",
            "拡張", "搾乳", "乳首責め", "クリ責め", "言葉責め", "焦らし", "くすぐり",
            "痴漢", "盗撮", "露出", "青姦", "睡眠姦", "機械姦", "触手", "獣姦", "異種姦",
            "丸呑み", "リョナ", "拷問", "人体改造", "媚薬", "ローション", "おさわり",
            "スパンキング", "尿道", "イラマチオ"
        }),
        (TagCategory.Situation, new[]
        {
            "cuckold", "netorare", "netori", "corruption", "pregnancy", "impregnation",
            "childbirth", "affair", "cheating", "confinement", "slavery", "isekai",
            "fantasy", "school", "office", "workplace", "romance", "lovers",
            "living together", "marriage", "hypnosis", "brainwashing", "mind control",
            "time stop", "possession", "petrification", "gender change", "horror",
            "mystery", "occult", "sci-fi", "history", "prostitution", "soap",
            "adventure", "dungeon", "寝取", "浮気", "快楽堕ち", "悪堕ち", "妊娠", "孕",
            "出産", "産卵", "監禁", "閉じ込め", "奴隷", "異世界", "ファンタジー", "学校",
            "学園", "オフィス", "職場", "屋外", "日常", "ラブラブ", "あまあま", "恋愛",
            "恋人", "同棲", "初体験", "結婚", "催眠", "洗脳", "精神支配", "時間停止",
            "憑依", "石化", "性転換", "女体化", "戦場", "ミリタリー", "ホラー",
            "ミステリー", "オカルト", "歴史", "コメディ", "シリアス", "癒し", "売春",
            "援交", "風俗", "ソープ", "冒険", "戦記"
        }),
        (TagCategory.Characters, new[]
        {
            "married", "widow", "virgin", "older", "younger", "sister", "brother",
            "mother", "father", "daughter", "childhood friend", "classmate", "colleague",
            "teacher", "student", "princess", "queen", "nurse", "idol", "model",
            "foreigner", "delinquent", "milf", "twins", "既婚", "人妻", "熟女", "未亡人",
            "処女", "童貞", "おねショタ", "ショタおね", "妹", "姉", "母", "父", "娘",
            "双子", "幼なじみ", "同級生", "同僚", "上司", "部下", "先輩", "後輩", "教師",
            "生徒", "学生", "ギャル", "ロリ", "ショタ", "お嬢様", "お姫様", "女王",
            "エルフ", "天使", "悪魔", "サキュバス", "魔法少女", "くノ一", "戦士",
            "魔法使い", "魔女", "メイド", "アイドル", "モデル", "外国人", "不良",
            "ヤンキー", "おやじ", "青年", "少年", "少女", "人外", "獣人", "ロボット",
            "アンドロイド", "幽霊", "妖怪", "お姉さん", "年上", "年下"
        })
    };

    public static TagCategory Classify(string tag)
    {
        string key = tag.Trim().ToLowerInvariant();
        if (key.Length == 0) return TagCategory.Other;

        if (Exact.TryGetValue(key, out var exact)) return exact;

        foreach (var (category, words) in Rules)
        {
            foreach (string word in words)
                if (key.Contains(word, StringComparison.Ordinal))
                    return category;
        }

        return TagCategory.Other;
    }

    public static string Label(TagCategory category, MetadataLanguage language)
    {
        bool japanese = language == MetadataLanguage.Japanese;
        return category switch
        {
            TagCategory.Protagonist => japanese ? "主人公" : "PROTAGONIST",
            TagCategory.Characters => japanese ? "キャラクター" : "CHARACTERS",
            TagCategory.Clothing => japanese ? "服装" : "CLOTHING",
            TagCategory.Appearance => japanese ? "外見" : "APPEARANCE",
            TagCategory.Situation => japanese ? "シチュエーション" : "SITUATION",
            TagCategory.Play => japanese ? "プレイ" : "PLAY",
            _ => japanese ? "その他" : "OTHER"
        };
    }

    private static Dictionary<string, TagCategory> BuildExact()
    {
        var map = new Dictionary<string, TagCategory>(StringComparer.Ordinal);

        void Add(TagCategory category, params string[] names)
        {
            foreach (string name in names)
                map[name.ToLowerInvariant()] = category;
        }

        Add(TagCategory.Protagonist,
            "male protagonist", "female protagonist", "男主人公", "女主人公");

        Add(TagCategory.Clothing,
            "clothed", "uniform", "school uniform", "sailor uniform", "gym uniform",
            "swimsuit", "school swimsuit", "bikini", "micro bikini", "underwear",
            "lingerie", "panties", "shorts", "no panties", "miniskirt", "kimono",
            "yukata", "apron", "naked apron", "leotard", "bunny girl", "spats",
            "knee socks", "thigh-high socks", "stockings", "suit", "garter", "cosplay",
            "maid outfit", "nurse outfit", "shrine maiden", "military uniform",
            "gothic lolita", "rubber", "chinese dress", "glasses", "accessories",
            "accessory", "piercing", "collar", "choker",
            "着衣", "制服", "セーラー服", "体操着", "水着", "スクール水着", "下着",
            "パンツ", "ショーツ", "ノーパン", "ミニスカ", "着物", "和服", "浴衣",
            "エプロン", "裸エプロン", "レオタード", "バニーガール", "スパッツ",
            "ニーソックス", "ストッキング", "スーツ", "ガーター", "コスプレ", "メイド服",
            "ナース服", "巫女", "軍服", "ゴスロリ", "ラバー", "チャイナドレス", "メガネ",
            "ピアス", "装飾品", "首輪", "チョーカー");

        Add(TagCategory.Appearance,
            "big breasts", "huge breasts", "small breasts", "flat chest", "nipples",
            "areola", "ass", "hips", "legs", "slim", "slender", "curvy", "chubby",
            "muscular", "tall", "short stature", "tanned", "dark skin", "blonde",
            "black hair", "short hair", "long hair", "ponytail", "twintails",
            "cat ears", "animal ears", "pregnant belly", "futanari", "giant",
            "pubic hair", "body type", "appearance",
            "巨乳/爆乳", "貧乳/微乳", "複乳/怪乳/超乳", "乳首/乳輪", "お尻/ヒップ", "脚",
            "スレンダー", "ムチムチ", "ぷに", "筋肉", "ガチムチ", "長身", "低身長",
            "褐色/日焼け", "金髪", "黒髪", "ショートヘア", "ロングヘア", "ポニーテール",
            "ツインテール", "ネコミミ", "獣耳", "ぼて腹/妊婦", "ツルペタ", "パイパン",
            "陰毛/腋毛", "フタナリ", "巨根", "巨大化", "太め/デブ", "短髪", "体型");

        Add(TagCategory.Situation,
            "cuckoldry (netorare)", "cuckoldry", "netorare", "netori", "reverse ntr",
            "pleasure corruption", "corruption", "pregnancy / impregnation",
            "pregnancy", "impregnation", "childbirth", "ovulation", "affair",
            "cheating", "confinement", "imprisonment", "slavery", "isekai", "fantasy",
            "school", "office", "workplace", "outdoors", "daily life", "lovey-dovey",
            "romance", "lovers", "living together", "first time", "marriage",
            "hypnosis", "brainwashing", "mind control", "time stop", "possession",
            "petrification", "gender change", "body swap", "feminization", "battlefield",
            "horror", "mystery", "occult", "sci-fi", "history", "military", "sports",
            "comedy", "serious", "healing", "prostitution", "soap", "brothel",
            "adventure", "war", "dungeon",
            "寝取られ", "寝取り", "逆NTR", "浮気", "快楽堕ち", "悪堕ち", "妊娠/孕ませ",
            "妊娠", "孕ませ", "出産", "産卵", "監禁", "閉じ込め", "奴隷", "下僕",
            "異世界転生", "異世界", "ファンタジー", "学校/学園", "学校", "学園",
            "オフィス/職場", "オフィス", "職場", "屋外", "日常/生活", "日常",
            "ラブラブ/あまあま", "ラブラブ", "あまあま", "恋愛", "恋人同士", "恋人",
            "同棲", "初体験", "結婚", "催眠", "洗脳", "精神支配", "時間停止", "憑依",
            "石化", "性転換(TS)", "性転換", "女体化", "戦場", "ミリタリー", "スポーツ",
            "ホラー", "ミステリー", "オカルト", "SF", "歴史/時代物", "歴史", "コメディ",
            "シリアス", "癒し", "売春/援交", "売春", "援交", "風俗/ソープ", "風俗",
            "ソープ", "冒険", "戦記", "ダンジョン");

        Add(TagCategory.Play,
            "internal cumshot", "creampie", "cumshot", "ejaculation", "bukkake",
            "facial", "oral ejaculation", "swallowing", "fellatio", "paizuri",
            "handjob", "footjob", "anal", "anal sex", "cunnilingus",
            "squirting / gushing", "squirting", "gushing", "urination", "pissing",
            "enema", "scat", "training", "discipline", "bondage", "restraint", "sm",
            "sadism", "masochism", "shame / humiliation", "humiliation",
            "coercion / compulsion", "coercion", "compulsion", "rape", "gangbang",
            "orgy", "group sex", "threesome", "masturbation", "vibrator", "sex toy",
            "dildo", "fisting", "stretching", "milking", "nipple play", "clitoris play",
            "clitoris", "dirty talk", "teasing", "tickling", "molestation",
            "voyeurism", "peeping", "public sex", "outdoor sex", "sleep sex",
            "machine sex", "tentacle", "bestiality", "monster sex", "deep throat",
            "irrumatio", "no penetration", "softcore", "spanking", "groping",
            "urethra", "torture", "blood", "guro", "body modification", "drug",
            "aphrodisiac", "lotion",
            "中出し", "射精", "ぶっかけ", "顔射", "口内射精", "ごっくん/食ザー", "ごっくん",
            "フェラチオ", "フェラ", "パイズリ", "手コキ", "足コキ", "アナル", "クンニ",
            "潮吹き", "放尿/おしっこ", "放尿", "おしっこ", "おもらし", "浣腸", "スカトロ",
            "調教", "しつけ", "拘束", "緊縛", "SM", "羞恥/恥辱", "羞恥", "恥辱", "陵辱",
            "屈辱", "強制/無理矢理", "強制", "無理矢理", "レイプ", "輪姦/回し", "輪姦",
            "複数プレイ/乱交", "複数プレイ", "乱交", "3P", "4P", "オナニー", "バイブ",
            "おもちゃ", "異物", "フィスト", "拡張", "搾乳", "乳首責め", "クリ責め",
            "ニプルファック", "言葉責め", "焦らし", "くすぐり", "痴漢", "盗撮", "露出",
            "青姦", "睡眠姦", "機械姦", "触手", "獣姦", "異種姦", "蟲姦", "丸呑み",
            "リョナ", "グロ", "血液/流血", "拷問", "人体改造", "薬物", "媚薬", "ローション",
            "おさわり", "本番なし", "ソフトエッチ", "スパンキング", "尿道", "イラマチオ");

        Add(TagCategory.Characters,
            "married person", "married woman", "married man", "wife", "husband",
            "widow", "widower", "virgin female", "virgin male",
            "elder girl x younger boy", "younger girl x older boy", "elder sister",
            "younger sister", "sister", "brother", "elder brother", "younger brother",
            "mother", "father", "daughter", "son", "aunt", "uncle", "twins",
            "childhood friend", "classmate", "colleague", "boss", "subordinate",
            "senior", "junior", "teacher", "female teacher", "school teacher",
            "student", "schoolgirl", "schoolboy", "office lady", "ol", "salaryman",
            "doctor", "nurse", "gal", "loli", "shota", "princess", "queen", "elf",
            "fairy", "demon", "angel", "succubus", "magical girl", "kunoichi",
            "warrior", "witch", "wizard", "maid", "idol", "model", "foreigner",
            "delinquent", "old man", "youth", "boy", "girl", "monster girl",
            "beastman", "robot", "android", "ghost", "yokai", "milf", "mature woman",
            "yandere", "tsundere",
            "既婚者", "人妻", "熟女", "未亡人", "処女", "童貞", "おねショタ", "ショタおね",
            "妹", "姉", "義妹", "義姉", "兄", "弟", "母", "母親", "義母", "父", "叔父",
            "娘", "双子", "幼なじみ", "同級生", "同僚", "上司", "部下", "先輩", "後輩",
            "教師", "女教師", "保健医", "医者", "看護婦", "ナース", "生徒", "学生",
            "ギャル", "ロリ", "ショタ", "お嬢様", "お姫様", "女王様", "エルフ", "妖精",
            "天使", "悪魔", "サキュバス", "魔法少女", "くノ一", "戦士", "魔法使い",
            "魔女", "メイド", "アイドル", "モデル", "外国人", "不良", "ヤンキー",
            "おやじ", "青年", "少年", "少女", "人外娘", "モンスター娘", "獣人",
            "ロボット", "アンドロイド", "幽霊", "妖怪", "お姉さん", "年上", "年下",
            "ヤンデレ", "ツンデレ", "OL", "サラリーマン");

        return map;
    }
}
