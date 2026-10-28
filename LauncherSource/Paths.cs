namespace RpgLauncher;

/// <summary>
/// File lookups that ignore case the way Windows does. Games made on Windows mix "Data" and "data" freely,
/// which Linux file systems treat as different names.
/// </summary>
internal static class Paths
{
    /// <summary>Directory listing options that match names regardless of case, otherwise like the plain overloads.</summary>
    public static readonly EnumerationOptions AnyCase = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        MatchType = MatchType.Win32,
        AttributesToSkip = 0,
        IgnoreInaccessible = false
    };

    /// <summary>The file as it is named on disk, ignoring case; null when there is none.</summary>
    public static string? File(string path) => Find(path, directory: false);

    /// <summary>The folder as it is named on disk, ignoring case; null when there is none.</summary>
    public static string? Folder(string path) => Find(path, directory: true);

    private static string? Find(string path, bool directory)
    {
        if (directory ? System.IO.Directory.Exists(path) : System.IO.File.Exists(path)) return path;
        // Windows file systems already ignore case.
        if (OperatingSystem.IsWindows()) return null;

        // Walk up to the deepest folder that exists, then match each remaining name.
        var names = new Stack<string>();
        string? current = Path.TrimEndingDirectorySeparator(path);
        while (!string.IsNullOrEmpty(current) && !System.IO.Directory.Exists(current))
        {
            names.Push(Path.GetFileName(current));
            current = Path.GetDirectoryName(current);
        }
        if (string.IsNullOrEmpty(current) || names.Count == 0) return null;

        try
        {
            while (names.TryPop(out var name))
            {
                bool last = names.Count == 0;
                var matches = last && !directory
                    ? System.IO.Directory.EnumerateFiles(current, name, AnyCase)
                    : System.IO.Directory.EnumerateDirectories(current, name, AnyCase);
                current = matches.FirstOrDefault();
                if (current is null) return null;
            }
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
