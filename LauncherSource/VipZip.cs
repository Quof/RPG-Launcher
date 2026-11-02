using System.IO.Compression;
using System.Text;

namespace RpgLauncher;

/// <summary>Unpacks zipped games beside their zip, as MoshimoLoader does for dropped zips.</summary>
internal static class VipZip
{
    static VipZip() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Extracts the zip next to itself: a zip holding one folder becomes that folder, anything else goes into a
    /// folder named after the zip. Never merges into an existing folder. Returns the game's folder.
    /// </summary>
    public static string Extract(string zip)
    {
        string parent = Path.GetDirectoryName(zip) ?? throw new IOException("The zip has no folder.");
        // Old Japanese zips store names in Shift-JIS without saying so; names flagged as UTF-8 are read as UTF-8.
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Read, Encoding.GetEncoding(932));

        var names = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).Where(name => name.Length > 0).ToList();
        var tops = names.Select(name => name.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool single = tops.Count == 1 && names.All(name => name.Contains('/'));

        string target, game;
        if (single && !Directory.Exists(Path.Combine(parent, tops[0])))
        {
            target = parent;
            game = Path.Combine(parent, tops[0]);
        }
        else
        {
            target = game = Unique(Path.Combine(parent, Path.GetFileNameWithoutExtension(zip)));
        }

        Directory.CreateDirectory(target);
        // Refuses entries that would land outside the target folder.
        archive.ExtractToDirectory(target, overwriteFiles: false);
        return game;
    }

    private static string Unique(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return path;
        for (int i = 2; ; i++)
        {
            string candidate = $"{path} ({i})";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>True when a folder beside the zip already has its name: the zip was extracted before.</summary>
    public static bool LooksExtracted(string zip)
    {
        string? parent = Path.GetDirectoryName(zip);
        if (parent is null) return false;
        string stem = VipCatalog.TitleKey(Path.GetFileNameWithoutExtension(zip));
        try
        {
            return Directory.EnumerateDirectories(parent).Any(dir => VipCatalog.TitleKey(Path.GetFileName(dir)) == stem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
