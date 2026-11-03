using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace RpgLauncher;

/// <summary>A game on the download server, as manifest.json lists it.</summary>
internal sealed class VipServerGame
{
    /// <summary>Path under the server address, such as games/matsuri/2005-kouhaku/01.zip.</summary>
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string? Sha256 { get; set; }

    /// <summary>matsuri or thread.</summary>
    public string? Source { get; set; }
    public string? Matsuri { get; set; }
    public string? No { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Thread { get; set; }
    public int? Year { get; set; }
    public string? Engine { get; set; }
    public string? Note { get; set; }

    /// <summary>A full download address (the uploader archive on archive.org) instead of a path on the server.</summary>
    public string? Url { get; set; }
    public string? Md5 { get; set; }

    /// <summary>A work's original link (itch.io, Google Drive…), resolved to a file when downloaded.</summary>
    public bool Original => Source == "original";

    /// <summary>The uploader archive's area the file came from, for listing games the catalog doesn't know.</summary>
    public string? Area { get; set; }

    /// <summary>The catalog work it is ("2005-kouhaku/01"), or null for games the catalog doesn't list.</summary>
    public string? WorkKey => string.IsNullOrWhiteSpace(Matsuri) || string.IsNullOrWhiteSpace(No) ? null : VipServer.Key(Matsuri, No);

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title! : System.IO.Path.GetFileNameWithoutExtension(Path);
}

internal sealed class VipManifest
{
    public int Version { get; set; }
    public string? Generated { get; set; }
    public List<VipServerGame> Games { get; set; } = new();
}

/// <summary>
/// The VIPRPG download server: a static file host (a Cloudflare R2 bucket) with manifest.json listing its zips.
/// The list is cached beside the launcher so it still shows offline.
/// </summary>
internal static class VipServer
{
    private const string UserAgent = "RPGLauncher/1.0 (+local desktop launcher)";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // No overall timeout: big games take minutes. Requests that stall still fail through the cancellation token.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    public static string Key(string matsuri, string no) => matsuri.Trim() + "/" + no.Trim();

    /// <summary>A usable server address (http or https), without a trailing slash; null if it isn't one.</summary>
    public static string? Normalize(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        string text = address.Trim();
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>The download address of a game; null for paths that try to leave the server's folder.</summary>
    public static string? Url(string server, string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or "..")) return null;
        return server + "/" + string.Join('/', parts.Select(Uri.EscapeDataString));
    }

    /// <summary>The cached list, or null if there is none.</summary>
    public static VipManifest? LoadCached(string cachePath)
    {
        try
        {
            return File.Exists(cachePath) ? JsonSerializer.Deserialize<VipManifest>(File.ReadAllText(cachePath), Options) : null;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("VIPRPG server list cache not read: " + ex.Message);
            return null;
        }
    }

    /// <summary>Fetches manifest.json from the server and caches it.</summary>
    public static async Task<VipManifest> FetchAsync(string server, string cachePath, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, server + "/manifest.json");
        // The server caches the list for a few minutes; ask for the current one.
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await Http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(timeout.Token);
        var manifest = JsonSerializer.Deserialize<VipManifest>(json, Options) ?? throw new InvalidDataException("manifest.json is empty.");
        manifest.Games.RemoveAll(game => string.IsNullOrWhiteSpace(game.Path));
        try
        {
            File.WriteAllText(cachePath, json);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("VIPRPG server list not cached: " + ex.Message);
        }
        return manifest;
    }

    /// <summary>
    /// Downloads a game to target through a .part file, checking its size and SHA-256 against the list. progress
    /// gets the bytes received so far.
    /// </summary>
    public static async Task DownloadAsync(string server, VipServerGame game, string target, Action<long> progress,
        CancellationToken cancel, string? resolved = null)
    {
        // An original link comes already resolved by VipOriginal; anything else only from the archive or the server.
        string url = game.Original
            ? resolved ?? throw new InvalidDataException("The original link wasn't resolved: " + game.Url)
            : game.Url is { } direct
            ? VipUploader.IsArchiveUrl(direct) ? direct : throw new InvalidDataException("Downloads only come from archive.org: " + direct)
            : Url(server, game.Path) ?? throw new InvalidDataException("The server lists a game at an unusable path: " + game.Path);
        string part = target + ".part";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        try
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                // A sign-in, quota or "file removed" page instead of the file.
                if (game.Original && response.Content.Headers.ContentType?.MediaType == "text/html")
                    throw new InvalidDataException("The site gave a web page instead of the file: it may be gone, private, or over its download limit.");
                await using var input = await response.Content.ReadAsStreamAsync(cancel);
                await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                var buffer = new byte[1 << 16];
                long received = 0;
                while (true)
                {
                    // A connection that goes quiet for a minute is treated as dropped.
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    stall.CancelAfter(TimeSpan.FromSeconds(60));
                    int read = await input.ReadAsync(buffer, stall.Token);
                    if (read == 0) break;
                    hash.AppendData(buffer, 0, read);
                    md5.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    received += read;
                    progress(received);
                }
                if (game.Size > 0 && received != game.Size)
                    throw new InvalidDataException($"The download is {received:N0} bytes; the server list says {game.Size:N0}.");
                string actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.IsNullOrWhiteSpace(game.Sha256) && !actual.Equals(game.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download doesn't match the server list's SHA-256: it was damaged on the way.");
                if (!string.IsNullOrWhiteSpace(game.Md5) &&
                    !Convert.ToHexString(md5.GetHashAndReset()).Equals(game.Md5.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download doesn't match the archive's MD5: it was damaged on the way.");
            }
            File.Move(part, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { /* left for the next try to overwrite */ }
            throw;
        }
    }

    /// <summary>Explains a failed list fetch or download in a line, in Japanese and English.</summary>
    public static (string Japanese, string English) Explain(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            ("サーバーにファイルがないみたい。", "The server doesn't have that file (404). The list may be newer than the files, or the address is wrong."),
        HttpRequestException { StatusCode: { } code } =>
            ("サーバーがエラーを返した…", $"The server answered {(int)code} {code}. It may be busy; try again later."),
        HttpRequestException =>
            ("サーバーにつながらない…", "Couldn't reach the download server: check the internet connection."),
        OperationCanceledException =>
            ("サーバーが応答しない…", "The server stopped answering. Try again later."),
        InvalidDataException or JsonException =>
            ("データがおかしい…", ex.Message),
        IOException or UnauthorizedAccessException =>
            ("保存できなかった…", "Couldn't save the file: " + ex.Message),
        _ => ("うまくいかなかった…", ex.Message)
    };
}
