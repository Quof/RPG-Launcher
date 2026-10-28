using System.Net;

namespace RpgLauncher;

/// <summary>
/// Spaces out requests to each host across every queue and client, and pauses a host that answers
/// 429 or 503 (for its Retry-After, else 10 minutes) so a blocked connection isn't hammered.
/// </summary>
internal sealed class RequestThrottle : DelegatingHandler
{
    private static readonly TimeSpan PageGap = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan ImageGap = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultPause = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LongestPause = TimeSpan.FromHours(1);

    private static readonly object Lock = new();
    private static readonly Dictionary<string, DateTime> NextSlot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> PausedUntil = new(StringComparer.OrdinalIgnoreCase);

    public RequestThrottle(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string host = request.RequestUri?.Host ?? "";
        TimeSpan wait;
        lock (Lock)
        {
            var now = DateTime.UtcNow;
            if (PausedUntil.TryGetValue(host, out var until) && until > now)
                throw new ThrottledException(host, until);

            var start = NextSlot.TryGetValue(host, out var slot) && slot > now ? slot : now;
            // Images come from a CDN and are small; pages and the API get a full second.
            NextSlot[host] = start + (host.StartsWith("img.", StringComparison.OrdinalIgnoreCase) ? ImageGap : PageGap);
            wait = start - now;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);

        var response = await base.SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            var retry = response.Headers.RetryAfter;
            var pause = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? DefaultPause;
            if (pause <= TimeSpan.Zero) pause = DefaultPause;
            if (pause > LongestPause) pause = LongestPause;
            lock (Lock) PausedUntil[host] = DateTime.UtcNow + pause;
            Diagnostics.Log($"{host} answered {(int)response.StatusCode}; pausing requests for {pause.TotalMinutes:0} min");
        }
        return response;
    }
}
