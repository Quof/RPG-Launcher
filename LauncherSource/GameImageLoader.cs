using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;

namespace RpgLauncher;

internal static class GameImageLoader
{
    private const int ThumbWidth = 512;
    private const int ThumbHeight = 288;

    // Bump when the thumbnail look changes so old cached images are rebuilt.
    private const int ThumbVersion = 3;

    private static readonly SemaphoreSlim Gate = new(3, 3);

    /// <summary>Thumbnail cache folder; null disables caching.</summary>
    public static string? CacheDirectory { get; set; }

    /// <summary>Largest side of a full (uncropped) thumbnail for square cards.</summary>
    private const int FullThumbSize = 420;

    /// <summary>Default framing: DLsite images (4:3, title logo usually at the top) are cropped from the top.</summary>
    public static ImageFraming? DefaultFraming(GameEntry game) =>
        game.UsesStoreImage ? new ImageFraming { CenterY = 0 } : null;

    /// <param name="full">The whole picture, uncropped (square cards), instead of the 16:9 crop.</param>
    public static async Task<Bitmap?> CreateThumbnailAsync(GameEntry game, CancellationToken token, bool full = false)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            return await Task.Run(() => full ? CreateFullThumbnail(game) : CreateThumbnail(game), token).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Bitmap? CreateFullThumbnail(GameEntry game)
    {
        string? cachePath = CachePathFor(game, full: true);
        if (cachePath is not null && TryLoadCached(cachePath, full: true) is { } cached) return cached;

        try
        {
            using var source = LoadSourceImage(game);
            if (source is null) return null; // the card falls back to the regular thumbnail
            float fit = Math.Min(1f, Math.Min(FullThumbSize / (float)source.Width, FullThumbSize / (float)source.Height));
            int w = Math.Max(1, (int)Math.Round(source.Width * fit)), h = Math.Max(1, (int)Math.Round(source.Height * fit));
            var output = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(output))
            {
                g.Clear(Palette.Default.Card);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var attributes = new ImageAttributes();
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(source, new Rectangle(0, 0, w, h), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }
            if (cachePath is not null) SaveCached(output, cachePath);
            return output;
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? CreateThumbnail(GameEntry game)
    {
        string? cachePath = CachePathFor(game);
        if (cachePath is not null && TryLoadCached(cachePath) is { } cached) return cached;

        var thumbnail = BuildThumbnail(game);
        if (thumbnail is not null && cachePath is not null) SaveCached(thumbnail, cachePath);
        return thumbnail;
    }

    /// <summary>Cache file for a game, named by a hash of its image paths and timestamps so changes invalidate it.</summary>
    public static string? CachePathFor(GameEntry game, bool full = false)
    {
        if (CacheDirectory is null) return null;

        static string Stamp(string? path)
        {
            if (path is null) return "-";
            try
            {
                // a picture inside an archive changes with the archive
                var info = new FileInfo(RgssArchive.FileOf(path));
                return info.Exists ? $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : path + "|missing";
            }
            catch
            {
                return path + "|error";
            }
        }

        var parts = new List<string>
        {
            ThumbVersion.ToString(), game.Key, game.DisplayTitle,
            Stamp(game.Title1Path), Stamp(game.Title2Path), Stamp(game.IconPath), game.EncryptionKey ?? "-"
        };
        // Extra parts are added only when they apply, so existing cache keys stay valid.
        if (game.CustomImagePath is not null) parts.Add("custom:" + Stamp(game.CustomImagePath));
        if (game.UsesStoreImage) parts.Add("store:" + Stamp(game.StoreImagePath));
        if (game.HidesStockTitle) parts.Add("stock-hidden");
        if (game.Framing is { IsDefault: false } framing) parts.Add("frame:" + framing.Signature());
        // Games drawn as a letter on a backdrop are redrawn when that letter changes.
        if (game.CustomImagePath is null && !game.UsesStoreImage &&
            (game.Title1Path is null || game.HidesStockTitle) && !HasOwnIcon(game))
            parts.Add("initial:" + PlaceholderInitial(game));

        if (full) parts.Add("full");
        string identity = string.Join("\n", parts);
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(identity.ToLowerInvariant()));
        return Path.Combine(CacheDirectory, Convert.ToHexString(hash) + ".jpg");
    }

    /// <summary>True when the game has an icon of its own rather than RPG Maker's default one.</summary>
    private static bool HasOwnIcon(GameEntry game) => game.IconPath is not null && !StockTitles.IsStockIcon(game.IconPath);

    private static string PlaceholderInitial(GameEntry game)
    {
        string name = string.IsNullOrWhiteSpace(game.CustomTitle) ? game.LocalName : game.CustomTitle!;
        return string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();
    }

    private static Bitmap? TryLoadCached(string path, bool full = false)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            if (!full && (image.Width != ThumbWidth || image.Height != ThumbHeight)) return null;
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCached(Bitmap thumbnail, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
            var temp = path + ".tmp";
            thumbnail.Save(temp, encoder, parameters);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Thumbnail not cached: " + ex.Message);
        }
    }

