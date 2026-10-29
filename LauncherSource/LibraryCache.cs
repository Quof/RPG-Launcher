using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RpgLauncher;

/// <summary>Caches the last folder scan (launcher-library.json) so startup can skip rescanning.</summary>
internal static class LibraryCache
{
    // 2: adds RPG Maker XP, VX, VX Ace, 2000/2003 and Wolf RPG Editor games. 3: adds Ren'Py, TyranoScript and Unity games.
    // 4: Ren'Py pictures from archives and scripts, Unity games' icons.
    private const int CurrentVersion = 4;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed class CacheFile
    {
        public int Version { get; set; }
        public string Root { get; set; } = "";
        public List<string> Directories { get; set; } = new();
        public List<CachedGame> Games { get; set; } = new();
    }

    private sealed class CachedGame
    {
        public string Key { get; set; } = "";
        public string RootPath { get; set; } = "";
        public string? ExecutablePath { get; set; }
        public string? DataRoot { get; set; }
        public string Title { get; set; } = "";
        public string Engine { get; set; } = "RPG Maker";
        public string? Locale { get; set; }
        public string? Title1Path { get; set; }
        public string? Title2Path { get; set; }
        public string? IconPath { get; set; }
        public string? EncryptionKey { get; set; }
        public string? LoadError { get; set; }
        public string? Signature { get; set; }
    }

    /// <summary>
    /// The cached library, or null when missing or made for another launcher folder or folder list.
    /// Games whose folder is gone are dropped.
    /// </summary>
    public static List<GameEntry>? Load(string path, string root, IReadOnlyList<string> directories)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path, Encoding.UTF8), Options);
            if (file is null || file.Version != CurrentVersion) return null;
            if (!string.Equals(file.Root, root, StringComparison.OrdinalIgnoreCase)) return null;
            if (!file.Directories.SequenceEqual(directories, StringComparer.OrdinalIgnoreCase)) return null;

            var games = new List<GameEntry>(file.Games.Count);
            foreach (var cached in file.Games)
            {
                if (string.IsNullOrWhiteSpace(cached.Key) || !Directory.Exists(cached.RootPath)) continue;
                games.Add(new GameEntry
                {
                    Key = cached.Key,
                    RootPath = cached.RootPath,
                    ExecutablePath = cached.ExecutablePath,
                    DataRoot = cached.DataRoot,
                    Title = cached.Title,
                    Engine = cached.Engine,
                    Locale = cached.Locale,
                    Title1Path = cached.Title1Path,
                    Title2Path = cached.Title2Path,
                    IconPath = cached.IconPath,
                    EncryptionKey = cached.EncryptionKey,
                    LoadError = cached.LoadError,
                    // No saved signature: assume the files haven't changed since that scan, keeping rescans quick.
                    Signature = cached.Signature ?? GameScanner.Signature(cached.RootPath),
                    // Not cached: cheap to read, and stays right if a game is deleted and downloaded again.
                    FolderCreatedUtc = GameScanner.FolderCreatedUtc(cached.RootPath)
                });
            }
            return games;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Library cache ignored: " + ex.Message);
            return null;
        }
    }

    /// <summary>Serializes the scan result; call on the UI thread, then <see cref="Write"/> anywhere.</summary>
    public static string Serialize(string root, IReadOnlyList<string> directories, IEnumerable<GameEntry> games)
    {
        var file = new CacheFile
        {
            Version = CurrentVersion,
            Root = root,
            Directories = directories.ToList(),
            Games = games.Select(game => new CachedGame
            {
                Key = game.Key,
                RootPath = game.RootPath,
                ExecutablePath = game.ExecutablePath,
                DataRoot = game.DataRoot,
                Title = game.Title,
                Engine = game.Engine,
                Locale = game.Locale,
                Title1Path = game.Title1Path,
                Title2Path = game.Title2Path,
                IconPath = game.IconPath,
                EncryptionKey = game.EncryptionKey,
                LoadError = game.LoadError,
                Signature = game.Signature
            }).ToList()
        };
        return JsonSerializer.Serialize(file, Options);
    }

    public static void Write(string path, string json)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Library cache not saved: " + ex.Message);
        }
    }
}
