using System.Buffers.Binary;
using SkiaSharp;

namespace RpgLauncher;

/// <summary>
/// The icon inside a Windows program, its largest image, for games that keep their pictures packed away (Unity).
/// Only the headers and that one image are read.
/// </summary>
internal static class ExeIcon
{
    /// <summary>Marks an exe's icon in a picture path: "…\Game.exe::#icon".</summary>
    public const string Name = "#icon";

    private const int ResourceIcon = 3, ResourceIconGroup = 14;

    public static string PathFor(string exe) => exe + RgssArchive.Separator + Name;

    /// <summary>
    /// True when the exe has an icon of its own: not missing, and not an engine's stock icon, which (like Unity's
    /// cube) is gray.
    /// </summary>
    public static bool HasOwn(string exe)
    {
        if (Read(exe) is not { } png) return false;
        using var icon = ImageKit.Decode(png);
        if (icon is null) return false;
        int opaque = 0, colored = 0;
        for (int y = 0; y < icon.Height; y += 2)
        {
            for (int x = 0; x < icon.Width; x += 2)
            {
                var c = icon.GetPixel(x, y);
                if (c.Alpha < 128) continue;
                opaque++;
                if (Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue)) > 24) colored++;
            }
        }
        return opaque > 0 && colored * 20 > opaque;
    }

    /// <summary>The largest icon as PNG bytes; null when the exe has none or can't be read.</summary>
    public static byte[]? Read(string exe)
    {
        try
        {
            using var stream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            if (Resources(stream) is not { } resources) return null;

            // The group lists each size; its largest entry names the icon resource holding that image.
            if (resources.Find(stream, ResourceIconGroup, null) is not { } group || group.Length < 6) return null;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4));
            int bestId = -1, bestSize = 0;
            for (int i = 0; i < count && 6 + i * 14 + 14 <= group.Length; i++)
            {
                var entry = group.AsSpan(6 + i * 14);
                int width = entry[0] == 0 ? 256 : entry[0], height = entry[1] == 0 ? 256 : entry[1];
                if (width * height <= bestSize) continue;
                bestSize = width * height;
                bestId = BinaryPrimitives.ReadUInt16LittleEndian(entry[12..]);
            }
            if (bestId < 0 || resources.Find(stream, ResourceIcon, bestId) is not { } image) return null;
            if (image.Length > 8 && image[0] == 0x89 && image[1] == (byte)'P' && image[2] == (byte)'N' && image[3] == (byte)'G') return image;
            using var bitmap = FromDib(image);
            if (bitmap is null) return null;
            using var picture = SKImage.FromBitmap(bitmap);
            using var encoded = picture.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // not a Windows program, or damaged
            return null;
        }
    }

    /// <summary>A 32-bit icon image (a BMP's pixels, bottom row first, then a transparency mask) as a bitmap.</summary>
    private static SKBitmap? FromDib(byte[] dib)
    {
        if (dib.Length < 40) return null;
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        int width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)) / 2;
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        if (bits != 32 || width <= 0 || height <= 0 || width > 1024 || height > 1024 || headerSize + width * height * 4 > dib.Length) return null;

        var pixels = dib.AsSpan(headerSize, width * height * 4);
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { anyAlpha = true; break; }
        // Old icons leave alpha at zero and use the 1-bit mask after the pixels (rows padded to 4 bytes).
        int maskStride = (width + 31) / 32 * 4;
        int maskStart = headerSize + width * height * 4;
        bool hasMask = maskStart + maskStride * height <= dib.Length;

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        for (int y = 0; y < height; y++)
        {
            int source = (height - 1 - y) * width * 4;
            for (int x = 0; x < width; x++)
            {
                byte b = pixels[source + x * 4], g = pixels[source + x * 4 + 1], r = pixels[source + x * 4 + 2];
                byte a = pixels[source + x * 4 + 3];
                if (!anyAlpha)
                {
                    bool transparent = hasMask && (dib[maskStart + (height - 1 - y) * maskStride + x / 8] & (0x80 >> (x % 8))) != 0;
                    a = transparent ? (byte)0 : (byte)255;
                }
                bitmap.SetPixel(x, y, new SKColor(r, g, b, a));
            }
        }
        return bitmap;
    }

    /// <summary>The exe's resource section: where it is in the file and its address in memory.</summary>
    private static ResourceTable? Resources(FileStream stream)
    {
        var dos = Read(stream, 0, 64);
        if (dos[0] != 'M' || dos[1] != 'Z') return null;
        int pe = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3C));
        var header = Read(stream, pe, 24);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x00004550) return null; // "PE\0\0"
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20));
        var optional = Read(stream, pe + 24, optionalSize);
        int magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
        int directories = magic == 0x20B ? 112 : 96;
        if (directories + 3 * 8 > optional.Length) return null;
        uint resourceRva = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(directories + 2 * 8));
        if (resourceRva == 0) return null;

        var table = Read(stream, pe + 24 + optionalSize, sections * 40);
        for (int i = 0; i < sections; i++)
        {
            var section = table.AsSpan(i * 40);
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(section[8..]);
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(section[12..]);
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(section[16..]);
            uint rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(section[20..]);
            if (resourceRva >= virtualAddress && resourceRva < virtualAddress + Math.Max(virtualSize, rawSize))
                return new ResourceTable(rawPointer + (resourceRva - virtualAddress), virtualAddress, rawPointer);
        }
        return null;
    }

    private static byte[] Read(FileStream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > stream.Length) throw new InvalidDataException("outside the file");
        var bytes = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>The resource tree: type, then name or number, then language, then the data.</summary>
    private sealed record ResourceTable(long Root, uint SectionAddress, uint SectionPointer)
    {
        /// <summary>The first resource of a type (with this number, when given), any language.</summary>
        public byte[]? Find(FileStream stream, int type, int? id)
        {
            if (Child(stream, 0, type) is not { } names) return null;
            uint? entry = id is { } number ? Child(stream, names, number) : FirstChild(stream, names);
            if (entry is not { } languages || FirstChild(stream, languages) is not { } leaf) return null;
            var data = Read(stream, Root + leaf, 16);
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(data);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
            if (size <= 0 || size > 16 * 1024 * 1024) return null;
            return Read(stream, rva - SectionAddress + SectionPointer, size);
        }

        // A directory's entries: 16 bytes of header (named and numbered counts at 12 and 14), then 8 bytes each.
        private IEnumerable<(uint Id, uint Target)> Entries(FileStream stream, uint directory)
        {
            var header = Read(stream, Root + directory, 16);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
            var entries = Read(stream, Root + directory + 16, Math.Min(count, 4096) * 8);
            for (int i = 0; i < entries.Length; i += 8)
                yield return (BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i)), BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i + 4)));
        }

        private uint? Child(FileStream stream, uint directory, int id)
        {
            foreach (var (entryId, target) in Entries(stream, directory))
                if (entryId == id) return target & 0x7FFFFFFF;
            return null;
        }

        private uint? FirstChild(FileStream stream, uint directory)
        {
            foreach (var (_, target) in Entries(stream, directory)) return target & 0x7FFFFFFF;
            return null;
        }
    }
}
