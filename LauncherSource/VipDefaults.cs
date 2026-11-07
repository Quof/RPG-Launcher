namespace RpgLauncher;

/// <summary>Playlists every VIPRPG mode starts with. Each is added once; one the user deletes stays deleted.</summary>
internal static class VipDefaults
{
    public sealed record Playlist(string Id, string Japanese, string English, string[] Games);

    public static readonly Playlist[] Playlists =
    {
        new("recommended-1", "色々のおすすめ", "Various Recommended", new[]
        {
        "dl:games/thread/hokan/05/1460.zip", "2017-kouhaku/11", "2013-summer/40", "2015-kouhaku/02", "2006-kouhaku/04",
        "2008-gw/012", "2005-kouhaku/09", "2012-kouhaku/054", "2014-kouhaku/037", "2014-kouhaku/008",
        "2019-kouhaku/17", "2016-gw/15", "2015-gw/37", "dl:games/thread/hokan/09/0104.zip", "dl:games/thread/hokan/06/2294.zip",
        "2005-kouhaku/30", "2020-gw/19", "2018-kouhaku/26", "2014-kouhaku/089", "dl:games/thread/hokan/06/1486.zip",
        "dl:games/thread/hokan/05/2655.zip", "2016-summer-2/014", "2015-summer/05", "2017-kouhaku/17", "2011-denpa-game-festival/10",
        "2012-kouhaku/016", "2008-kouhaku/021", "dl:games/thread/hokan/10/1674.zip", "2012-kouhaku/055", "2010-gw/003",
        "2020-kouhaku/01", "2020-gw/7", "dl:games/thread/hokan/12/3830.zip", "dl:games/thread/hokan/06/1601.zip", "2012-magic-festival/6",
        "2007-summer/19", "dl:games/thread/other/wiki/5c0b80bb.zip", "2006-kouhaku/09", "2010-kouhaku/039", "2010-kouhaku/012",
        "2020-summer/07", "dl:games/thread/other/wiki/d3ef25ff.zip", "2011-kouhaku/022", "2020-summer/05", "2015-kouhaku/31",
        "2020-gw/20", "2007-summer/84", "dl:games/thread/other/wiki/9a50109e.zip", "dl:games/thread/hokan/11/2638.zip", "dl:games/thread/hokan/11/0290.zip",
        "2010-gw/051", "2013-summer/30", "2012-kouhaku/078", "2013-kouhaku/71", "dl:games/thread/hokan/09/1001.zip",
        "2015-kouhaku/55", "dl:games/thread/hokan/09/0922.zip", "2013-kouhaku/6", "2017-kouhaku/35", "2018-kouhaku/16",
        "2016-gw/31", "2013-kouhaku/72", "2009-kouhaku/013", "dl:games/thread/hokan/10/0768.zip", "dl:games/thread/hokan/10/0123.zip",
        "2014-kouhaku/057", "dl:games/thread/hokan/06/3789.zip", "2009-kouhaku/043", "2014-summer/07", "dl:games/thread/other/wiki/0fc3af33.zip",
        "2019-kouhaku/28", "dl:games/thread/hokan/10/0716.zip", "2016-summer/07", "2006-gw/21", "dl:games/thread/hokan/08/1355.zip",
        "2013-summer/37", "wiki-kouhaku2007/079", "2014-gw/004", "dl:games/thread/hokan/06/3435.zip", "2016-kouhaku/16",
        "2009-kouhaku/024", "2011-kouhaku/065", "2017-gw/29", "dl:games/thread/hokan/06/2091.zip", "dl:games/thread/hokan/02/game_629.zip",
        "2019-kouhaku/48", "2009-summer/069", "2014-kouhaku/031", "2005-kouhaku/12", "2013-summer/55",
        "2018-kouhaku/29", "2014-kouhaku/026", "2018-summer/03", "2011-kouhaku/003", "2013-kouhaku/28",
        "2011-denpa-game-festival/30", "2018-kouhaku/08", "2014-summer/54", "2017-gw/44", "2018-gw/08",
        "2013-summer/13", "2019-summer/8", "2016-kouhaku/18", "2022-kouhaku/6", "2021-gw/07",
        "2020-kouhaku/04",
        })
    };

    /// <summary>
    /// Built-in playlists that were dropped (their games moved to a series), with their games. A copy still holding
    /// exactly those games is removed; one the user changed stays as their own list.
    /// </summary>
    private static readonly (string Id, string[] Games)[] Retired =
    {
        ("tkool-samples", new[]
        {
            "dl:games/thread/other/tkool2000/hanayome.zip", "dl:games/thread/other/tkool2000/abyss.zip", "dl:games/thread/other/tkool2000/kaizoku.zip", "dl:games/thread/other/tkool2000/queen.zip", "dl:games/thread/other/tkool2000/iii.zip", "dl:games/thread/other/tkool2000/ugomeku.zip", "dl:games/thread/other/tkool2000/syuudou.zip",
            "dl:games/thread/other/tkool2003/hermit01.zip", "dl:games/thread/other/tkool2003/hermit02.zip", "dl:games/thread/other/tkool2003/hermit03.zip", "dl:games/thread/other/tkool2003/hermit04.zip", "dl:games/thread/other/tkool2003/hermit05.zip", "dl:games/thread/other/tkool2003/hermit06.zip", "dl:games/thread/other/tkool2003/trial.zip", "dl:games/thread/other/tkool2003/nekotsukai.zip", "dl:games/thread/other/tkool2003/minigame.zip", "dl:games/thread/other/tkool2003/orenoshiren.zip"
        })
    };

    public static Playlist? Of(VipPlaylist playlist) =>
        playlist.Builtin is { } id ? Playlists.FirstOrDefault(p => p.Id == id) : null;

    /// <summary>
    /// The name shown for a playlist: a built-in one in the launcher's language while it keeps its own name,
    /// else the name it was given.
    /// </summary>
    public static string Label(VipPlaylist playlist) =>
        Of(playlist) is { } builtin && playlist.Name == builtin.Japanese ? VipText.T(builtin.Japanese, builtin.English) : playlist.Name;

    /// <summary>
    /// Adds the built-in playlists this data hasn't had yet, at the top, and takes out retired ones; true if the
    /// data changed.
    /// </summary>
    public static bool Seed(VipData data)
    {
        bool added = false;
        foreach (var (id, games) in Retired)
        {
            foreach (var playlist in data.Playlists.Where(p => p.Builtin == id).ToList())
            {
                if (playlist.Games.SequenceEqual(games))
                {
                    data.Playlists.Remove(playlist);
                    if (data.Selected == "playlist:" + playlist.Name) data.Selected = null;
                }
                else playlist.Builtin = null;
                added = true;
            }
        }
        foreach (var builtin in Playlists.Reverse())
        {
            if (data.SeededPlaylists.Contains(builtin.Id)) continue;
            data.SeededPlaylists.Add(builtin.Id);
            added = true;
            // A list of the user's own with that name is left as it is.
            if (data.Playlists.Any(p => p.Name == builtin.Japanese || p.Name == builtin.English)) continue;
            data.Playlists.Insert(0, new VipPlaylist { Name = builtin.Japanese, Builtin = builtin.Id, Games = builtin.Games.ToList() });
        }
        return added;
    }
}
