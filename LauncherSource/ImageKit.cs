using System.Runtime.InteropServices;
using Vector4 = System.Numerics.Vector4;
using SkiaSharp;

namespace RpgLauncher;

/// <summary>Picture decoding and processing with SkiaSharp (safe off the UI thread), and conversion to Avalonia bitmaps.</summary>
internal static class ImageKit
{
    /// <summary>Decodes PNG, JPEG, WebP, BMP, GIF or ICO data into a premultiplied BGRA bitmap; null if it isn't a picture.</summary>
    public static SKBitmap? Decode(byte[] bytes)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return null;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput) return bitmap;
            bitmap.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A blank premultiplied BGRA bitmap, optionally filled.</summary>
    public static SKBitmap Create(int width, int height, Color? fill = null)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, width), Math.Max(1, height), SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(fill is { } color ? ToSk(color) : SKColors.Transparent);
        return bitmap;
    }

    public static SKColor ToSk(Color color) => new(color.R, color.G, color.B, color.A);

    /// <summary>Draws part of a picture into a rectangle of the canvas, scaled sharply (<see cref="Resample(SKBitmap, SKRect, int, int)"/>).</summary>
    public static void Draw(SKCanvas canvas, SKBitmap source, SKRect sourceRect, SKRect dest)
    {
        int width = Math.Max(1, (int)Math.Round(dest.Width)), height = Math.Max(1, (int)Math.Round(dest.Height));
        using var scaled = Resample(source, sourceRect, width, height);
        canvas.DrawBitmap(scaled, (float)Math.Round(dest.Left), (float)Math.Round(dest.Top));
    }

    /// <summary>
    /// Scales part of a picture to an exact pixel size with a bicubic filter that widens when shrinking, as GDI+'s
    /// HighQualityBicubic did. Skia's own high-quality drawing shrinks through mipmaps, which softens pictures.
    /// </summary>
    public static SKBitmap Resample(SKBitmap source, SKRect from, int width, int height)
    {
        var pixels = ResamplePixels(source.GetPixelSpan(), source.Width, source.Height, source.RowBytes,
            from.Left, from.Top, from.Width, from.Height, width, height);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, source.ColorType, source.AlphaType));
        for (int y = 0; y < height; y++)
            Marshal.Copy(pixels, y * width * 4, bitmap.GetPixels() + y * bitmap.RowBytes, width * 4);
        return bitmap;
    }

    /// <summary>
    /// Part of an Avalonia picture (<paramref name="from"/> in its units) scaled the same way to <paramref name="size"/>
    /// pixels, for a screen with <paramref name="scale"/> pixels per unit, so it can be drawn 1:1.
    /// </summary>
    public static Bitmap Resample(Bitmap source, Rect from, PixelSize size, double scale)
    {
        var format = source.Format ?? Avalonia.Platform.PixelFormat.Bgra8888;
        var alpha = source.AlphaFormat ?? Avalonia.Platform.AlphaFormat.Premul;
        if (format.BitsPerPixel != 32) return source;

        var pixelSize = source.PixelSize;
        int stride = pixelSize.Width * 4;
        var buffer = new byte[stride * pixelSize.Height];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            source.CopyPixels(new PixelRect(pixelSize), pin.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            pin.Free();
        }

        double sx = pixelSize.Width / source.Size.Width, sy = pixelSize.Height / source.Size.Height;
        var pixels = ResamplePixels(buffer, pixelSize.Width, pixelSize.Height, stride,
            from.X * sx, from.Y * sy, from.Width * sx, from.Height * sy, size.Width, size.Height);
        pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(format, alpha, pin.AddrOfPinnedObject(), size, new Vector(96 * scale, 96 * scale), size.Width * 4);
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>The resampling itself, on 32-bit premultiplied pixels in any channel order (the fourth is alpha).</summary>
    private static byte[] ResamplePixels(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int sourceStride,
        double fromX, double fromY, double fromWidth, double fromHeight, int width, int height)
    {
        var columns = Taps.For(sourceWidth, fromX, fromWidth, width);
        var rows = Taps.For(sourceHeight, fromY, fromHeight, height);

        // Across first, on just the source rows the second pass reads.
        int firstRow = rows.First, rowCount = rows.Last - firstRow + 1;
        var line = new Vector4[sourceWidth];
        var across = new Vector4[rowCount * width];
        for (int r = 0; r < rowCount; r++)
        {
            var row = source.Slice((firstRow + r) * sourceStride, sourceWidth * 4);
            for (int x = 0; x < sourceWidth; x++)
                line[x] = new Vector4(row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]);
            int output = r * width;
            for (int x = 0; x < width; x++)
            {
                var sum = Vector4.Zero;
                int tap = x * columns.Stride, end = tap + columns.Count[x];
                for (; tap < end; tap++) sum += line[columns.Index[tap]] * columns.Weight[tap];
                across[output + x] = sum;
            }
        }

        // Then down, a row at a time; bicubic overshoot is clipped, and colors never exceed their alpha.
        var result = new byte[width * height * 4];
        var sums = new Vector4[width];
        for (int y = 0; y < height; y++)
        {
            Array.Clear(sums);
            int tap = y * rows.Stride, end = tap + rows.Count[y];
            for (; tap < end; tap++)
            {
                int from = (rows.Index[tap] - firstRow) * width;
                float weight = rows.Weight[tap];
                for (int x = 0; x < width; x++) sums[x] += across[from + x] * weight;
            }
            int output = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                var sum = sums[x];
                float a = Math.Clamp(sum.W, 0f, 255f);
                result[output + x * 4] = (byte)(Math.Clamp(sum.X, 0f, a) + 0.5f);
                result[output + x * 4 + 1] = (byte)(Math.Clamp(sum.Y, 0f, a) + 0.5f);
                result[output + x * 4 + 2] = (byte)(Math.Clamp(sum.Z, 0f, a) + 0.5f);
                result[output + x * 4 + 3] = (byte)(a + 0.5f);
            }
        }
        return result;
    }

    /// <summary>For each output pixel along one axis, the source pixels it reads and their weights.</summary>
    private sealed class Taps
    {
        public int Stride;
        public int[] Count = Array.Empty<int>();
        public int[] Index = Array.Empty<int>();
        public float[] Weight = Array.Empty<float>();
        public int First = int.MaxValue, Last = int.MinValue;

        public static Taps For(int sourceSize, double from, double fromSize, int size)
        {
            double step = fromSize / size;
            // When shrinking, the filter spreads over every source pixel an output pixel covers.
            double widen = Math.Max(1, step);
            double reach = 2 * widen;
            var taps = new Taps { Stride = (int)Math.Ceiling(reach * 2) + 2 };
            taps.Count = new int[size];
            taps.Index = new int[size * taps.Stride];
            taps.Weight = new float[size * taps.Stride];
            for (int i = 0; i < size; i++)
            {
                double center = from + (i + 0.5) * step;
                int lo = (int)Math.Floor(center - reach), hi = (int)Math.Ceiling(center + reach);
                int start = i * taps.Stride, n = 0;
                double total = 0;
                for (int j = lo; j <= hi && n < taps.Stride; j++)
                {
                    double weight = CatmullRom((j + 0.5 - center) / widen);
                    if (weight == 0) continue;
                    int index = Math.Clamp(j, 0, sourceSize - 1);
                    taps.Index[start + n] = index;
                    taps.Weight[start + n] = (float)weight;
                    taps.First = Math.Min(taps.First, index);
                    taps.Last = Math.Max(taps.Last, index);
                    total += weight;
                    n++;
                }
                for (int k = 0; k < n; k++) taps.Weight[start + k] = (float)(taps.Weight[start + k] / total);
                taps.Count[i] = n;
            }
            return taps;
        }

        private static double CatmullRom(double x)
        {
            x = Math.Abs(x);
            if (x < 1) return (1.5 * x - 2.5) * x * x + 1;
            if (x < 2) return ((-0.5 * x + 2.5) * x - 4) * x + 2;
            return 0;
        }
    }

    /// <summary>Fills <paramref name="dest"/> with the picture, cropping what doesn't fit.</summary>
    public static void DrawCover(SKCanvas canvas, SKBitmap source, SKRect dest)
    {
        float scale = Math.Max(dest.Width / source.Width, dest.Height / source.Height);
        float w = dest.Width / scale, h = dest.Height / scale;
        Draw(canvas, source, SKRect.Create((source.Width - w) / 2f, (source.Height - h) / 2f, w, h), dest);
    }

    /// <summary>A copy of part of a picture.</summary>
    public static SKBitmap Crop(SKBitmap source, SKRectI area)
    {
        var copy = Create(area.Width, area.Height);
        using var canvas = new SKCanvas(copy);
        canvas.DrawBitmap(source, area, SKRect.Create(area.Width, area.Height));
        return copy;
    }

    /// <summary>An Avalonia bitmap with the same pixels, for showing on screen.</summary>
    public static Bitmap ToAvalonia(SKBitmap bitmap)
    {
        if (bitmap.ColorType != SKColorType.Bgra8888 || bitmap.AlphaType != SKAlphaType.Premul)
        {
            using var converted = Create(bitmap.Width, bitmap.Height);
            using (var canvas = new SKCanvas(converted)) canvas.DrawBitmap(bitmap, 0, 0);
            return ToAvalonia(converted);
        }
        return new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul, bitmap.GetPixels(),
            new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), bitmap.RowBytes);
    }

    /// <summary>Writes the picture as a JPEG through a temporary file, so a crash never leaves half a file.</summary>
    public static void SaveJpeg(SKBitmap bitmap, string path, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality) ?? throw new IOException("JPEG encoding failed");
        var temp = path + ".tmp";
        using (var file = File.Create(temp)) data.SaveTo(file);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Copies 0xAARRGGBB pixels (one int each, rows top to bottom) into a bitmap.</summary>
    public static SKBitmap FromArgb(int[] pixels, int width, int height)
    {
        var bitmap = Create(width, height);
        // Little-endian 0xAARRGGBB is B, G, R, A in memory: the BGRA layout. Pixels must be opaque or premultiplied.
        var target = bitmap.GetPixels();
        for (int y = 0; y < height; y++)
            Marshal.Copy(pixels, y * width, target + y * bitmap.RowBytes, width);
        return bitmap;
    }
}
