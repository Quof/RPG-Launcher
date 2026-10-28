using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace RpgLauncher;

/// <summary>The chosen network adapter isn't connected; DLsite requests fail rather than go out another way.</summary>
internal sealed class AdapterUnavailableException : HttpRequestException
{
    public AdapterUnavailableException(string? adapterName, string message) : base(message) => AdapterName = adapterName;

    /// <summary>Null when the setting is "any VPN adapter".</summary>
    public string? AdapterName { get; }
}

/// <summary>DLsite asked the launcher to slow down; requests to the host are paused until <see cref="Until"/>.</summary>
internal sealed class ThrottledException : HttpRequestException
{
    public ThrottledException(string host, DateTime until)
        : base($"{host} asked the launcher to slow down; trying again after {until.ToLocalTime():HH:mm}.") => Until = until;

    public DateTime Until { get; }
}

/// <summary>Why a DLsite request failed: a short reason, and what the user can do about it.</summary>
internal sealed record DlsiteProblem(string Reason, string Fix)
{
    /// <summary>One line for the status bar.</summary>
    public string Status => $"{Reason} · {Fix}";
}

/// <summary>Turns network exceptions into plain explanations.</summary>
internal static class DlsiteErrors
{
    private const string ConnectionMenu = "⋯ → DLsite connection";

    public static DlsiteProblem Describe(Exception ex) => Describe(ex, DlsiteConnection.ActiveProxy);

    /// <param name="proxy">The proxy in use: connection failures are then the proxy's.</param>
    public static DlsiteProblem Describe(Exception ex, Uri? proxy)
    {
        var chain = Chain(ex).ToList();

        if (chain.OfType<AdapterUnavailableException>().FirstOrDefault() is { } adapter)
        {
            string reason = adapter.AdapterName is null
                ? "DLsite is set to go through a VPN, and none is connected"
                : $"DLsite is set to go through “{adapter.AdapterName}”, which isn't connected";
            return new DlsiteProblem(reason, $"Turn the VPN on, or set the adapter to Automatic in {ConnectionMenu}");
        }

        if (chain.OfType<ThrottledException>().FirstOrDefault() is { } throttled)
            return new DlsiteProblem("DLsite asked the launcher to slow down",
                $"It will try again after {throttled.Until.ToLocalTime():HH:mm}");

        if (chain.Any(e => e.Message.Contains("SOCKS", StringComparison.OrdinalIgnoreCase) ||
                           e.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase)) ||
            (proxy is not null && chain.OfType<SocketException>().Any()))
        {
            bool login = chain.Any(e => e.Message.Contains("407") ||
                                        e.Message.Contains("authenticat", StringComparison.OrdinalIgnoreCase));
            string where = proxy is null ? "" : $" ({proxy.Host}:{proxy.Port})";
            return new DlsiteProblem(login ? "The proxy rejected the username or password" : $"The proxy{where} isn't responding",
                $"Check the proxy settings in {ConnectionMenu}, or clear the address to connect without one");
        }

        if (chain.OfType<HttpRequestException>().FirstOrDefault(e => e.StatusCode is not null) is { StatusCode: { } status })
        {
            return status switch
            {
                HttpStatusCode.Forbidden => new DlsiteProblem("DLsite refused the connection (403)",
                    "Your region or VPN server may be blocked; try another server"),
                HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => new DlsiteProblem(
                    "DLsite is busy or limiting requests", "Try again in a few minutes"),
                _ => new DlsiteProblem($"DLsite answered with an error ({(int)status})", "Try again later")
            };
        }

        if (chain.OfType<SocketException>().FirstOrDefault() is { } socket)
        {
            return socket.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                ? new DlsiteProblem("DLsite's address couldn't be looked up", "Check that you're online")
                : new DlsiteProblem("Couldn't connect to DLsite", "Check your internet connection and VPN");
        }

        if (chain.Any(e => e is TaskCanceledException or TimeoutException))
            return new DlsiteProblem("DLsite didn't respond in time", "Check your connection, or try again later");

        if (chain.Any(e => e is AuthenticationException))
            return new DlsiteProblem("The secure connection to DLsite failed",
                "Something on your network may be intercepting it; try another connection");

        return new DlsiteProblem(ex.Message.TrimEnd('.'), "Try again later");
    }

    private static IEnumerable<Exception> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException) yield return ex;
    }
}
