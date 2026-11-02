namespace RpgLauncher;

/// <summary>
/// The characters of VIPRPG mode, from <see cref="VipAssets"/>: the RTP's hero sheet (Alex and company) and its
/// monster sheet, as RPG Maker 2000 character sets of 24×32 frames, three per direction, rows facing up, right,
/// down and left; and the hero faces (48×48, four by four).
/// </summary>
internal static class VipSprites
{
    public static Bitmap? Party => VipAssets.Load("party", "CharSet", "主人公1", keyed: true);
    public static Bitmap? Monsters => VipAssets.Load("monster", "CharSet", "モンスター1", keyed: true);
    public static Bitmap? Faces => VipAssets.Load("face", "FaceSet", "主人公1", keyed: false);

    /// <summary>The source rectangle of one frame of character <paramref name="index"/> on an 8-character sheet.</summary>
    public static Rect Frame(Bitmap sheet, int index, int direction, int frame)
    {
        // A full sheet holds 4×2 characters; a sheet of one character is 72×128.
        bool single = sheet.PixelSize.Width <= 72;
        int column = single ? 0 : index % 4, row = single ? 0 : index / 4;
        return new Rect(column * 72 + frame * 24, row * 128 + direction * 32, 24, 32);
    }

    /// <summary>The source rectangle of face <paramref name="index"/>, or null when the sheet is too small.</summary>
    public static Rect? Face(Bitmap sheet, int index)
    {
        var rect = new Rect(index % 4 * 48, index / 4 * 48, 48, 48);
        return rect.Right <= sheet.PixelSize.Width && rect.Bottom <= sheet.PixelSize.Height ? rect : null;
    }
}

/// <summary>Alex and the party walking along the banner, a slime in pursuit; click one and they say something.</summary>
internal sealed class VipParty
{
    /// <summary>A walker: a character on the party or monster sheet, with a face (-1 for none) and lines.</summary>
    public sealed record Member(bool Monster, int Index, int Face, string Name, (string Japanese, string English)[] Lines);

    // The RTP's first heroes, in its own order.
    private static readonly Member[] Members =
    {
        new(false, 0, 0, "アレックス", new[]
        {
            ("VIPRPGの主人公といえば俺だ！", "Every VIPRPG hero starts with me!"),
            ("今日はどの祭りの作品を遊ぶ？", "Which matsuri are we playing today?"),
            ("ksgも最後まで遊ぶのが礼儀だぞ。", "Even a ksg deserves to be played to the end."),
            ("テストプレイならCtrlで壁をすり抜けられるぞ。", "In test play, hold Ctrl to walk through walls.")
        }),
        new(false, 1, 1, "ブライアン", new[]
        {
            ("アレックスばかり目立ちやがって…", "Alex always gets the spotlight…"),
            ("作者別にすれば、好きな作者の作品をまとめて追えるぞ。", "List by author to follow someone's work across every matsuri.")
        }),
        new(false, 2, 2, "キャロル", new[]
        {
            ("説明書もちゃんと読んでね。", "Do read the readme, too."),
            ("「感想スレ」から作品のスレッドに行けるよ。", "感想スレ opens the work's thread on the matsuri board.")
        }),
        new(false, 3, 3, "デイジー", new[]
        {
            ("zipのままの作品は「展開」で遊べるようになるよ。", "Zipped works become playable with 展開 (Extract)."),
            ("RPG_RT.exeが無い作品も、設定のランタイムかEasyRPGで動くよ。", "Games without RPG_RT.exe run with the runtime folder or EasyRPG (設定).")
        }),
        new(true, 0, -1, "スライム", new[]
        {
            ("ぷるぷる。", "*wobble wobble*"),
            ("ぷるぷる！ ぷるぷる！", "*wobbles excitedly*")
        })
    };

    private double _x = 60;
    private readonly List<(Rect Area, Member Member)> _drawn = new();
    private int _line;

    /// <summary>Alex, for the message window when nobody else is speaking.</summary>
    public static Member Leader => Members[0];

    public void Step(double width)
    {
        _x += 0.75;
        if (_x > width + Members.Length * 28 + 60) _x = -30;
    }

    public void Paint(DrawingContext g, double groundTop, int tick)
    {
        _drawn.Clear();
        for (int i = 0; i < Members.Length; i++)
        {
            var member = Members[i];
            if ((member.Monster ? VipSprites.Monsters : VipSprites.Party) is not { } sheet) continue;
            // Walk cycle 0-1-2-1; the slime keeps a wary distance.
            int step = (tick / 5 + i) % 4;
            int frame = step == 3 ? 1 : step;
            double x = Math.Round(_x - i * 28 - (member.Monster ? 24 : 0)), y = groundTop + 2;
            RetroDraw.Sprite(g, sheet, VipSprites.Frame(sheet, member.Index, direction: 1, frame), x, y);
            _drawn.Add((new Rect(x, y, 24, 32), member));
        }
    }

    /// <summary>The walker under the point and its next line, or null.</summary>
    public (Member Member, string Japanese, string English)? Hit(Point point)
    {
        foreach (var (area, member) in _drawn)
        {
            if (!area.Contains(point) || member.Lines.Length == 0) continue;
            var line = member.Lines[_line++ % member.Lines.Length];
            return (member, line.Japanese, line.English);
        }
        return null;
    }
}
