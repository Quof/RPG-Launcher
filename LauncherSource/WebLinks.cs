using System.Diagnostics.CodeAnalysis;

namespace RpgLauncher;

/// <summary>Hosts the launcher may open or download from. URLs from scraped pages are checked before use.</summary>
internal static class WebLinks
{
    private static readonly string[] Hosts = { "dlsite.com", "dlsite.jp", "nepchan.org" };

    public static bool IsTrusted([NotNullWhen(true)] string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        Hosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                          uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
}
