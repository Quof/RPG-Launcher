using System.Reflection;
using System.Text.Json;

namespace RpgLauncher;

/// <summary>
/// Server games removed as duplicates of another copy (Assets/vip-moved.json: old key → the kept copy's key). Saved
/// favorites, completed and hidden marks, groups and playlists that still name a removed copy are pointed at the kept
/// one, so they don't turn into games the launcher can't find.
/// </summary>
internal static class VipMoved
{
    private sealed class File
    {
        public Dictionary<string, string> Moved { get; set; } = new();
    }

    private static readonly Lazy<Dictionary<string, string>> Map = new(Load);

    private static Dictionary<string, string> Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RpgLauncher.Assets.vip-moved.json");
            if (stream is not null && JsonSerializer.Deserialize<File>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) is { } file)
                return new Dictionary<string, string>(file.Moved, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Diagnostics.Log("Moved-games list not loaded: " + ex.Message);
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Repoints the saved keys; true when anything changed (then the data should be saved).</summary>
    public static bool Apply(VipData data)
    {
        var map = Map.Value;
        if (map.Count == 0) return false;
        bool changed = false;

        void Fix(List<string> keys)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (!map.TryGetValue(keys[i], out var kept)) continue;
                keys[i] = kept;
                changed = true;
            }
            // A list that had both copies now has the kept one twice.
            int before = keys.Count;
            var distinct = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count != before)
            {
                keys.Clear();
                keys.AddRange(distinct);
            }
        }

        Fix(data.Favorites);
        Fix(data.Completed);
        Fix(data.Hidden);
        foreach (var playlist in data.Playlists) Fix(playlist.Games);
        foreach (var old in data.GroupMembers.Keys.Where(map.ContainsKey).ToList())
        {
            var groups = data.GroupMembers[old];
            data.GroupMembers.Remove(old);
            string kept = map[old];
            if (data.GroupMembers.TryGetValue(kept, out var existing))
                existing.AddRange(groups.Where(g => !existing.Contains(g, StringComparer.Ordinal)));
            else data.GroupMembers[kept] = groups;
            changed = true;
        }
        foreach (var (folder, key) in data.Downloaded.ToList())
        {
            // Downloads are recorded by server path, without "dl:".
            // and festival works by "matsuri/No", as their kept keys are.
            if (!map.TryGetValue("dl:" + key, out var kept)) continue;
            data.Downloaded[folder] = kept.StartsWith("dl:", StringComparison.Ordinal) ? kept[3..] : kept;
            changed = true;
        }
        return changed;
    }
}
