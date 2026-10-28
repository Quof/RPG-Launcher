using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RpgLauncher;

/// <summary>DLsite store lookup. Callers cache every result, misses included, to search each game only once.</summary>
internal static class MetadataService
{
    private const string UserAgent = "RPGLauncher/1.0 (+local desktop launcher)";

    private static readonly string[] Sites = { "maniax", "home" };

    /// <summary>
    /// Version of the store search; games an older version didn't find are searched again once.
    /// 2: cleaned titles and folder names; 3: part of the title; 4: censored spellings;
    /// 5: romanized names in katakana; 6: shorter title pieces until one matches; 7: two-character Japanese titles.
    /// </summary>
    public const int LookupRevision = 7;

    private const string JapaneseLocale = "ja_JP";
    private const string EnglishLocale = "en_US";

    // Cap on store searches per game, across all its names and fallbacks; the rest count as no match.
    private const int MaxSearchesPerGame = 16;
    private static readonly AsyncLocal<StrongBox<int>?> SearchesLeft = new();

    private static readonly HashSet<string> GenericTitles = new(StringComparer.Ordinal)
    {
        "game", "rpg", "rpgmaker", "rpgmakermv", "rpgmakermz", "untitled", "newgame", "test", "sample"
    };

    // Replaced when the connection changes; the old one is disposed once requests in flight have finished or timed out.
    private static volatile HttpClient Http = CreateHttpClient(null);

    /// <summary>Shared DLsite client (same headers and timeouts) for other store features.</summary>
    internal static HttpClient Client => Http;

    /// <summary>
    /// Sends all DLsite requests through <paramref name="proxy"/> and/or out of network <paramref name="adapter"/>;
    /// null leaves it to Windows.
    /// </summary>
    internal static void UseConnection(IWebProxy? proxy, string? adapter)
    {
        var old = Http;
        Http = CreateHttpClient(proxy, adapter);
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
    }

