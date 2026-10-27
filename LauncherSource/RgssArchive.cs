using System.Collections.Concurrent;
using System.Text;

namespace RpgLauncher;

/// <summary>
/// Reads single files from RPG Maker XP, VX and VX Ace archives (Game.rgssad, .rgss2a, .rgss3a),
/// touching only the index and the requested file.
/// </summary>
internal sealed class RgssArchive
{
    /// <summary>Joins archive and inner file in picture paths: "…\Game.rgss3a::Graphics\Titles1\Title.png".</summary>
    public const string Separator = "::";

    private const uint XpVxKey = 0xDEADCAFE;

    private readonly record struct Entry(long Offset, int Size, uint Key);

    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;

    // Indexes already read, so building several pictures doesn't walk an archive again.
    private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, RgssArchive Archive)> Opened =
        new(StringComparer.OrdinalIgnoreCase);

    private RgssArchive(string path, Dictionary<string, Entry> entries)
    {
        _path = path;
        _entries = entries;
    }

    /// <summary>The archive file a picture path points into, or the path itself for an ordinary file.</summary>
    public static string FileOf(string path)
    {
        int split = path.IndexOf(Separator, StringComparison.Ordinal);
        return split < 0 ? path : path[..split];
    }

    /// <summary>A file's bytes from an archive picture path; null for ordinary paths or missing files.</summary>
    public static byte[]? ReadPath(string path)
    {
        int split = path.IndexOf(Separator, StringComparison.Ordinal);
        if (split < 0) return null;
        return Open(path[..split])?.Read(path[(split + Separator.Length)..]);
    }

    public static RgssArchive? Open(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (Opened.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
                return known.Archive;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            using var reader = new BinaryReader(stream);
            var magic = reader.ReadBytes(8);
            if (magic.Length < 8 || Encoding.ASCII.GetString(magic, 0, 6) != "RGSSAD") return null;

            var entries = magic[7] == 3 ? ReadIndexVxAce(reader) : ReadIndexXpVx(reader);
            var archive = new RgssArchive(path, entries);
            Opened[path] = (info.Length, info.LastWriteTimeUtc, archive);
            return archive;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the archive holds a file of that name (Graphics\Titles1\Title.png).</summary>
    public bool Contains(string name) => _entries.ContainsKey(Normalize(name));

    public byte[]? Read(string name)
    {
        if (!_entries.TryGetValue(Normalize(name), out var entry)) return null;
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(entry.Offset, SeekOrigin.Begin);
            var data = new byte[entry.Size];
            stream.ReadExactly(data);

            // Each file is XORed with a key that moves on every four bytes.
            uint key = entry.Key;
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0 && i % 4 == 0) key = key * 7 + 3;
                data[i] ^= (byte)(key >> (8 * (i % 4)));
            }
            return data;
        }
        catch
        {
            return null;
        }
    }

    // XP (.rgssad) and VX (.rgss2a): each file's name and size sit just before its data,
    // encrypted with one running key.
    private static Dictionary<string, Entry> ReadIndexXpVx(BinaryReader reader)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var stream = reader.BaseStream;
        uint key = XpVxKey;
        while (stream.Position + 4 <= stream.Length)
        {
            uint nameLength = reader.ReadUInt32() ^ key;
            key = key * 7 + 3;
            if (nameLength == 0 || nameLength > 1024) break;

            var name = reader.ReadBytes((int)nameLength);
            for (int i = 0; i < name.Length; i++)
            {
                name[i] ^= (byte)key;
                key = key * 7 + 3;
            }

            uint size = reader.ReadUInt32() ^ key;
            key = key * 7 + 3;
            long offset = stream.Position;
            if (offset + size > stream.Length) break;

            entries[Normalize(TextFiles.Decode(name))] = new Entry(offset, (int)size, key);
            stream.Seek(size, SeekOrigin.Current);
        }
        return entries;
    }

    // VX Ace (.rgss3a): one index at the start listing every file's offset, size and key.
    private static Dictionary<string, Entry> ReadIndexVxAce(BinaryReader reader)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        uint key = reader.ReadUInt32() * 9 + 3;
        while (reader.BaseStream.Position + 16 <= reader.BaseStream.Length)
        {
            uint offset = reader.ReadUInt32() ^ key;
            if (offset == 0) break;
            uint size = reader.ReadUInt32() ^ key;
            uint fileKey = reader.ReadUInt32() ^ key;
            uint nameLength = reader.ReadUInt32() ^ key;
            if (nameLength > 1024) break;

            var name = reader.ReadBytes((int)nameLength);
            for (int i = 0; i < name.Length; i++)
                name[i] ^= (byte)(key >> (8 * (i % 4)));
            entries[Normalize(TextFiles.Decode(name))] = new Entry(offset, (int)size, fileKey);
        }
        return entries;
    }

    private static string Normalize(string name) => name.Replace('/', '\\');

    /// <summary>
    /// A string field such as "@title1_name" from System.rxdata/.rvdata/.rvdata2 (Ruby Marshal), or null.
    /// Marshal writes a field name in full only where it first appears, which for these is the System object.
    /// </summary>
    public static string? SystemString(byte[] data, string field)
    {
        int at = data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(field));
        if (at < 0) return null;
        int i = at + field.Length;
        if (i < data.Length && data[i] == (byte)'I') i++;
        if (i >= data.Length || data[i] != (byte)'"') return null;
        i++;
        if (i >= data.Length) return null;

        // Marshal's packed length: 0, 1–4 following bytes, or the byte minus 5.
        int lead = (sbyte)data[i++];
        int length;
        if (lead == 0) length = 0;
        else if (lead > 4) length = lead - 5;
        else if (lead is >= 1 and <= 4 && i + lead <= data.Length)
        {
            length = 0;
            for (int b = 0; b < lead; b++) length |= data[i + b] << (8 * b);
            i += lead;
        }
        else return null;

        if (length < 0 || i + length > data.Length) return null;
        return TextFiles.Decode(data.AsSpan(i, length).ToArray());
    }
}