    /// <summary>Deletes cached thumbnails that no longer belong to any game in the library.</summary>
    public static void PruneCache(IReadOnlyList<GameEntry> games)
    {
        if (CacheDirectory is null || !Directory.Exists(CacheDirectory)) return;
        try
        {
            var keep = games.SelectMany(g => new[] { CachePathFor(g), CachePathFor(g, full: true) }).Where(p => p is not null)
                .Select(p => Path.GetFileName(p!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(CacheDirectory, "*.jpg"))
            {
                if (!keep.Contains(Path.GetFileName(file))) File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Thumbnail cache prune failed: " + ex.Message);
        }
    }

    private static Bitmap? BuildThumbnail(GameEntry game)
    {
        try
        {
            using var source = LoadSourceImage(game);
            if (source is not null) return RenderFramed(source, game.Framing ?? DefaultFraming(game));

            using var icon = HasOwnIcon(game) ? LoadIcon(game.IconPath!) : null;
            return ComposeFallback(icon, game);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The full-size card picture: the custom image, else the DLsite image (<see cref="GameEntry.WantsStoreImage"/>),
    /// else the title screen (title2 over title1). Null when there is none or only a stock background.
    /// </summary>
    public static Bitmap? LoadSourceImage(GameEntry game)
    {
        if (game.CustomImagePath is not null && LoadImage(game.CustomImagePath, null) is { } custom)
            return custom;

        if (game.UsesStoreImage && LoadImage(game.StoreImagePath!, null) is { } store)
            return store;

        if (game.Title1Path is null || game.HidesStockTitle) return null;
        using var background = LoadImage(game.Title1Path, game.EncryptionKey);
        // Some games leave a blank title picture and draw their title another way.
        if (background is null || IsBlank(background)) return null;
        using var foreground = game.Title2Path is null ? null : LoadImage(game.Title2Path, game.EncryptionKey);

        var canvas = new Bitmap(background.Width, background.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.CompositingMode = CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImageUnscaled(background, 0, 0);
            if (foreground is not null) g.DrawImageUnscaled(foreground, 0, 0);
        }
        return TrimLetterbox(canvas);
    }

    /// <summary>True when a picture is a single flat colour.</summary>
    private static bool IsBlank(Bitmap image)
    {
        var first = image.GetPixel(0, 0);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                var c = image.GetPixel(x * (image.Width - 1) / 7, y * (image.Height - 1) / 7);
                if (Math.Abs(c.R - first.R) > 10 || Math.Abs(c.G - first.G) > 10 || Math.Abs(c.B - first.B) > 10 ||
                    Math.Abs(c.A - first.A) > 10) return false;
            }
        }
        return true;
    }

    /// <summary>Trims black letterbox bars, at most a fifth per side, off a title screen.</summary>
    private static Bitmap TrimLetterbox(Bitmap image)
    {
        int w = image.Width, h = image.Height;
        if (w < 32 || h < 32) return image;

        bool Dark(Color c) => c.A > 200 && c.R < 28 && c.G < 28 && c.B < 28;
        bool RowDark(int y)
        {
            for (int x = 0; x < w; x += 3)
                if (!Dark(image.GetPixel(x, y))) return false;
            return true;
        }
        bool ColumnDark(int x, int top, int bottom)
        {
            for (int y = top; y < bottom; y += 3)
                if (!Dark(image.GetPixel(x, y))) return false;
            return true;
        }

        int top = 0, bottom = h, left = 0, right = w;
        while (top < h / 5 && RowDark(top)) top++;
        while (h - bottom < h / 5 && bottom - 1 > top && RowDark(bottom - 1)) bottom--;
        while (left < w / 5 && ColumnDark(left, top, bottom)) left++;
        while (w - right < w / 5 && right - 1 > left && ColumnDark(right - 1, top, bottom)) right--;
        // a bar only counts if it's more than a hairline
        if (top < 3) top = 0;
        if (h - bottom < 3) bottom = h;
        if (left < 3) left = 0;
        if (w - right < 3) right = w;
        if (top == 0 && bottom == h && left == 0 && right == w) return image;

        var trimmed = image.Clone(new Rectangle(left, top, right - left, bottom - top), image.PixelFormat);
        image.Dispose();
        return trimmed;
    }

    private static Bitmap RenderFramed(Bitmap source, ImageFraming? framing)
    {
        var output = new Bitmap(ThumbWidth, ThumbHeight, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(output);
        // Cached thumbnails must not depend on the style that happened to be active.
        g.Clear(Palette.Default.Card);
        var dest = new RectangleF(0, 0, ThumbWidth, ThumbHeight);

        if (framing is null || framing.IsDefault)
        {
            Theme.DrawCover(g, source, dest);
            return output;
        }

        var crop = ImageFraming.CropRect(source.Size, framing);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Clamp sampling at the crop edge so zoomed crops don't pick up a dark fringe.
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(source, Rectangle.Round(dest), crop.X, crop.Y, crop.Width, crop.Height,
            GraphicsUnit.Pixel, attributes);
        return output;
    }

    private static Bitmap ComposeFallback(Bitmap? icon, GameEntry game)
    {
        var output = new Bitmap(ThumbWidth, ThumbHeight, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(output);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, ThumbWidth, ThumbHeight);
        using (var gradient = new LinearGradientBrush(rect,
                   Color.FromArgb(55, 45, 84), Color.FromArgb(20, 20, 29), 42f))
        {
            g.FillRectangle(gradient, rect);
        }

        using (var glowPath = new GraphicsPath())
        {
            glowPath.AddEllipse(-80, 40, 380, 380);
            using var glow = new SolidBrush(Color.FromArgb(20, 240, 197, 94));
            g.FillPath(glow, glowPath);
            using var inner = new SolidBrush(Color.FromArgb(18, 240, 197, 94));
            g.FillEllipse(inner, 10, 110, 230, 230);
        }

        if (icon is not null)
        {
            float scale = Math.Min(150f / icon.Width, 150f / icon.Height);
            float w = icon.Width * scale;
            float h = icon.Height * scale;
            g.DrawImage(icon, new RectangleF((ThumbWidth - w) / 2f, (ThumbHeight - h) / 2f, w, h));
        }
        else
        {
            var initial = PlaceholderInitial(game);
            using var font = Theme.Font(64f, FontStyle.Bold);
            Theme.DrawText(g, initial, font, Color.FromArgb(120, 240, 197, 94),
                new RectangleF(0, 0, ThumbWidth, ThumbHeight));
        }

        return output;
    }

    public static Bitmap? LoadImage(string path, string? encryptionKey)
    {
        try
        {
            // "…\Game.rgss3a::Graphics\Titles1\Title.png" is a picture inside an RPG Maker archive.
            var bytes = path.Contains(RgssArchive.Separator) ? RgssArchive.ReadPath(path) : File.ReadAllBytes(path);
            if (bytes is null) return null;
            if (Rm2kFiles.IsXyz(bytes)) return Rm2kFiles.DecodeXyz(bytes);
            bytes = DecryptIfNeeded(bytes, encryptionKey);
            if (bytes is null) return null;

            using var stream = new MemoryStream(bytes, writable: false);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? DecryptIfNeeded(byte[] bytes, string? encryptionKey)
    {
        if (bytes.Length < 17) return bytes;
        bool encrypted = bytes[0] == (byte)'R' && bytes[1] == (byte)'P' && bytes[2] == (byte)'G' &&
                         bytes[3] == (byte)'M' && bytes[4] == (byte)'V';
        if (!encrypted) return bytes;

        if (string.IsNullOrWhiteSpace(encryptionKey) || encryptionKey.Length < 32) return null;

        var key = new byte[16];
        try
        {
            for (int i = 0; i < 16; i++)
                key[i] = Convert.ToByte(encryptionKey.Substring(i * 2, 2), 16);
        }
        catch
        {
            return null;
        }

        var body = new byte[bytes.Length - 16];
        Buffer.BlockCopy(bytes, 16, body, 0, body.Length);
        for (int i = 0; i < Math.Min(16, body.Length); i++)
            body[i] ^= key[i];
        return body;
    }

    private static Bitmap? LoadIcon(string path)
    {
        try
        {
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using var icon = new Icon(path, 256, 256);
                return icon.ToBitmap();
            }
            return LoadImage(path, null);
        }
        catch
        {
            return null;
        }
    }
}
