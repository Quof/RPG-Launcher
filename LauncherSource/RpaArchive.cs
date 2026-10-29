using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace RpgLauncher;

/// <summary>
/// Reads single files from Ren'Py archives (.rpa). The header names where the index is: a zlib-compressed Python
/// pickle of {name: [(offset, length, prefix)]}. RPA-3.0 XORs offsets and lengths with the header's key; RPA-2.0
/// doesn't. The pickle is read by a small reader that knows plain values only, so nothing in an index can run.
/// </summary>
internal sealed class RpaArchive
{
    private const int MaxIndex = 64 * 1024 * 1024;
    private const int MaxFile = 64 * 1024 * 1024;

    private readonly record struct Entry(long Offset, long Length, byte[] Prefix);

    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;

    // Indexes already read, and files that aren't archives, so rescans don't read them again.
    private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, RpaArchive? Archive)> Opened =
        new(StringComparer.OrdinalIgnoreCase);

    private RpaArchive(string path, Dictionary<string, Entry> entries)
    {
        _path = path;
        _entries = entries;
    }

    /// <summary>File names, '/'-separated, relative to the game folder.</summary>
    public IEnumerable<string> Names => _entries.Keys;

    public bool Contains(string name) => _entries.ContainsKey(Normalize(name));

    public static RpaArchive? Open(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (Opened.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
                return known.Archive;

            RpaArchive? archive = null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess))
            {
                if (ReadIndex(stream) is { Count: > 0 } entries) archive = new RpaArchive(path, entries);
            }
            Opened[path] = (info.Length, info.LastWriteTimeUtc, archive);
            return archive;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // unreadable, or not an archive Ren'Py writes
            return null;
        }
    }

    public byte[]? Read(string name)
    {
        if (!_entries.TryGetValue(Normalize(name), out var entry) || entry.Length > MaxFile) return null;
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            // The prefix, kept in the index, is the start of the file; the rest follows at the offset.
            var data = new byte[entry.Length];
            int prefix = Math.Min(entry.Prefix.Length, data.Length);
            entry.Prefix.AsSpan(0, prefix).CopyTo(data);
            stream.Position = entry.Offset;
            stream.ReadExactly(data, prefix, data.Length - prefix);
            return data;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static string Normalize(string name) => name.Replace('\\', '/').TrimStart('/');

    private static Dictionary<string, Entry>? ReadIndex(FileStream stream)
    {
        // "RPA-3.0 <offset, 16 hex digits> <key, 8 hex digits>\n"; the key can be split into several words to XOR.
        var head = new byte[Math.Min(256, stream.Length)];
        stream.ReadExactly(head);
        int end = Array.IndexOf(head, (byte)'\n');
        if (end < 0) return null;
        string[] words = Encoding.ASCII.GetString(head, 0, end).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words[0] is not ("RPA-3.0" or "RPA-2.0")) return null;
        long offset = long.Parse(words[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        long key = 0;
        if (words[0] == "RPA-3.0")
            foreach (string word in words.Skip(2))
                key ^= long.Parse(word, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (offset <= 0 || offset >= stream.Length || stream.Length - offset > MaxIndex) return null;

        var compressed = new byte[stream.Length - offset];
        stream.Position = offset;
        stream.ReadExactly(compressed);
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress))
            zlib.CopyTo(inflated);
        if (inflated.Length > MaxIndex) return null;

        if (new PlainPickle(inflated.ToArray()).Load() is not Dictionary<object, object?> index) return null;
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in index)
        {
            string? file = name switch { string text => text, byte[] bytes => TextFiles.Decode(bytes), _ => null };
            // Each name maps to a list of pieces; files Ren'Py writes have one.
            if (file is null || value is not List<object?> { Count: > 0 } pieces || pieces[0] is not object?[] { Length: >= 2 } piece) continue;
            if (Number(piece[0]) is not { } at || Number(piece[1]) is not { } length) continue;
            byte[] prefix = piece.Length > 2 ? piece[2] switch { byte[] bytes => bytes, string text => Encoding.Latin1.GetBytes(text), _ => Array.Empty<byte>() }
                : Array.Empty<byte>();
            long start = at ^ key, size = length ^ key;
            if (start < 0 || size < 0 || start + size - prefix.Length > stream.Length) continue;
            entries.TryAdd(Normalize(file), new Entry(start, size, prefix));
        }
        return entries;
    }

    private static long? Number(object? value) => value switch
    {
        long number => number,
        BigInteger big when big >= long.MinValue && big <= long.MaxValue => (long)big,
        _ => null
    };

    /// <summary>
    /// A Python pickle of plain values only (dicts, lists, tuples, text, bytes, numbers): any other opcode, including
    /// every one that would name a class or call a function, stops the reading.
    /// </summary>
    private sealed class PlainPickle
    {
        private static readonly object MarkObject = new();
        private readonly byte[] _data;
        private int _at;
        private readonly List<object?> _stack = new();
        private readonly Dictionary<long, object?> _memo = new();

        public PlainPickle(byte[] data) => _data = data;

        public object? Load()
        {
            while (_at < _data.Length)
            {
                byte op = _data[_at++];
                switch (op)
                {
                    case 0x80: _at++; break; // PROTO
                    case 0x95: _at += 8; break; // FRAME
                    case (byte)'}': Push(new Dictionary<object, object?>()); break; // EMPTY_DICT
                    case (byte)']': Push(new List<object?>()); break; // EMPTY_LIST
                    case (byte)')': Push(Array.Empty<object?>()); break; // EMPTY_TUPLE
                    case (byte)'(': Push(MarkObject); break; // MARK
                    case (byte)'N': Push(null); break; // NONE
                    case 0x88: Push(true); break; // NEWTRUE
                    case 0x89: Push(false); break; // NEWFALSE
                    case (byte)'J': Push((long)BinaryPrimitives.ReadInt32LittleEndian(Take(4))); break; // BININT
                    case (byte)'K': Push((long)Take(1)[0]); break; // BININT1
                    case (byte)'M': Push((long)BinaryPrimitives.ReadUInt16LittleEndian(Take(2))); break; // BININT2
                    case 0x8a: Push(Long(Take(Take(1)[0]))); break; // LONG1
                    case 0x8b: Push(Long(Take(Length4()))); break; // LONG4
                    case (byte)'G': Push(BinaryPrimitives.ReadDoubleBigEndian(Take(8))); break; // BINFLOAT
                    case 0x8c: Push(Encoding.UTF8.GetString(Take(Take(1)[0]))); break; // SHORT_BINUNICODE
                    case (byte)'X': Push(Encoding.UTF8.GetString(Take(Length4()))); break; // BINUNICODE
                    case 0x8d: Push(Encoding.UTF8.GetString(Take(Length8()))); break; // BINUNICODE8
                    case (byte)'U': case (byte)'C': Push(Take(Take(1)[0]).ToArray()); break; // SHORT_BINSTRING, SHORT_BINBYTES
                    case (byte)'T': case (byte)'B': Push(Take(Length4()).ToArray()); break; // BINSTRING, BINBYTES
                    case 0x8e: Push(Take(Length8()).ToArray()); break; // BINBYTES8
                    case 0x85: Push(new[] { Pop() }); break; // TUPLE1
                    case 0x86: { var b = Pop(); var a = Pop(); Push(new[] { a, b }); break; } // TUPLE2
                    case 0x87: { var c = Pop(); var b = Pop(); var a = Pop(); Push(new[] { a, b, c }); break; } // TUPLE3
                    case (byte)'t': Push(PopToMark().ToArray()); break; // TUPLE
                    case (byte)'a': { var item = Pop(); List().Add(item); break; } // APPEND
                    case (byte)'e': { var items = PopToMark(); List().AddRange(items); break; } // APPENDS
                    case (byte)'s': { var value = Pop(); var key = Pop(); Dict()[Key(key)] = value; break; } // SETITEM
                    case (byte)'u': // SETITEMS
                    {
                        var items = PopToMark();
                        var dict = Dict();
                        for (int i = 0; i + 1 < items.Count; i += 2) dict[Key(items[i])] = items[i + 1];
                        break;
                    }
                    case 0x94: _memo[_memo.Count] = Peek(); break; // MEMOIZE
                    case (byte)'q': _memo[Take(1)[0]] = Peek(); break; // BINPUT
                    case (byte)'r': _memo[BinaryPrimitives.ReadUInt32LittleEndian(Take(4))] = Peek(); break; // LONG_BINPUT
                    case (byte)'h': Push(_memo[Take(1)[0]]); break; // BINGET
                    case (byte)'j': Push(_memo[BinaryPrimitives.ReadUInt32LittleEndian(Take(4))]); break; // LONG_BINGET
                    case (byte)'.': return _stack.Count > 0 ? Pop() : null; // STOP
                    default: throw new InvalidDataException($"pickle opcode 0x{op:x2} isn't a plain value");
                }
            }
            throw new InvalidDataException("pickle without STOP");
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _at + count > _data.Length) throw new InvalidDataException("pickle ends early");
            var span = _data.AsSpan(_at, count);
            _at += count;
            return span;
        }

        private int Length4() => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(Take(4)));

        private int Length8() => checked((int)BinaryPrimitives.ReadUInt64LittleEndian(Take(8)));

        private static object Long(ReadOnlySpan<byte> bytes)
        {
            var big = new BigInteger(bytes, isUnsigned: false, isBigEndian: false);
            return big >= long.MinValue && big <= long.MaxValue ? (long)big : big;
        }

        private void Push(object? value) => _stack.Add(value);

        private object? Peek() => _stack.Count > 0 ? _stack[^1] : throw new InvalidDataException("pickle stack empty");

        private object? Pop()
        {
            var value = Peek();
            _stack.RemoveAt(_stack.Count - 1);
            if (ReferenceEquals(value, MarkObject)) throw new InvalidDataException("pickle mark misplaced");
            return value;
        }

        private List<object?> PopToMark()
        {
            int mark = _stack.FindLastIndex(item => ReferenceEquals(item, MarkObject));
            if (mark < 0) throw new InvalidDataException("pickle mark missing");
            var items = _stack.GetRange(mark + 1, _stack.Count - mark - 1);
            _stack.RemoveRange(mark, _stack.Count - mark);
            return items;
        }

        private List<object?> List() => Peek() as List<object?> ?? throw new InvalidDataException("pickle append to a non-list");

        private Dictionary<object, object?> Dict() =>
            Peek() as Dictionary<object, object?> ?? throw new InvalidDataException("pickle item set on a non-dict");

        // Keys are compared by value: bytes keys become text.
        private static object Key(object? key) => key switch
        {
            null => throw new InvalidDataException("pickle key is None"),
            byte[] bytes => TextFiles.Decode(bytes),
            _ => key
        };
    }
}
