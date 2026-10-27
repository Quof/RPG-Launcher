using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RpgLauncher;

internal sealed class LauncherStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string FilePath { get; }
    public LauncherData Data { get; private set; } = new();
    public string? LoadWarning { get; set; }

    public LauncherStore(string filePath)
    {
        FilePath = filePath;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Data = new LauncherData();
                return;
            }

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<LauncherData>(json, Options) ?? new LauncherData();
            if (data.GameDirectories is null) data.GameDirectories = new();
            data.Groups = (data.Groups ?? new()).Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            if (data.ActiveGroup is not null &&
                !data.Groups.Contains(data.ActiveGroup, StringComparer.CurrentCultureIgnoreCase))
                data.ActiveGroup = null;
            if (data.Games is null) data.Games = new(StringComparer.OrdinalIgnoreCase);
            else if (!ReferenceEquals(data.Games.Comparer, StringComparer.OrdinalIgnoreCase))
                data.Games = new Dictionary<string, GameRecord>(data.Games, StringComparer.OrdinalIgnoreCase);
            Data = data;
        }
        catch (Exception ex)
        {
            LoadWarning = "launcher-data.json could not be read (" + ex.Message + "). Starting with a fresh library.";
            TryBackupBrokenFile();
            Data = new LauncherData();
        }
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var backup = FilePath + ".broken";
            File.Copy(FilePath, backup, overwrite: true);
        }
        catch
        {
            // best effort
        }
    }

    private readonly object _writeLock = new();
    private long _snapshotVersion;
    private long _writtenVersion;

    /// <summary>Saves synchronously on the calling thread.</summary>
    public bool Save()
    {
        string json;
        long version;
        try
        {
            (json, version) = Snapshot();
        }
        catch
        {
            return false;
        }
        return Write(json, version);
    }

    /// <summary>Serializes <see cref="Data"/> (call on the UI thread) for <see cref="Write"/> on any thread.</summary>
    public (string Json, long Version) Snapshot()
    {
        var json = JsonSerializer.Serialize(Data, Options);
        return (json, ++_snapshotVersion);
    }

    /// <summary>Writes a snapshot; an older snapshot never overwrites a newer one.</summary>
    public bool Write(string json, long version)
    {
        lock (_writeLock)
        {
            if (version <= _writtenVersion) return true;
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                File.Move(temp, FilePath, overwrite: true);
                _writtenVersion = version;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
