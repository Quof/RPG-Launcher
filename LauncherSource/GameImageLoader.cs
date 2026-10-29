using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

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
            return await Task.Run(() =>
            {
                using var thumbnail = full ? CreateFullThumbnail(game) : CreateThumbnail(game);
                return thumbnail is null ? null : ImageKit.ToAvalonia(thumbnail);
            }, token).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static SKBitmap? CreateFullThumbnail(GameEntry game)
    {
        string? cachePath = CachePathFor(game, full: true);
        if (cachePath is not null && TryLoadCached(cachePath, full: true) is { } cached) return cached;

        try
        {
            using var source = LoadSourceImage(game);
            if (source is null) return null; // the card falls back to the regular thumbnail
            float fit = Math.Min(1f, Math.Min(FullThumbSize / (float)source.Width, FullThumbSize / (float)source.Height));
            int w = Math.Max(1, (int)Math.Round(source.Width * fit)), h = Math.Max(1, (int)Math.Round(source.Height * fit));
            var output = ImageKit.Create(w, h, Palette.Default.Card);
            using (var canvas = new SKCanvas(output))
                ImageKit.Draw(canvas, source, SKRect.Create(source.Width, source.Height), SKRect.Create(w, h));
            if (cachePath is not null) SaveCached(output, cachePath);
            return output;
        }
        catch
        {
            return null;
        }
    }

    private static SKBitmap? CreateThumbnail(GameEntry game)
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

    private static string PlaceholderInitial(GameEntry game) =>
        InitialOf(string.IsNullOrWhiteSpace(game.CustomTitle) ? game.LocalName : game.CustomTitle!);

    /// <summary>A name's first character, upper-cased: a whole one, even outside the BMP (emoji, rare kanji).</summary>
    public static string InitialOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        name = name.TrimStart();
        return name[..System.Globalization.StringInfo.GetNextTextElementLength(name)].ToUpperInvariant();
    }

    private static SKBitmap? TryLoadCached(string path, bool full = false)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = ImageKit.Decode(File.ReadAllBytes(path));
            if (image is null) return null;
            if (!full && (image.Width != ThumbWidth || image.Height != ThumbHeight))
            {
                image.Dispose();
                return null;
            }
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCached(SKBitmap thumbnail, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ImageKit.SaveJpeg(thumbnail, path, 92);
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

    private static SKBitmap? BuildThumbnail(GameEntry game)
    {
        try
        {
            using var source = LoadSourceImage(game);
            if (source is not null) return RenderFramed(source, game.Framing ?? DefaultFraming(game));

            using var icon = HasOwnIcon(game) ? LoadImage(game.IconPath!, null) : null;
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
    public static SKBitmap? LoadSourceImage(GameEntry game)
    {
        if (game.CustomImagePath is not null && LoadImage(game.CustomImagePath, null) is { } custom)
            return custom;

        if (game.UsesStoreImage && LoadImage(game.StoreImagePath!, null) is { } store)
            return store;

        if (game.Title1Path is null || game.HidesStockTitle) return null;
        var background = LoadImage(game.Title1Path, game.EncryptionKey);
        // Some games leave a blank title picture and draw their title another way.
        if (background is null || IsBlank(background))
        {
            background?.Dispose();
            return null;
        }
        using var foreground = game.Title2Path is null ? null : LoadImage(game.Title2Path, game.EncryptionKey);
        if (foreground is not null)
        {
            using var canvas = new SKCanvas(background);
            canvas.DrawBitmap(foreground, 0, 0);
        }
        return TrimLetterbox(background);
    }

    /// <summary>The card picture as an Avalonia bitmap, for dialogs; null when there is none.</summary>
    public static Bitmap? LoadSourcePicture(GameEntry game)
    {
        using var source = LoadSourceImage(game);
        return source is null ? null : ImageKit.ToAvalonia(source);
    }

    /// <summary>A picture file as an Avalonia bitmap; null when it can't be read.</summary>
    public static Bitmap? LoadPicture(string path)
    {
        using var image = LoadImage(path, null);
        return image is null ? null : ImageKit.ToAvalonia(image);
    }

    /// <summary>True when a picture is a single flat colour, such as an unused template background.</summary>
    internal static bool IsBlank(SKBitmap image)
    {
        var first = image.GetPixel(0, 0);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                var c = image.GetPixel(x * (image.Width - 1) / 7, y * (image.Height - 1) / 7);
                if (Math.Abs(c.Red - first.Red) > 10 || Math.Abs(c.Green - first.Green) > 10 ||
                    Math.Abs(c.Blue - first.Blue) > 10 || Math.Abs(c.Alpha - first.Alpha) > 10) return false;
            }
        }
        return true;
    }

    /// <summary>Trims black letterbox bars, at most a fifth per side, off a title screen.</summary>
    private static SKBitmap TrimLetterbox(SKBitmap image)
    {
        int w = image.Width, h = image.Height;
        if (w < 32 || h < 32) return image;

        bool Dark(SKColor c) => c.Alpha > 200 && c.Red < 28 && c.Green < 28 && c.Blue < 28;
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

        var trimmed = ImageKit.Crop(image, new SKRectI(left, top, right, bottom));
        image.Dispose();
        return trimmed;
    }

    private static SKBitmap RenderFramed(SKBitmap source, ImageFraming? framing)
    {
        // Cached thumbnails must not depend on the style that happened to be active.
        var output = ImageKit.Create(ThumbWidth, ThumbHeight, Palette.Default.Card);
        using var canvas = new SKCanvas(output);
        var dest = SKRect.Create(ThumbWidth, ThumbHeight);

        if (framing is null || framing.IsDefault)
        {
            ImageKit.DrawCover(canvas, source, dest);
            return output;
        }

        var crop = ImageFraming.CropRect(new Size(source.Width, source.Height), framing);
        ImageKit.Draw(canvas, source, SKRect.Create((float)crop.X, (float)crop.Y, (float)crop.Width, (float)crop.Height), dest);
        return output;
    }

    private static SKBitmap ComposeFallback(SKBitmap? icon, GameEntry game)
    {
        var output = ImageKit.Create(ThumbWidth, ThumbHeight);
        using var canvas = new SKCanvas(output);
        var rect = SKRect.Create(ThumbWidth, ThumbHeight);

        // A diagonal violet-to-navy backdrop (about 42°, corner to corner).
        using (var backdrop = new SKPaint { IsAntialias = true })
        {
            backdrop.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 14), new SKPoint(ThumbWidth, ThumbHeight - 14),
                new[] { new SKColor(55, 45, 84), new SKColor(20, 20, 29) }, SKShaderTileMode.Clamp);
            canvas.DrawRect(rect, backdrop);
        }

        using (var glow = new SKPaint { IsAntialias = true, Color = new SKColor(240, 197, 94, 20) })
        {
            canvas.DrawOval(SKRect.Create(-80, 40, 380, 380), glow);
            glow.Color = new SKColor(240, 197, 94, 18);
            canvas.DrawOval(SKRect.Create(10, 110, 230, 230), glow);
        }

        if (icon is not null)
        {
            float scale = Math.Min(150f / icon.Width, 150f / icon.Height);
            float w = icon.Width * scale, h = icon.Height * scale;
            ImageKit.Draw(canvas, icon, SKRect.Create(icon.Width, icon.Height),
                SKRect.Create((ThumbWidth - w) / 2f, (ThumbHeight - h) / 2f, w, h));
        }
        else
        {
            DrawInitial(canvas, PlaceholderInitial(game), rect);
        }

        return output;
    }

    /// <summary>Draws one big bold letter centered in <paramref name="rect"/>, in translucent gold.</summary>
    private static void DrawInitial(SKCanvas canvas, string initial, SKRect rect)
    {
        var style = SKFontStyle.Bold;
        var typeface = SKTypeface.FromFamilyName(Look.FontFamilyName, style) ?? SKTypeface.Default;
        // Japanese or other letters the font lacks come from a font that has them.
        int codepoint = char.ConvertToUtf32(initial, 0);
        if (typeface.GetGlyph(codepoint) == 0 &&
            SKFontManager.Default.MatchCharacter(Look.FontFamilyName, style, null, codepoint) is { } fallback)
            typeface = fallback;

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(240, 197, 94, 120),
            Typeface = typeface,
            TextSize = 64f * 96f / 72f
        };
        float width = paint.MeasureText(initial);
        var metrics = paint.FontMetrics;
        float x = rect.MidX - width / 2f;
        float baseline = rect.MidY - (metrics.Ascent + metrics.Descent) / 2f;
        canvas.DrawText(initial, x, baseline, paint);
    }

    public static SKBitmap? LoadImage(string path, string? encryptionKey)
    {
        try
        {
            var bytes = path.Contains(RgssArchive.Separator) ? ReadPacked(path) : File.ReadAllBytes(path);
            if (bytes is null) return null;
            if (Rm2kFiles.IsXyz(bytes)) return Rm2kFiles.DecodeXyz(bytes);
            bytes = DecryptIfNeeded(bytes, encryptionKey);
            return bytes is null ? null : ImageKit.Decode(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A file inside a package: "…\Game.rgss3a::Graphics\Titles1\Title.png" (RPG Maker), "…\archive.rpa::gui/main_menu.png"
    /// (Ren'Py), "…\Game.exe::data/bgimage/title.jpg" (an HTML game's package) or "…\Game.exe::#icon" (the exe's icon).
    /// </summary>
    private static byte[]? ReadPacked(string path)
    {
        int split = path.IndexOf(RgssArchive.Separator, StringComparison.Ordinal);
        string file = path[..split], name = path[(split + RgssArchive.Separator.Length)..];
        if (name == ExeIcon.Name) return ExeIcon.Read(file);
        return Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".rgssad" or ".rgss2a" or ".rgss3a" => RgssArchive.ReadPath(path),
            ".rpa" => RpaArchive.Open(file)?.Read(name),
            _ => AppArchive.Open(file)?.Read(name)
        };
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
}
