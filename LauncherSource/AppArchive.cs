using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace RpgLauncher;

/// <summary>
/// Reads single files from an HTML game's package: a zip (package.nw, or zipped onto the end of an NW.js exe with
/// "copy /b nw.exe+app.nw game.exe") or an Electron app.asar. Only the index and the requested file are read.
/// </summary>
internal sealed class AppArchive
{
    private const int MaxIndex = 64 * 1024 * 1024;
    private const int MaxFile = 64 * 1024 * 1024;

    // Method: -1 for asar (stored), 0 stored, 8 deflated.
    private readonly record struct Entry(long Offset, long Size, long PackedSize, int Method);

    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;
    private readonly HashSet<string> _folders;

    // Indexes already read, and files that turned out not to be packages, so rescans don't read them again.
    private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, AppArchive? Archive)> Opened =
        new(StringComparer.OrdinalIgnoreCase);

    private AppArchive(string path, Dictionary<string, Entry> entries)
    {
        _path = path;
        _entries = entries;
        _folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in entries.Keys)
        {
            for (int slash = name.IndexOf('/'); slash > 0; slash = name.IndexOf('/', slash + 1))
                _folders.Add(name[..slash]);
        }
    }

    /// <summary>File names, '/'-separated.</summary>
    public IEnumerable<string> Names => _entries.Keys;

    public bool Contains(string name) => _entries.ContainsKey(Normalize(name));

    public bool HasFolder(string name) => _folders.Contains(Normalize(name).TrimEnd('/'));

    /// <summary>The package a picture path points into ("…\Game.exe::data/bgimage/title.jpg"), or null.</summary>
    public static byte[]? ReadPath(string path)
    {
        int split = path.IndexOf(RgssArchive.Separator, StringComparison.Ordinal);
        if (split < 0) return null;
        return Open(path[..split])?.Read(path[(split + RgssArchive.Separator.Length)..]);
    }

    /// <summary>The package in a file: a zip anywhere at its end, or an asar; null for anything else.</summary>
    public static AppArchive? Open(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (Opened.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
                return known.Archive;

            AppArchive? archive;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess))
            {
                var entries = ReadZipIndex(stream) ?? ReadAsarIndex(stream);
                archive = entries is null ? null : new AppArchive(path, entries);
            }
            Opened[path] = (info.Length, info.LastWriteTimeUtc, archive);
            return archive;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // unreadable, or not a package after all
            return null;
        }
    }

    public byte[]? Read(string name)
    {
        if (!_entries.TryGetValue(Normalize(name), out var entry) || entry.Size > MaxFile || entry.PackedSize > MaxFile) return null;
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            long start = entry.Offset;
            if (entry.Method >= 0)
            {
                // The local header repeats the name and has its own extra field; the data follows it.
                var header = new byte[30];
                stream.Position = entry.Offset;
                stream.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034b50) return null;
                start = entry.Offset + 30 + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26)) +
                        BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            }
            stream.Position = start;
            var packed = new byte[entry.PackedSize];
            stream.ReadExactly(packed);
            if (entry.Method is -1 or 0) return packed;

            var data = new byte[entry.Size];
            using var inflater = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
            inflater.ReadExactly(data);
            return data;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // unreadable or damaged
            return null;
        }
    }

    private static string Normalize(string name) => name.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// The central directory of a zip that ends the file. Its offsets count from the zip's own start, which is found
    /// from where the directory ends, so a program in front of the zip doesn't matter. Zip64 isn't read.
    /// </summary>
    private static Dictionary<string, Entry>? ReadZipIndex(FileStream stream)
    {
        long length = stream.Length;
        int tailLength = (int)Math.Min(length, 22 + 65535);
        if (tailLength < 22) return null;
        var tail = new byte[tailLength];
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);

        int end = -1;
        for (int i = tailLength - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50 &&
                i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) <= tailLength)
            {
                end = i;
                break;
            }
        }
        if (end < 0) return null;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        long directorySize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
        long directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        if (count == 0xFFFF || directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF || directorySize > MaxIndex) return null;
        long directoryStart = length - tailLength + end - directorySize;
        long zipStart = directoryStart - directoryOffset;
        if (directoryStart < 0 || zipStart < 0) return null;

        var directory = new byte[directorySize];
        stream.Position = directoryStart;
        stream.ReadExactly(directory);

        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        int at = 0;
        while (at + 46 <= directory.Length && BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at)) == 0x02014b50)
        {
            var span = directory.AsSpan(at);
            int flags = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
            int method = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
            long packedSize = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(span[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(span[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(span[32..]);
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[42..]);
            if (at + 46 + nameLength > directory.Length) break;

            var nameBytes = directory.AsSpan(at + 46, nameLength).ToArray();
            // Bit 11 marks UTF-8 names; zips from Japanese Windows may use Shift-JIS without saying so.
            string name = Normalize((flags & 0x800) != 0 ? Encoding.UTF8.GetString(nameBytes) : TextFiles.Decode(nameBytes));
            bool usable = (flags & 1) == 0 && method is 0 or 8 && !name.EndsWith('/') && name.Length > 0;
            if (usable) entries.TryAdd(name, new Entry(zipStart + localOffset, size, packedSize, method));
            at += 46 + nameLength + extraLength + commentLength;
        }
        return entries.Count > 0 ? entries : null;
    }

    /// <summary>
    /// An Electron asar: a size, then a JSON tree of files ({"files": {name: {"offset": "…", "size": …}}}), then the
    /// files themselves, uncompressed. Files kept in app.asar.unpacked are left out.
    /// </summary>
    private static Dictionary<string, Entry>? ReadAsarIndex(FileStream stream)
    {
        if (stream.Length < 16) return null;
        var head = new byte[16];
        stream.Position = 0;
        stream.ReadExactly(head);
        uint sizeOfSize = BinaryPrimitives.ReadUInt32LittleEndian(head);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        uint jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
        if (sizeOfSize != 4 || jsonLength == 0 || jsonLength > MaxIndex || jsonLength + 8 > headerSize || 8L + headerSize > stream.Length)
            return null;

        var json = new byte[jsonLength];
        stream.ReadExactly(json);
        if (json[0] != (byte)'{') return null;
        long dataStart = 8L + headerSize;

        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json);
            void Walk(JsonElement folder, string prefix)
            {
                if (!folder.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object) return;
                foreach (var item in files.EnumerateObject())
                {
                    string name = prefix + item.Name;
                    if (item.Value.ValueKind != JsonValueKind.Object) continue;
                    if (item.Value.TryGetProperty("files", out _))
                    {
                        Walk(item.Value, name + "/");
                    }
                    else if (item.Value.TryGetProperty("size", out var size) && size.TryGetInt64(out long bytes) &&
                             item.Value.TryGetProperty("offset", out var offset) && offset.ValueKind == JsonValueKind.String &&
                             long.TryParse(offset.GetString(), out long from) &&
                             !(item.Value.TryGetProperty("unpacked", out var unpacked) && unpacked.ValueKind == JsonValueKind.True))
                    {
                        entries.TryAdd(name, new Entry(dataStart + from, bytes, bytes, -1));
                    }
                }
            }
            Walk(document.RootElement, "");
        }
        catch (JsonException)
        {
            return null;
        }
        return entries.Count > 0 ? entries : null;
    }
}
