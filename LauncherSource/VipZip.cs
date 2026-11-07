using System.IO.Compression;
using System.Text;

namespace RpgLauncher;

/// <summary>Unpacks zipped games beside their zip, as MoshimoLoader does for dropped zips.</summary>
internal static class VipZip
{
    static VipZip() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// After a zip is unpacked: when its folder holds no game but holds zips (a game zipped again inside the upload,
    /// with a readme beside it), unpacks those as well, each beside itself, and deletes each one that unpacked.
    /// Two levels at most. Returns how many were unpacked.
    /// </summary>
    public static int ExtractInner(string folder)
    {
        int unpacked = 0;
        for (int level = 0; level < 2; level++)
        {
            if (!Directory.Exists(folder) || GameScanner.Scan(folder).Count > 0) break;
            List<string> inner;
            try
            {
                inner = Directory.EnumerateFiles(folder, "*.zip", new EnumerationOptions
                {
                    RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive
                }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }
            int before = unpacked;
            foreach (string zip in inner)
            {
                try
                {
                    Extract(zip);
                    File.Delete(zip);
                    unpacked++;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
                {
                    // A password or a broken zip: it stays, for the player to open.
                    Diagnostics.Log($"Inner zip not extracted {zip}: {ex.Message}");
                }
            }
            if (unpacked == before) break;
        }
        return unpacked;
    }

    /// <summary>
    /// Extracts the zip next to itself: a zip holding one folder becomes that folder, anything else goes into a
    /// folder named after the zip. Never merges into an existing folder. Returns the game's folder.
    /// </summary>
    public static string Extract(string zip)
    {
        string parent = Path.GetDirectoryName(zip) ?? throw new IOException("The zip has no folder.");
        // Names flagged as UTF-8 are always read as UTF-8; unflagged ones are whatever the zipper used.
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Read, NameEncoding(zip));

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

        bool created = !Directory.Exists(game);
        Directory.CreateDirectory(target);
        try
        {
            // Refuses entries that would land outside the target folder.
            archive.ExtractToDirectory(target, overwriteFiles: false);
        }
        catch
        {
            // A half-unpacked game is no use; the zip stays for another try.
            if (created)
            {
                try { Directory.Delete(game, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostics.Log("Partial unpack left: " + game); }
            }
            throw;
        }
        return game;
    }

    /// <summary>
    /// The encoding of a zip's unflagged names: UTF-8 when every one of them is valid UTF-8 (newer zippers, such as
    /// itch.io uploads made on a Mac or with 7-Zip), else Shift-JIS, as old Japanese zips use without saying so, or the
    /// DOS Western code page when the names make sense only that way (a zip made on a Western PC).
    /// </summary>
    private static Encoding NameEncoding(string zip)
    {
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        // Latin-1 keeps each byte of a name as one character, so the raw bytes can be had back.
        using var raw = ZipFile.Open(zip, ZipArchiveMode.Read, Encoding.Latin1);
        var names = new List<byte>();
        bool utf8 = true;
        foreach (var entry in raw.Entries)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(entry.FullName);
            if (bytes.All(b => b < 0x80)) continue;
            names.AddRange(bytes);
            names.Add((byte)'/');
            try { strict.GetString(bytes); }
            catch (DecoderFallbackException) { utf8 = false; }
        }
        if (names.Count == 0) return Encoding.GetEncoding(932);
        if (utf8) return Encoding.UTF8;
        // Japanese Windows zips names in Shift-JIS; Western Windows in its DOS code page (437), where "ó" is one byte.
        return TextFiles.Guess(names.ToArray(), Encoding.GetEncoding(437));
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
