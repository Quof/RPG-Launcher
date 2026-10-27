using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RpgLauncher;

/// <summary>Release covers, downloaded once and decoded at card size; only recently shown ones stay in memory.</summary>
internal sealed class ReleaseCovers : IDisposable
{
    private const int MemoryLimit = 180;

    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(3, 3);
    private readonly Dictionary<string, Bitmap> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recent = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private Size _size;
    private CancellationTokenSource _cts = new();

    /// <summary>Raised on the UI thread when a cover finishes loading.</summary>
    public event Action<string>? Loaded;

    private readonly Control _owner;

    public ReleaseCovers(Control owner, string directory)
    {
        _owner = owner;
        _directory = directory;
    }

    /// <summary>Returns the cover at <paramref name="size"/>, or null while it loads.</summary>
    public Bitmap? Get(ReleaseItem item, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return null;
        if (size != _size)
        {
            // Card size changed: decoded covers are the wrong size, reload them from disk.
            Clear();
            _size = size;
        }

        if (_bitmaps.TryGetValue(item.ProductId, out var bitmap))
        {
            Touch(item.ProductId);
            return bitmap;
        }

        if (!_pending.Contains(item.ProductId) && !_failed.Contains(item.ProductId) && item.ImageBase is not null)
        {
            _pending.Add(item.ProductId);
            _ = LoadAsync(item, size, _cts.Token);
        }
        return null;
    }

    private async Task LoadAsync(ReleaseItem item, Size size, CancellationToken token)
    {
        Bitmap? bitmap = null;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                bitmap = await Task.Run(() => LoadOrDownloadAsync(item, size, token), token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Cover {item.ProductId} failed: {ex.Message}");
        }

        try
        {
            _owner.BeginInvoke(new Action(() =>
            {
                _pending.Remove(item.ProductId);
                if (token.IsCancellationRequested || size != _size)
                {
                    bitmap?.Dispose();
                    return;
                }
                if (bitmap is null)
                {
                    _failed.Add(item.ProductId);
                    return;
                }
                _bitmaps[item.ProductId] = bitmap;
                Touch(item.ProductId);
                Trim();
                Loaded?.Invoke(item.ProductId);
            }));
        }
        catch
        {
            bitmap?.Dispose();
        }
    }

    private async Task<Bitmap?> LoadOrDownloadAsync(ReleaseItem item, Size size, CancellationToken token)
    {
        // Small cards use DLsite's 300px resize; large or high-DPI cards use the full image.
        bool large = size.Width > 300;
        string path = Path.Combine(_directory, $"{item.ProductId}_{(large ? "main" : "300")}.jpg");

        if (!File.Exists(path))
        {
            string? url = item.CoverUrl(large);
            if (url is null) return null;
            var bytes = await MetadataService.Client.GetByteArrayAsync(url, token).ConfigureAwait(false);
            Directory.CreateDirectory(_directory);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }

        using var stream = new MemoryStream(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false), writable: false);
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);

        var output = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(output);
        g.Clear(Theme.Card);
        Theme.DrawCover(g, image, new RectangleF(0, 0, size.Width, size.Height));
        return output;
    }

    private void Touch(string id)
    {
        _recent.Remove(id);
        _recent.AddFirst(id);
    }

    private void Trim()
    {
        while (_recent.Count > MemoryLimit)
        {
            string id = _recent.Last!.Value;
            _recent.RemoveLast();
            if (_bitmaps.Remove(id, out var bitmap)) bitmap.Dispose();
        }
    }

    private void Clear()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        foreach (var bitmap in _bitmaps.Values) bitmap.Dispose();
        _bitmaps.Clear();
        _recent.Clear();
        _pending.Clear();
        _failed.Clear();
    }

    public void Dispose()
    {
        Clear();
        _cts.Dispose();
    }
}
