namespace RpgLauncher;

/// <summary>
/// Japanese and English DLsite tag names, paired from games whose store data has both lists (in the same order),
/// so tags added by hand follow the language setting.
/// </summary>
internal static class TagNames
{
    private static Dictionary<string, string> _english = new(StringComparer.CurrentCultureIgnoreCase);
    private static Dictionary<string, string> _japanese = new(StringComparer.CurrentCultureIgnoreCase);

    public static void Learn(IEnumerable<GameMetadata?> library)
    {
        var english = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        var japanese = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var metadata in library)
        {
            if (metadata?.English is not { } text || text.Tags.Count != metadata.Tags.Count) continue;
            for (int i = 0; i < metadata.Tags.Count; i++)
            {
                english.TryAdd(metadata.Tags[i], text.Tags[i]);
                japanese.TryAdd(text.Tags[i], metadata.Tags[i]);
            }
        }
        _english = english;
        _japanese = japanese;
    }

    /// <summary>A tag's name in <paramref name="language"/>, or the tag unchanged when unknown.</summary>
    public static string For(string tag, MetadataLanguage language) =>
        (language == MetadataLanguage.English ? _english : _japanese).TryGetValue(tag, out var name) ? name : tag;
}
