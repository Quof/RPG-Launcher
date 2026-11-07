using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RpgLauncher;

/// <summary>
/// Turns a work's original download link (as the matsuri listed it) into a file to download: a free itch.io game,
/// a Google Drive file, a getuploader upload, or a file saved on the Wayback Machine. Other hosts (MEGA, Yahoo Box…)
/// open in the browser.
/// </summary>
/// <summary>An upload needs a download password, or the one given was wrong.</summary>
internal sealed class PasswordRequiredException(bool wrong) : Exception(wrong ? "The download password was wrong." : "This upload needs a download password.")
{
    public bool Wrong { get; } = wrong;
}

internal static class VipOriginal
{
    private static readonly Regex CsrfRegex = new("name=\"csrf_token\" value=\"([^\"]+)\"", RegexOptions.Compiled);
    // Each upload's id and the file name shown under it (<strong title="…" class="name">), read together.
    private static readonly Regex UploadRegex = new(@"data-upload_id=""([0-9]+)"".*?<strong[^>]*\btitle=""([^""]*)""",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex DriveIdRegex = new(@"(?:[?&]id=|/d/)([A-Za-z0-9_\-]{20,})", RegexOptions.Compiled);
    private static readonly Regex WaybackRegex = new(@"^https?://web\.archive\.org/web/([0-9]+)(?:[a-z_]*)/(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UploaderPageRegex = new(@"^/[^/]+/download/[0-9]+(?:/[^/]*)?/?$", RegexOptions.Compiled);
    private static readonly Regex UploaderPasswordRegex = new("type=\"password\" name=\"password\"", RegexOptions.Compiled);
    private static readonly Regex UploaderTokenRegex = new(@"name=""token"" value=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex UploaderFileRegex = new(@"https://download[a-z0-9]*\.getuploader\.com/g/[^""'<>\s]+", RegexOptions.Compiled);
    private static readonly string[] ArchiveExtensions = { ".zip", ".rar", ".7z", ".lzh", ".exe" };

    /// <summary>True when the launcher can download from this link itself.</summary>
    public static bool CanFetch(string? url) => Kind(url) is not null;

    private static string? Kind(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        string host = uri.Host.ToLowerInvariant();
        if (host.EndsWith(".itch.io", StringComparison.Ordinal) && uri.AbsolutePath.Trim('/').Length > 0) return "itch";
        if (host is "drive.google.com" or "drive.usercontent.google.com" && DriveIdRegex.IsMatch(url!)) return "drive";
        if (host.EndsWith(".getuploader.com", StringComparison.Ordinal) && UploaderPageRegex.IsMatch(uri.AbsolutePath)) return "uploader";
        if (host == "web.archive.org" && WaybackRegex.Match(url!) is { Success: true } wayback &&
            ArchiveExtensions.Any(ext => wayback.Groups[2].Value.Split('?')[0].EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return "wayback";
        return null;
    }

    /// <summary>The direct file address and its name (when known) behind a work's original link.</summary>
    public static async Task<(string Url, string? FileName)> ResolveAsync(string url, CancellationToken token, string? password = null)
    {
        url = url.Trim();
        switch (Kind(url))
        {
            case "itch":
                return await ResolveItchAsync(url, token);
            case "uploader":
                return await ResolveUploaderAsync(url, password, token);
            case "drive":
                string id = DriveIdRegex.Match(url).Groups[1].Value;
                // confirm=t skips the "can't scan this large file for viruses" page.
                return ($"https://drive.usercontent.google.com/download?id={id}&export=download&confirm=t", null);
            case "wayback":
                var match = WaybackRegex.Match(url);
                // "id_" asks for the saved file itself rather than the archive's page around it.
                return ($"https://web.archive.org/web/{match.Groups[1].Value}id_/{match.Groups[2].Value}",
                    Path.GetFileName(match.Groups[2].Value.Split('?')[0]));
            default:
                throw new NotSupportedException("The launcher can't download from this site.");
        }
    }

    /// <summary>
    /// A getuploader upload: its page's ダウンロード button posts a one-time token back to the page, which answers with
    /// the file's own address. Deleted and password-protected uploads have no such button.
    /// </summary>
    private static async Task<(string Url, string? FileName)> ResolveUploaderAsync(string page, string? password, CancellationToken token)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (RPG Launcher)");
        string html = await client.GetStringAsync(page, token);

        async Task<string> Post(Dictionary<string, string> fields)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, page) { Content = new FormUrlEncodedContent(fields) };
            request.Headers.Referrer = new Uri(page);
            using var response = await client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(token);
        }

        // A download password (often the matsuri's own, given on its page or in the thread) comes first.
        if (UploaderPasswordRegex.IsMatch(html))
        {
            if (string.IsNullOrEmpty(password)) throw new PasswordRequiredException(wrong: false);
            html = await Post(new Dictionary<string, string> { ["password"] = password, ["yes"] = "認証" });
            if (UploaderPasswordRegex.IsMatch(html)) throw new PasswordRequiredException(wrong: true);
        }
        if (UploaderFileRegex.Match(html) is not { Success: true } link)
        {
            string formToken = UploaderTokenRegex.Match(html) is { Success: true } m ? m.Groups[1].Value
                : throw new NotSupportedException("This uploader page has no download button: the file may have been deleted.");
            html = await Post(new Dictionary<string, string> { ["token"] = formToken, ["yes"] = "ダウンロード" });
            link = UploaderFileRegex.Match(html);
            if (!link.Success) throw new InvalidDataException("The uploader didn't give the file's address.");
        }
        string direct = WebUtility.HtmlDecode(link.Value);
        return (direct, Uri.UnescapeDataString(direct.Split('?')[0].Split('/')[^1]));
    }

    /// <summary>The extension a downloaded file's first bytes say it is (.zip, .rar, .7z, .lzh, .exe), or .bin.</summary>
    public static string SniffExtension(string path)
    {
        var head = new byte[8];
        int read;
        using (var file = File.OpenRead(path)) read = file.Read(head, 0, head.Length);
        var bytes = head.AsSpan(0, read);
        if (bytes.StartsWith("PK"u8)) return ".zip";
        if (bytes.StartsWith("Rar!"u8)) return ".rar";
        if (bytes.StartsWith(new byte[] { 0x37, 0x7A, 0xBC, 0xAF })) return ".7z";
        if (bytes.StartsWith("MZ"u8)) return ".exe";
        if (read >= 5 && bytes[2] == '-' && bytes[3] == 'l' && bytes[4] == 'h') return ".lzh";
        return ".bin";
    }

    /// <summary>
    /// A free itch.io game: the page's download button asks the game's file endpoint (with the page's CSRF token and
    /// cookie) for a short-lived link. Paid and pay-what-you-want games have no such button.
    /// </summary>
    private static async Task<(string Url, string? FileName)> ResolveItchAsync(string page, CancellationToken token)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (RPG Launcher)");
        string html = await client.GetStringAsync(page, token);
        if (!html.Contains("direct_download_btn", StringComparison.Ordinal) && !html.Contains("download_btn", StringComparison.Ordinal))
            throw new NotSupportedException("This itch.io page has no free download (it may be paid or pay-what-you-want).");
        string csrf = CsrfRegex.Match(html) is { Success: true } m ? m.Groups[1].Value
            : throw new InvalidDataException("The itch.io page has no download token.");

        // Uploads in page order, with their names; a Windows build or archive is preferred, and pictures (a page often
        // offers its icon too) come last.
        var found = UploadRegex.Matches(html).Select(x => (Id: x.Groups[1].Value, Name: WebUtility.HtmlDecode(x.Groups[2].Value)))
            .DistinctBy(x => x.Id).ToList();
        if (found.Count == 0) throw new NotSupportedException("This itch.io page has no files to download.");
        var uploads = found.Select(x => x.Id).ToList();
        var names = found.Select(x => x.Name).ToList();
        int Rank(int i)
        {
            string name = names[i].ToLowerInvariant();
            if (new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico" }.Any(name.EndsWith)) return 4;
            if (name.Contains("mac") || name.Contains("linux") || name.Contains("android") || name.EndsWith(".apk")) return 3;
            if (name.Contains("win") || ArchiveExtensions.Any(name.EndsWith)) return 0;
            return 1;
        }
        int best = Enumerable.Range(0, uploads.Count).OrderBy(Rank).First();

        string trimmed = page.Split('?')[0].TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{trimmed}/file/{uploads[best]}?source=view_game&as_props=1&after_download_lightbox=true")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["csrf_token"] = csrf })
        };
        request.Headers.Referrer = new Uri(page);
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        string direct = json.RootElement.TryGetProperty("url", out var link) && link.GetString() is { } value
            ? value : throw new InvalidDataException("itch.io didn't give a download link" + ItchError(json.RootElement));
        return (direct, names[best]);
    }

    /// <summary>itch.io's own reason ("errors": […]), as ": reason", or ".".</summary>
    private static string ItchError(JsonElement root) =>
        root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
            ? ": " + string.Join(" ", errors.EnumerateArray().Select(e => e.ToString()))
            : ". " + root.GetRawText()[..Math.Min(200, root.GetRawText().Length)];
}
