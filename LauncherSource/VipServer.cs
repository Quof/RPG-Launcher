using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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

    /// <summary>
    /// A link resolved to a file when downloaded: the work's original link (itch.io, Google Drive…), or "live", a file
    /// the archive didn't keep, from the uploader itself.
    /// </summary>
    public bool Original => Source is "original" or "live";

    /// <summary>The uploader archive's area the file came from, for listing games the catalog doesn't know.</summary>
    public string? Area { get; set; }

    /// <summary>The archive's section for that area ("Wiki", "主帖用上传区"…), to group the 保管庫 list.</summary>
    public string? Section { get; set; }

    /// <summary>The catalog work it is ("2005-kouhaku/01"), or null for games the catalog doesn't list.</summary>
    public string? WorkKey => VipServer.WorkKey(Matsuri, No, Title);

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title! : System.IO.Path.GetFileNameWithoutExtension(Path);
}

internal sealed class VipManifest
{
    public int Version { get; set; }
    public string? Generated { get; set; }
    public List<VipServerGame> Games { get; set; } = new();
}

/// <summary>
/// This connection's game downloads today as the download server last counted them: different files, against a
/// daily limit per IP address that resets at midnight UTC.
/// </summary>
internal sealed record VipQuota(int Used, int Limit, DateTimeOffset Reset)
{
    public int Left => Math.Max(0, Limit - Used);
}

/// <summary>The download server refused a request over one of its limits.</summary>
internal sealed class VipLimitException : Exception
{
    public VipLimitException(bool daily, TimeSpan retryAfter, int limit)
        : base(daily ? "The server's daily download limit is reached." : "The server paused this connection for too many requests.")
    {
        Daily = daily;
        RetryAfter = retryAfter;
        Limit = limit;
    }

    /// <summary>The day's download limit (else the short burst limit, which blocks for a few minutes).</summary>
    public bool Daily { get; }
    public TimeSpan RetryAfter { get; }
    public int Limit { get; }
    public DateTimeOffset Until => DateTimeOffset.Now + RetryAfter;
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
        // Development aid: RPGLAUNCHER_TEST_SOCKET_ERROR=<code> fails every connection with that Windows socket error
        // (10013 blocked by this PC, 11001 no such host…), to check how failures are explained.
        if (int.TryParse(Environment.GetEnvironmentVariable("RPGLAUNCHER_TEST_SOCKET_ERROR"), out int testError))
            handler.ConnectCallback = (_, _) => ValueTask.FromException<Stream>(new SocketException(testError));
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static VipQuota? _quota;
    private static long _blockedUntil;

    /// <summary>Today's downloads as the server last reported them; null before it has, or once its day is over.</summary>
    public static VipQuota? Quota => Volatile.Read(ref _quota) is { } quota && quota.Reset > DateTimeOffset.Now ? quota : null;

    /// <summary>When the server's pause for too many requests ends, if it paused this connection.</summary>
    public static DateTimeOffset? BlockedUntil =>
        Interlocked.Read(ref _blockedUntil) is var ticks and > 0 && new DateTimeOffset(ticks, TimeSpan.Zero) is var until
            && until > DateTimeOffset.UtcNow ? until.ToLocalTime() : null;

    /// <summary>Reads the server's usage headers (X-Downloads-Used, -Limit, -Reset) into Quota.</summary>
    private static void ReadUsage(HttpResponseMessage response)
    {
        static int? Header(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out int value) ? value : null;
        if (Header(response, "X-Downloads-Used") is { } used && Header(response, "X-Downloads-Limit") is { } limit and > 0 &&
            Header(response, "X-Downloads-Reset") is { } reset)
            Volatile.Write(ref _quota, new VipQuota(used, limit, DateTimeOffset.Now.AddSeconds(reset)));
    }

