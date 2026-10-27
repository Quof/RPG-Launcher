using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace RpgLauncher;

/// <summary>RPG Maker 2000 / 2003 files: the database (RPG_RT.ldb) and the XYZ picture format.</summary>
internal static class Rm2kFiles
{
    private const int SystemChunk = 0x16;
    private const int TitleNameChunk = 0x11;
    // Sections only RPG Maker 2003 writes: battle commands and classes.
    private static readonly int[] Rm2003Chunks = { 0x1D, 0x1E };

    /// <summary>The title picture's name and whether the game was made with RPG Maker 2003.</summary>
    public static (string? TitleName, bool Is2003) ReadDatabase(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            int pos = 0;
            int headerLength = ReadNumber(data, ref pos);
            pos += headerLength; // "LcfDataBase"

            string? title = null;
            bool is2003 = false;
            foreach (var (id, start, length) in Chunks(data, pos, data.Length))
            {
                if (Rm2003Chunks.Contains(id) && length > 1) is2003 = true;
                if (id != SystemChunk) continue;
                foreach (var (field, fieldStart, fieldLength) in Chunks(data, start, start + length))
                {
                    if (field == TitleNameChunk)
                        title = TextFiles.Decode(data.AsSpan(fieldStart, fieldLength).ToArray());
                }
            }
            return (string.IsNullOrWhiteSpace(title) ? null : title, is2003);
        }
        catch
        {
            return (null, false);
        }
    }

    // A chunk is a number (its id), a number (its length), then that many bytes.
    private static IEnumerable<(int Id, int Start, int Length)> Chunks(byte[] data, int pos, int end)
    {
        while (pos < end)
        {
            int id = ReadNumber(data, ref pos);
            if (id == 0) yield break;
            int length = ReadNumber(data, ref pos);
            if (length < 0 || pos + length > end) yield break;
            yield return (id, pos, length);
            pos += length;
        }
    }

    // Seven bits per byte, the top bit saying another byte follows.
    private static int ReadNumber(byte[] data, ref int pos)
    {
        int value = 0;
        for (int i = 0; i < 5 && pos < data.Length; i++)
        {
            byte b = data[pos++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) return value;
        }
        return value;
    }

    public static bool IsXyz(byte[] bytes) =>
        bytes.Length > 8 && bytes[0] == (byte)'X' && bytes[1] == (byte)'Y' && bytes[2] == (byte)'Z' && bytes[3] == (byte)'1';

    /// <summary>
    /// Decodes an XYZ picture: "XYZ1", width and height, then zlib-compressed data holding a
    /// 256-colour palette and one palette index per pixel.
    /// </summary>
    public static Bitmap? DecodeXyz(byte[] bytes)
    {
        try
        {
            int width = BitConverter.ToUInt16(bytes, 4), height = BitConverter.ToUInt16(bytes, 6);
            // 2000/2003 pictures are small; refuse sizes that would only come from a damaged file.
            if (width == 0 || height == 0 || (long)width * height > 4096 * 4096) return null;
            using var input = new MemoryStream(bytes, 8, bytes.Length - 8, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var raw = new byte[768 + width * height];
            zlib.ReadExactly(raw);

            var pixels = new int[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                int c = raw[768 + i] * 3;
                pixels[i] = unchecked((int)0xFF000000) | (raw[c] << 16) | (raw[c + 1] << 8) | raw[c + 2];
            }

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var lockData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < height; y++)
                Marshal.Copy(pixels, y * width, lockData.Scan0 + y * lockData.Stride, width);
            bitmap.UnlockBits(lockData);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
