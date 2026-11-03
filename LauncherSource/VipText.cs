namespace RpgLauncher;

/// <summary>
/// The VIPRPG view's interface language: Japanese unless 設定 → 言語 says English. Only the launcher's own words are
/// translated; game titles, authors, matsuri names and other catalog data are shown as they are.
/// </summary>
internal static class VipText
{
    public static bool English { get; set; }

    /// <summary>The text in the chosen language.</summary>
    public static string T(string japanese, string english) => English ? english : japanese;
}