    /// <summary>Throws VipLimitException when the server answered 429 for one of its limits.</summary>
    private static async Task ThrowIfLimitedAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests) return;
        var retry = response.Headers.RetryAfter is { } after
            ? after.Delta ?? (after.Date is { } date ? date - DateTimeOffset.UtcNow : null)
            : null;
        bool daily = false;
        int limit = Quota?.Limit ?? 0;
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            var root = body.RootElement;
            // "limit" names the limit; a server from before it only says so in its message.
            daily = root.TryGetProperty("limit", out var kind) ? kind.GetString() == "daily"
                : root.TryGetProperty("error", out var error) && (error.GetString() ?? "").Contains("Daily", StringComparison.OrdinalIgnoreCase);
            if (root.TryGetProperty("max", out var max) && max.TryGetInt32(out int most)) limit = most;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // a 429 without the server's JSON (a proxy's) is taken as a short pause
        }
        var wait = retry is { } span && span > TimeSpan.Zero ? span : TimeSpan.FromMinutes(daily ? 60 : 10);
        if (!daily) Interlocked.Exchange(ref _blockedUntil, (DateTimeOffset.UtcNow + wait).UtcTicks);
        else if (Quota is { } quota) Volatile.Write(ref _quota, quota with { Used = Math.Max(quota.Used, quota.Limit) });
        throw new VipLimitException(daily, wait, limit);
    }

    /// <summary>
    /// Games kept in the bucket for preservation only and never listed or downloaded: the WOLF RPG Editor contest
    /// games (games/wolf/). The download Worker refuses them too.
    /// </summary>
    public static bool Withheld(VipServerGame game) =>
        game.Path.StartsWith("games/wolf/", StringComparison.OrdinalIgnoreCase) || VipWolf.IsContestArea(game.Area);

    public static string Key(string matsuri, string no) => matsuri.Trim() + "/" + no.Trim();

    /// <summary>
    /// A catalog work's key: its matsuri and entry number, or for a work with no number (2024蔵出し祭り's extras…)
    /// its matsuri and "#" and title; null without a matsuri.
    /// </summary>
    public static string? WorkKey(string? matsuri, string? no, string? title)
    {
        if (string.IsNullOrWhiteSpace(matsuri)) return null;
        if (!string.IsNullOrWhiteSpace(no)) return Key(matsuri, no);
        return string.IsNullOrWhiteSpace(title) ? null : Key(matsuri, "#" + title.Trim());
    }

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

    /// <summary>
    /// The list built into the launcher (the bucket's manifest.json when the launcher was built, gzipped), so the
    /// 保管庫 lists and downloads show even before the server has ever answered; null if this build has none.
    /// </summary>
    public static VipManifest? LoadBuiltIn()
    {
        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("RpgLauncher.Assets.vip-server.json.gz");
            if (stream is null) return null;
            using var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
            var manifest = JsonSerializer.Deserialize<VipManifest>(gzip, Options);
            manifest?.Games.RemoveAll(game => string.IsNullOrWhiteSpace(game.Path));
            return manifest;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Built-in VIPRPG server list not read: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Whether a list should replace the current one: it lists games and is at least as new (by its generated time).
    /// A list without a time only replaces nothing at all.
    /// </summary>
    public static bool Replaces(VipManifest candidate, VipManifest? current)
    {
        if (candidate.Games.Count == 0) return false;
        if (current is null || current.Games.Count == 0) return true;
        return Generated(candidate) >= Generated(current);
    }

    private static DateTimeOffset Generated(VipManifest manifest) =>
        DateTimeOffset.TryParse(manifest.Generated, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var time) ? time : DateTimeOffset.MinValue;

    /// <summary>Fetches manifest.json from the server and caches it.</summary>
    public static async Task<VipManifest> FetchAsync(string server, string cachePath, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, server + "/manifest.json");
        // The server caches the list for a few minutes; ask for the current one.
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await Http.SendAsync(request, timeout.Token);
        ReadUsage(response);
        await ThrowIfLimitedAsync(response, timeout.Token);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(timeout.Token);
        var manifest = JsonSerializer.Deserialize<VipManifest>(json, Options) ?? throw new InvalidDataException("manifest.json is empty.");
        manifest.Games.RemoveAll(game => string.IsNullOrWhiteSpace(game.Path));
        // A list with nothing in it (a half-finished upload, a placeholder) never replaces a real one, here or in the cache.
        if (manifest.Games.Count == 0) throw new InvalidDataException("manifest.json lists no games.");
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
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                var buffer = new byte[1 << 16];
                long received = 0;
                // A connection that drops or goes quiet is picked up where it stopped (a range request), a few times.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        if (received > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(received, null);
                        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
                        // Only the launcher's own server counts downloads; archive.org and original sites have their own rules.
                        if (!game.Original && game.Url is null)
                        {
                            ReadUsage(response);
                            await ThrowIfLimitedAsync(response, cancel);
                        }
                        response.EnsureSuccessStatusCode();
                        // A sign-in, quota or "file removed" page instead of the file.
                        if (game.Original && response.Content.Headers.ContentType?.MediaType == "text/html")
                            throw new InvalidDataException("The site gave a web page instead of the file: it may be gone, private, or over its download limit.");
                        if (received > 0 && (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != received))
                        {
                            // The site sent the whole file again: start over.
                            Diagnostics.Log($"Download restarted from the beginning (no resume): {url}");
                            received = 0;
                            output.SetLength(0);
                            output.Position = 0;
                            hash.GetHashAndReset();
                            md5.GetHashAndReset();
                        }
                        await using var input = await response.Content.ReadAsStreamAsync(cancel);
                        while (true)
                        {
                            // A connection that goes quiet for half a minute is treated as dropped.
                            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                            stall.CancelAfter(TimeSpan.FromSeconds(30));
                            int read;
                            try
                            {
                                read = await input.ReadAsync(buffer, stall.Token);
                            }
                            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                            {
                                throw new IOException("The connection went quiet for 30 seconds.");
                            }
                            if (read == 0) break;
                            hash.AppendData(buffer, 0, read);
                            md5.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                            received += read;
                            progress(received);
                        }
                        if (game.Size > 0 && received < game.Size)
                            throw new IOException($"The connection closed at {received:N0} of {game.Size:N0} bytes.");
                        break;
                    }
                    // A connection this PC itself refuses (a VPN or firewall) fails the same way every time: no retries.
                    catch (Exception ex) when (attempt < 3 && !cancel.IsCancellationRequested && ex is IOException or HttpRequestException &&
                                               !BlockedByThisPc(ex))
                    {
                        Diagnostics.Log($"Download interrupted at {received:N0} bytes, resuming (try {attempt + 2}): {url}: {ex.Message}");
                        await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancel);
                    }
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
    /// <summary>The socket error under a failed request (10013 blocked, 11001 no such host…), if there is one.</summary>
    private static SocketError? SocketCode(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is SocketException socket) return socket.SocketErrorCode;
        return null;
    }

    /// <summary>True when this PC itself refused the connection (Windows error 10013: a VPN's kill switch or
    /// split tunnel, a firewall or security software), so no server was ever reached.</summary>
    public static bool BlockedByThisPc(Exception ex) => ex is HttpRequestException && SocketCode(ex) is SocketError.AccessDenied;

    /// <summary>The longer explanation for <see cref="BlockedByThisPc"/>, for a dialog.</summary>
    public static (string Japanese, string English) BlockedDetails =>
        ("このPCがランチャーの通信をブロックしたので、サーバーまで届いていません（Windowsのエラー10013）。\n\n" +
         "よくある原因はVPNです。VPNが切れている間のキルスイッチや、RPG Launcher.exeを「VPNのみ」にするスプリットトンネルの設定で起こります。\n\n" +
         "VPNにつなぐか、スプリットトンネルでRPG Launcher.exeをVPNの対象外（バイパス）にしてね。ファイアウォールやセキュリティソフトがこのアプリを止めている場合も同じになります。",
         "This PC blocked the launcher's connection, so the server was never reached (Windows error 10013).\n\n" +
         "A VPN usually does this: its kill switch while it's disconnected, or a split-tunnel rule that lets RPG Launcher.exe use the VPN only.\n\n" +
         "Connect the VPN, or set RPG Launcher.exe to bypass it in the VPN's split tunneling. A firewall or security software blocking the app does the same.");

    public static (string Japanese, string English) Explain(Exception ex) => ex switch
    {
        VipLimitException { Daily: true } limit =>
            ($"今日のダウンロード上限{(limit.Limit > 0 ? $"（{limit.Limit:N0}本）" : "")}に達したよ。{limit.Until:HH:mm}にリセットされるよ。",
             $"You've reached the server's daily download limit{(limit.Limit > 0 ? $" ({limit.Limit:N0} different games)" : "")}. It resets at {limit.Until:HH:mm} " +
             $"(midnight UTC), in {Look.FormatDuration((long)limit.RetryAfter.TotalSeconds)}. Games already downloaded today can still be downloaded again."),
        VipLimitException limit =>
            ($"短い時間にリクエストが多すぎたので、サーバーが{Look.FormatDuration((long)limit.RetryAfter.TotalSeconds)}止めているよ。",
             $"Too many requests in a few seconds: the server paused this connection for {Look.FormatDuration((long)limit.RetryAfter.TotalSeconds)} (until {limit.Until:HH:mm})."),
        HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable } =>
            ("サーバーが混んでいるみたい。しばらくしてからもう一度。", "The download server is unavailable right now (503), perhaps over its free daily allowance. Try again later."),
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            ("サーバーにファイルがないみたい。", "The server doesn't have that file (404). The list may be newer than the files, or the address is wrong."),
        HttpRequestException { StatusCode: { } code } =>
            ("サーバーがエラーを返した…", $"The server answered {(int)code} {code}. It may be busy; try again later."),
        HttpRequestException when SocketCode(ex) is SocketError.AccessDenied =>
            ("このPCが通信をブロック中…VPNの設定を確認してね", "This PC blocked the connection: check your VPN or firewall"),
        HttpRequestException when SocketCode(ex) is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData =>
            ("サーバーの住所が分からない…ネット接続を確認してね", "Couldn't look up the server: check the connection or VPN"),
        HttpRequestException when SocketCode(ex) is SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.HostUnreachable =>
            ("ネットワークにつながっていないみたい", "No network connection: check Wi-Fi, the cable or the VPN"),
        HttpRequestException when SocketCode(ex) is SocketError.TimedOut or SocketError.ConnectionRefused or SocketError.ConnectionReset =>
            ("接続が切られた…少ししてからもう一度", "The connection was cut off: try again in a little while"),
        HttpRequestException when ex.InnerException is System.Security.Authentication.AuthenticationException =>
            ("安全な接続ができなかった…", "The secure connection failed: security software or the PC's clock?"),
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