    private static readonly Regex ProductIdRegex = new(
        "data-list_item_product_id=\"(?<id>[A-Za-z]{2}\\d+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WorkNameRegex = new(
        "<dd class=\"work_name\">.*?<a[^>]*title=\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex WorkNameFallbackRegex = new(
        "<dd class=\"work_name\">.*?<a[^>]*>(?<value>[^<]*)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex MakerRegex = new(
        "<dd class=\"maker_name\">.*?<a[^>]*>(?<value>[^<]*)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex VersionSuffixRegex = new(
        @"[\s\-–—_]*(?:ver(?:sion)?|v)\.?\s*\d+(?:\.\d+)*[a-z]?[\s\-–—_]*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BracketIdRegex = new(
        @"\s*\[(?:RJ|VJ|BJ)\d+\]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HtmlTagRegex = new(
        "<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Searches the store for each name in turn until one matches.</summary>
    /// <param name="strict">
    /// Short Latin titles match only exactly: for Ren'Py and Unity games, mostly western, whose short English titles
    /// begin many unrelated store titles.
    /// </param>
    public static async Task<MetadataLookupResult> LookupAsync(IReadOnlyList<string> titles, CancellationToken token, bool strict = false)
    {
        MetadataLookupResult? failure = null;
        bool searched = false;
        SearchesLeft.Value = new StrongBox<int>(MaxSearchesPerGame);
        foreach (string title in titles)
        {
            string query = CleanTitle(title);
            string normalized = Normalize(query);
            // Japanese titles can be two characters long; Latin ones need three.
            int shortest = normalized.Any(ch => ch >= '\u3040') ? 2 : 3;
            if (normalized.Length < shortest || GenericTitles.Contains(normalized)) continue;

            if (searched) await Task.Delay(400, token).ConfigureAwait(false);
            searched = true;
            var result = await LookupTitleAsync(query, strict, token).ConfigureAwait(false);
            if (result.Metadata is not null) return result;
            if (!result.NotFound) failure ??= result;
        }

        // Report a failed search, not a miss: failures are retried next launch.
        return failure ?? MetadataLookupResult.Miss();
    }

    private static async Task<MetadataLookupResult> LookupTitleAsync(string query, bool strict, CancellationToken token)
    {
        try
        {
            foreach (string site in Sites)
            {
                token.ThrowIfCancellationRequested();

                var candidates = await SearchAsync(site, query, token).ConfigureAwait(false);
                var best = PickBest(candidates, query, strict);

                // DLsite's search fails if one word differs (spelling, added brackets, censoring): try shorter
                // pieces until one finds a work matching the whole title. Skipped for the rarely used all-ages site.
                if (best is null && site == Sites[0])
                {
                    foreach (string part in ShortKeywords(query))
                    {
                        await Task.Delay(400, token).ConfigureAwait(false);
                        best = PickBest(await SearchAsync(site, part, token).ConfigureAwait(false), query, strict);
                        if (best is not null) break;
                    }
                }

                // A name the game writes in Latin letters may be katakana in the store title (Sakura → サクラ):
                // search that spelling and accept a single result containing every part of the game's title.
                if (best is null && !strict && KatakanaSearch(query) is { } spelled)
                {
                    await Task.Delay(400, token).ConfigureAwait(false);
                    var found = await SearchAsync(site, spelled.Keyword, token).ConfigureAwait(false);
                    var containing = found.Where(c => spelled.Parts.All(Normalize(c.Title).Contains)).ToList();
                    if (containing.Count == 1)
                    {
                        best = containing[0];
                        best.Score = 0.85;
                    }
                }
                if (best is null) continue;

                // Pause between requests to go easy on the store.
                await Task.Delay(400, token).ConfigureAwait(false);

                return await FetchProductAsync(site, best, token).ConfigureAwait(false)
                       ?? MetadataLookupResult.Fail("DLsite returned an empty product response.");
            }

            return MetadataLookupResult.Miss();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return MetadataLookupResult.Fail("DLsite took too long to respond.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MetadataLookupResult.Fail(ex.Message);
        }
    }

    /// <summary>The product code (RJ01234567) in a DLsite link or typed on its own, or null.</summary>
    public static string? ProductIdFrom(string? text)
    {
        var match = Regex.Match(text ?? "", @"(?<![A-Za-z])(RJ|VJ|BJ)\d{6,8}(?!\d)", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    /// <summary>Loads a known product page directly, for games whose store title can't be found by searching.</summary>
    public static async Task<MetadataLookupResult> LookupProductAsync(string productId, CancellationToken token)
    {
        try
        {
            foreach (string site in Sites)
            {
                token.ThrowIfCancellationRequested();
                var found = await FetchProductAsync(site, new Candidate(productId, "", "") { Score = 1 }, token)
                    .ConfigureAwait(false);
                if (found is not null) return found;
                await Task.Delay(400, token).ConfigureAwait(false);
            }
            return MetadataLookupResult.Miss();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return MetadataLookupResult.Fail("DLsite took too long to respond.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MetadataLookupResult.Fail(ex.Message);
        }
    }

    /// <summary>A product's Japanese page plus its English text, or null when the site has no such product.</summary>
    private static async Task<MetadataLookupResult?> FetchProductAsync(string site, Candidate product, CancellationToken token)
    {
        string jaJson = await Http.GetStringAsync(
            ProductUrl(site, product.Id, JapaneseLocale), token).ConfigureAwait(false);
        var metadata = ParseProduct(jaJson, site, product);
        if (metadata is null) return null;

        // An English failure keeps the Japanese metadata; the caller retries English next launch.
        bool englishFailed = false;
        string? enJson = null;
        try
        {
            await Task.Delay(400, token).ConfigureAwait(false);
            enJson = await Http.GetStringAsync(
                ProductUrl(site, product.Id, EnglishLocale), token).ConfigureAwait(false);
            if (ParseText(enJson) is { } english) metadata.English = english;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            englishFailed = true;
        }

        if (metadata.English is { } text && enJson is not null)
        {
            try
            {
                if (metadata.NeedsEnglishTitle() &&
                    await EnglishEditionTitleAsync(site, product.Id, enJson, token).ConfigureAwait(false) is { } title)
                    text.Title = title;
                metadata.EnglishTitleChecked = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // checked again later
            }
        }

        return MetadataLookupResult.Found(metadata, englishFailed);
    }

    // Tags DLsite puts on English editions' titles: "[ENG TL Patch] …", "[ENG TL] …".
    private static readonly Regex EditionTagRegex = new(
        @"^\s*[\[【(（][^\]】)）]*(?:ENG|English|TL|Translat|翻訳)[^\]】)）]*[\]】)）]\s*" +
        @"|\s*[\[【(（][^\]】)）]*(?:ENG|English|Translat|翻訳)[^\]】)）]*[\]】)）]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The title of a work's English edition (a separate product listed under language_editions), or null.
    /// <paramref name="productJson"/> is the work's English product data when already fetched.
    /// </summary>
    public static async Task<string?> EnglishEditionTitleAsync(string site, string productId, string? productJson,
        CancellationToken token)
    {
        productJson ??= await Http.GetStringAsync(ProductUrl(site, productId, EnglishLocale), token).ConfigureAwait(false);
        string? edition = EnglishEditionId(productJson, productId);
        if (edition is null) return null;

        await Task.Delay(400, token).ConfigureAwait(false);
        string json = await Http.GetStringAsync(ProductUrl(site, edition, EnglishLocale), token).ConfigureAwait(false);
        if (ParseText(json) is not { Title.Length: > 0 } text) return null;
        string title = EditionTagRegex.Replace(text.Title, string.Empty).Trim();
        return title.Length > 0 ? title : null;
    }

    private static string? EnglishEditionId(string json, string productId)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
        var work = doc.RootElement[0];
        if (work.ValueKind != JsonValueKind.Object || !work.TryGetProperty("language_editions", out var editions)) return null;

        IEnumerable<JsonElement> items = editions.ValueKind switch
        {
            JsonValueKind.Object => editions.EnumerateObject().Select(p => p.Value),
            JsonValueKind.Array => editions.EnumerateArray(),
            _ => Enumerable.Empty<JsonElement>()
        };
        foreach (var edition in items)
        {
            if (edition.ValueKind != JsonValueKind.Object || GetString(edition, "lang") != "ENG") continue;
            string? workno = GetString(edition, "workno");
            if (!string.IsNullOrWhiteSpace(workno) && !string.Equals(workno, productId, StringComparison.OrdinalIgnoreCase))
                return workno;
        }
        return null;
    }

    /// <summary>Fetches only the English store text for a product already known to us.</summary>
    public static async Task<MetadataLookupResult> LookupEnglishAsync(GameMetadata metadata, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(metadata.ProductId) || string.IsNullOrWhiteSpace(metadata.Site))
            return MetadataLookupResult.Miss();

        try
        {
            string json = await Http.GetStringAsync(
                ProductUrl(metadata.Site, metadata.ProductId, EnglishLocale), token).ConfigureAwait(false);
            var text = ParseText(json);
            return text is null ? MetadataLookupResult.Miss() : MetadataLookupResult.FoundTranslation(text);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return MetadataLookupResult.Fail("DLsite took too long to respond.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MetadataLookupResult.Fail(ex.Message);
        }
    }

    private static async Task<List<Candidate>> SearchAsync(string site, string keyword, CancellationToken token)
    {
        if (SearchesLeft.Value is { } left && left.Value-- <= 0) return new List<Candidate>();
        // DLsite returns 403 for %20 in the keyword path; a plus sign is accepted.
        string path = Uri.EscapeDataString(keyword).Replace("%20", "+", StringComparison.Ordinal);
        string html = await Http.GetStringAsync(
            $"https://www.dlsite.com/{site}/fsr/=/language/jp/keyword/{path}", token).ConfigureAwait(false);
        return ParseSearch(html);
    }

    private static readonly Regex KeywordSplitRegex = new(
        @"[\s～~〜\-–—「」『』()（）【】\[\]、。!！?？・]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TitlePartRegex = new(
        @"[A-Za-z]+|[^A-Za-z\s\-–—_~～・:：!！?？()（）\[\]【】]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// For titles made of a label and a name (XYZ-Sakura): the name to search for, in katakana when romanized,
    /// and each title part normalized as the store title should contain it. Null for other titles.
    /// </summary>
    private static (string Keyword, List<string> Parts)? KatakanaSearch(string query)
    {
        var parts = new List<string>();
        string? keyword = null;
        foreach (Match match in TitlePartRegex.Matches(query))
        {
            string part = match.Value;
            // a name, not an acronym such as RPG
            bool name = part.Length >= 3 && part.Any(char.IsLower) && part.Any(ch => "aeiouAEIOU".Contains(ch));
            string? kana = name ? Romaji.ToKatakana(part) : null;
            // a romanized name in katakana, or a Japanese part as it is
            string? candidate = kana ?? (part.Any(ch => ch > '　') ? part : null);
            if (candidate is not null && (keyword is null || candidate.Length > keyword.Length)) keyword = candidate;
            string normalized = Normalize(kana ?? part);
            if (normalized.Length > 0) parts.Add(normalized);
        }
        return keyword is { Length: >= 2 } && parts.Count >= 2 ? (keyword, parts) : null;
    }

    // Where a subtitle starts: "Title～Subtitle" → "Title".
    private static readonly Regex SubtitleRegex = new(
        @"\s*[～~〜\-–—(（【\[「『]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Runs of katakana (often names) or of kanji.
    private static readonly Regex ScriptRunRegex = new(
        @"[\u30A1-\u30FF]{3,}|[\u4E00-\u9FFF]{3,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Up to five shorter searches for a title: the part before its subtitle, its words or phrases
    /// (first, then longest), then its katakana and kanji runs (longest first).
    /// </summary>
    private static IEnumerable<string> ShortKeywords(string query)
    {
        var keywords = new List<string>();
        void Add(string keyword)
        {
            keyword = keyword.Trim();
            if (Normalize(keyword).Length >= 3 && keyword != query && !keywords.Contains(keyword)) keywords.Add(keyword);
        }

        var subtitle = SubtitleRegex.Match(query);
        if (subtitle.Success && subtitle.Index > 0) Add(query[..subtitle.Index]);

        // Latin titles also without spaces; long Japanese ones by their start, in case the store's ending differs.
        if (query.Contains(' ') && query.All(ch => ch < 128)) Add(query.Replace(" ", ""));
        if (query.Length >= 10 && query.Any(ch => ch > '　')) Add(query[..7]);

        var parts = KeywordSplitRegex.Split(query).Where(part => Normalize(part).Length >= 3).Distinct().ToList();
        if (parts.Count >= 2)
        {
            foreach (string part in parts.Take(1).Concat(parts.Skip(1).OrderByDescending(part => part.Length)).Take(3))
                Add(part);
        }

        foreach (string run in ScriptRunRegex.Matches(query).Select(m => m.Value).OrderByDescending(run => run.Length))
            Add(run);
        return keywords.Take(5);
    }

    private static string ProductUrl(string site, string productId, string locale) =>
        $"https://www.dlsite.com/{site}/api/=/product.json?workno={Uri.EscapeDataString(productId)}&locale={locale}";

    internal static string CleanTitle(string raw)
    {
        // Store titles use spaces where games often use underscores.
        string title = GameTitles.Clean(raw).Replace('_', ' ');
        title = VersionSuffixRegex.Replace(title, string.Empty);
        title = BracketIdRegex.Replace(title, string.Empty);
        return title.Trim().Trim('-', '–', '—', '_', ':', '：', '・', '~', '～', ' ');
    }

    private static string Normalize(string value)
    {
        string form = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(form.Length);
        foreach (char ch in form)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
        }
        return builder.ToString();
    }

    private static List<Candidate> ParseSearch(string html)
    {
        var candidates = new List<Candidate>();
        var matches = ProductIdRegex.Matches(html);
        for (int i = 0; i < matches.Count; i++)
        {
            string id = matches[i].Groups["id"].Value;
            if (candidates.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))) continue;

            int start = matches[i].Index;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : html.Length;
            string block = html.Substring(start, Math.Max(0, end - start));

            var titleMatch = WorkNameRegex.Match(block);
            if (!titleMatch.Success) titleMatch = WorkNameFallbackRegex.Match(block);
            if (!titleMatch.Success) continue;

            var makerMatch = MakerRegex.Match(block);
            candidates.Add(new Candidate(
                id,
                WebUtility.HtmlDecode(titleMatch.Groups["value"].Value).Trim(),
                makerMatch.Success ? WebUtility.HtmlDecode(makerMatch.Groups["value"].Value).Trim() : string.Empty));
        }
        return candidates;
    }

    private static Candidate? PickBest(List<Candidate> candidates, string query, bool strict = false)
    {
        string local = Normalize(query);
        Candidate? best = null;
        double bestScore = 0;
        bool exactOnly = local.Length <= 3 || (strict && local.Length < 10 && local.All(char.IsAscii));

        foreach (var candidate in candidates)
        {
            string other = Normalize(candidate.Title);
            if (other.Length == 0) continue;

            // Short titles only match exactly; many works merely contain them.
            if (exactOnly && other != local) continue;

            // The store title is ours plus a number: a sequel.
            if (other.Length > local.Length && other.StartsWith(local, StringComparison.Ordinal) &&
                char.IsDigit(other[local.Length])) continue;

            double score;
            if (other == local)
            {
                score = 1.0;
            }
            else if (other.StartsWith(local, StringComparison.Ordinal))
            {
                // The store adds a subtitle.
                score = 0.97 - 0.01 * Math.Min(3, other.Length - local.Length);
            }
            else if (local.StartsWith(other, StringComparison.Ordinal) && other.Length * 2 >= local.Length)
            {
                // Ours adds a subtitle; a store title under half our length is a different, shorter work.
                score = 0.94 - 0.01 * Math.Min(3, local.Length - other.Length);
            }
            else
            {
                score = Similarity(local, other);
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        // A looser match counts if a long run of the name is identical (one word spelled differently).
        if (best is null) return null;
        if (bestScore < 0.84 &&
            !(bestScore >= 0.7 && LongestCommonRun(local, Normalize(best.Title)) >= 8)) return null;
        best.Score = bestScore;
        return best;
    }

    private static int LongestCommonRun(string a, string b)
    {
        int best = 0;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                current[j] = a[i - 1] == b[j - 1] ? previous[j - 1] + 1 : 0;
                if (current[j] > best) best = current[j];
            }
            (previous, current) = (current, previous);
        }
        return best;
    }

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        int distance = Levenshtein(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private static GameMetadata? ParseProduct(string json, string site, Candidate candidate)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        var work = doc.RootElement[0];
        if (work.ValueKind != JsonValueKind.Object) return null;

        string? workno = GetString(work, "workno");
        string id = workno is not null && Regex.IsMatch(workno, @"^[A-Za-z]{2}\d+$") ? workno : candidate.Id;
        string title = GetString(work, "work_name") ?? candidate.Title;
        string maker = GetString(work, "maker_name") ?? candidate.Maker;

        DateTime? released = null;
        string? dateText = GetString(work, "regist_date");
        if (DateTime.TryParseExact(dateText, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            released = parsed.Date;
        }

        var tags = ReadTags(work);

        string? image = null;
        if (work.TryGetProperty("image_main", out var imageElement) &&
            imageElement.ValueKind == JsonValueKind.Object)
        {
            image = GetString(imageElement, "url");
            if (!string.IsNullOrWhiteSpace(image) && image!.StartsWith("//", StringComparison.Ordinal))
                image = "https:" + image;
            if (!WebLinks.IsTrusted(image)) image = null;
        }

        string? description = CleanDescription(GetString(work, "intro_s") ?? GetString(work, "intro"));

        return new GameMetadata
        {
            Source = "DLsite",
            Site = site,
            ProductId = id,
            Url = $"https://www.dlsite.com/{site}/work/=/product_id/{id}.html",
            Title = title,
            Maker = maker,
            WorkType = GetString(work, "work_type") ?? string.Empty,
            WorkTypeName = GetString(work, "work_type_string") ?? string.Empty,
            Released = released,
            Tags = tags,
            Description = description,
            ImageUrl = image,
            Match = candidate.Score,
            FetchedUtc = DateTime.UtcNow
        };
    }

    /// <summary>Parses one localized product response (title, circle, type, tags, description).</summary>
    private static GameMetadataText? ParseText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        var work = doc.RootElement[0];
        if (work.ValueKind != JsonValueKind.Object) return null;

        return new GameMetadataText
        {
            Title = GetString(work, "work_name") ?? string.Empty,
            Maker = GetString(work, "maker_name") ?? string.Empty,
            WorkType = GetString(work, "work_type") ?? string.Empty,
            WorkTypeName = GetString(work, "work_type_string") ?? string.Empty,
            Description = CleanDescription(GetString(work, "intro_s") ?? GetString(work, "intro")),
            Tags = ReadTags(work)
        };
    }

    private static List<string> ReadTags(JsonElement work)
    {
        var tags = new List<string>();
        if (!work.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
            return tags;

        foreach (var genre in genres.EnumerateArray())
        {
            string? name = genre.ValueKind == JsonValueKind.Object ? GetString(genre, "name") : null;
            if (!string.IsNullOrWhiteSpace(name)) tags.Add(name.Trim());
            if (tags.Count >= 16) break;
        }
        return tags;
    }

    private static string? CleanDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = WebUtility.HtmlDecode(HtmlTagRegex.Replace(value, " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length > 700) text = text[..700].TrimEnd() + "\u2026";
        return text.Length == 0 ? null : text;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    /// <summary>Without a proxy, follows Windows' network settings; with one, never connects directly.</summary>
    internal static HttpClient CreateHttpClient(IWebProxy? proxy, string? adapter = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        if (proxy is not null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        if (!string.IsNullOrWhiteSpace(adapter))
            handler.ConnectCallback = DlsiteConnection.BoundConnect(adapter);
        var client = new HttpClient(new RequestThrottle(handler)) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/json;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en;q=0.8");
        return client;
    }

    private sealed class Candidate
    {
        public Candidate(string id, string title, string maker)
        {
            Id = id;
            Title = title;
            Maker = maker;
        }

        public string Id { get; }
        public string Title { get; }
        public string Maker { get; }
        public double Score { get; set; }
    }
}
