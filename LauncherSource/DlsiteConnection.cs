using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;

namespace RpgLauncher;

/// <summary>How DLsite requests leave the PC (proxy and/or network adapter), saved in launcher-data.json.</summary>
internal sealed class ProxySettings
{
    /// <summary>e.g. socks5://host:1080 or http://host:8080; empty connects without a proxy.</summary>
    [JsonIgnore] public string? Address { get; set; }
    [JsonIgnore] public string? Username { get; set; }
    [JsonIgnore] public string? Password { get; set; }

    // Saved encrypted, since the address can hold user:pass@ too. Plain values from older files are read as is.
    [JsonPropertyName("address")]
    public string? SavedAddress
    {
        get => Secrets.Protect(Address);
        set
        {
            Address = Secrets.Unprotect(value);
            Unreadable = !string.IsNullOrEmpty(value) && Address is null;
        }
    }
    [JsonPropertyName("username")] public string? SavedUsername { get => Secrets.Protect(Username); set => Username = Secrets.Unprotect(value); }
    [JsonPropertyName("password")] public string? SavedPassword { get => Secrets.Protect(Password); set => Password = Secrets.Unprotect(value); }

    /// <summary>
    /// Network adapter to send DLsite connections through: an adapter name, or
    /// <see cref="DlsiteConnection.AnyVpnAdapter"/>. Null lets the system choose.
    /// </summary>
    public string? Adapter { get; set; }

    /// <summary>A proxy was saved, but on another PC or account, so it can't be decrypted here.</summary>
    [JsonIgnore] public bool Unreadable { get; private set; }

    [JsonIgnore] public bool HasProxy => !string.IsNullOrWhiteSpace(Address);
    [JsonIgnore] public bool IsSet => HasProxy || !string.IsNullOrWhiteSpace(Adapter);

    public ProxySettings Clone() => new() { Address = Address, Username = Username, Password = Password, Adapter = Adapter };
}

/// <summary>Builds the DLsite connection (proxy, adapter binding) and checks which country DLsite sees.</summary>
internal static class DlsiteConnection
{
    /// <summary>Saved adapter value meaning "whichever VPN adapter is connected".</summary>
    public const string AnyVpnAdapter = "*vpn*";

    private static readonly string[] Schemes = { "http", "socks4", "socks4a", "socks5" };

    // Words that identify VPN tunnel adapters (Linux names them tun0, wg0, ppp0 and the like).
    private static readonly string[] VpnWords =
        { "pia", "vpn", "wireguard", "wintun", "tap-windows", "tap adapter", "tunnel", "nordlynx", "mullvad", "proton",
          "tun", "wg", "ppp" };

    /// <summary>Returns false with an error when the address is unusable; true with a null proxy means no proxy.</summary>
    public static bool TryCreateProxy(ProxySettings? settings, out IWebProxy? proxy, out string? error)
    {
        proxy = null;
        error = null;
        if (settings is { Unreadable: true, HasProxy: false })
        {
            error = "it was saved on another PC or account; enter it again in \u22ef \u2192 DLsite connection\u2026";
            return false;
        }
        if (settings is null || !settings.HasProxy) return true;

        string address = settings.Address!.Trim();
        if (!address.Contains("://", StringComparison.Ordinal))
        {
            error = "Start the address with socks5:// or http:// (for example socks5://127.0.0.1:1080).";
            return false;
        }

        // socks5h ("resolve names on the proxy") is what .NET's socks5 already does.
        if (address.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase))
            address = "socks5://" + address["socks5h://".Length..];

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            !Schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(uri.Host))
        {
            error = "Use socks5://host:port, socks4://host:port or http://host:port.";
            return false;
        }

        // socks schemes have no default port, so Uri reports -1 when it was left out.
        if (uri.Port <= 0)
        {
            error = "Add the proxy's port, for example socks5://host:1080.";
            return false;
        }

        // The username and password fields take precedence over user:pass@ in the address.
        string? user = settings.Username, password = settings.Password;
        if (string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }

        var webProxy = new WebProxy(new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri)
        {
            BypassProxyOnLocal = false
        };
        if (!string.IsNullOrEmpty(user)) webProxy.Credentials = new NetworkCredential(user, password ?? "");
        proxy = webProxy;
        return true;
    }

    // ---- network adapters ----

    private static bool IsVpn(NetworkInterface nic)
    {
        string text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        return nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel ||
               VpnWords.Any(text.Contains);
    }

    private static IPAddress? IPv4Of(NetworkInterface nic) =>
        nic.GetIPProperties().UnicastAddresses
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    private static IEnumerable<NetworkInterface> ConnectedAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                          nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                          IPv4Of(nic) is not null);

    /// <summary>Connected adapters for the settings list: (saved value, label).</summary>
    public static List<(string Value, string Label)> AdapterChoices()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var nic in ConnectedAdapters().OrderByDescending(IsVpn).ThenBy(n => n.Name))
                list.Add((nic.Name, $"{nic.Name}  \u00b7  {IPv4Of(nic)}{(IsVpn(nic) ? "  \u00b7  VPN" : "")}"));
        }
        catch (NetworkInformationException)
        {
            // adapter list unavailable; the automatic choices still work
        }
        return list;
    }

    /// <summary>IPv4 address of the chosen adapter, or null with an error when it isn't connected.</summary>
    public static IPAddress? FindAdapterAddress(string adapter, out string? error) =>
        FindAdapter(adapter, out error) is { } nic ? IPv4Of(nic) : null;

    /// <summary>The chosen adapter, or null with an error when it isn't connected.</summary>
    private static NetworkInterface? FindAdapter(string adapter, out string? error)
    {
        error = null;
        var adapters = ConnectedAdapters().ToList();
        var match = adapter == AnyVpnAdapter
            ? adapters.FirstOrDefault(IsVpn)
            : adapters.FirstOrDefault(n => string.Equals(n.Name, adapter, StringComparison.OrdinalIgnoreCase));

        if (match is not null) return match;
        error = adapter == AnyVpnAdapter
            ? "No VPN adapter is connected. Turn your VPN on, or choose another adapter."
            : $"The adapter \u201c{adapter}\u201d isn't connected. Is your VPN on?";
        return null;
    }

    /// <summary>
    /// Connect callback that binds each socket to the adapter's address so traffic goes through it, e.g. a VPN
    /// that split tunneling would bypass. Fails instead of falling back when the adapter is gone.
    /// </summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> BoundConnect(string adapter)
    {
        return async (context, token) =>
        {
            var nic = FindAdapter(adapter, out var error) ??
                      throw new AdapterUnavailableException(adapter == AnyVpnAdapter ? null : adapter, error!);
            var local = IPv4Of(nic) ?? throw new HttpRequestException($"The adapter \u201c{nic.Name}\u201d has no address.");

            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token).ConfigureAwait(false);

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.Bind(new IPEndPoint(local, 0));
                if (OperatingSystem.IsLinux()) BindToDevice(socket, nic.Name);
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    /// <summary>
    /// Ties a socket to an adapter. Linux picks the route by destination, not by the bound address, so binding to the
    /// adapter's address alone could still leave through another adapter. Allowed without root since Linux 5.7.
    /// </summary>
    private static void BindToDevice(Socket socket, string adapter)
    {
        const int SolSocket = 1, SoBindToDevice = 25;
        try
        {
            socket.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(adapter + "\0"));
        }
        catch (SocketException ex)
        {
            throw new HttpRequestException($"Linux wouldn't send this connection through \u201c{adapter}\u201d: {ex.Message}", ex);
        }
    }

    /// <summary>Applies settings to every DLsite request; false with an error when the proxy is unusable.</summary>
    /// <summary>Address of the proxy DLsite requests currently go through, or null.</summary>
    public static Uri? ActiveProxy { get; private set; }

    public static bool Apply(ProxySettings? settings, out string? error)
    {
        ActiveProxy = null;
        if (!TryCreateProxy(settings, out var proxy, out error))
        {
            MetadataService.UseConnection(null, settings?.Adapter);
            return false;
        }
        MetadataService.UseConnection(proxy, settings?.Adapter);
        ActiveProxy = (proxy as WebProxy)?.Address;
        return true;
    }

    /// <summary>Requests a DLsite page with these settings and describes the country DLsite sees.</summary>
    public static async Task<string> TestAsync(ProxySettings? settings, CancellationToken token)
    {
        if (!TryCreateProxy(settings, out var proxy, out var error)) throw new InvalidOperationException(error);
        if (settings?.Adapter is { Length: > 0 } adapter && FindAdapterAddress(adapter, out var adapterError) is null)
            throw new InvalidOperationException(adapterError);

        using var client = MetadataService.CreateHttpClient(proxy, settings?.Adapter);
        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.GetAsync("https://www.dlsite.com/maniax/new",
            HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string? code = response.Headers.TryGetValues("X-Geo-Country-Code", out var values)
            ? values.FirstOrDefault()
            : null;
        return string.IsNullOrWhiteSpace(code) ? "Connected (DLsite didn't say which country it sees)" : DescribeCountry(code);
    }

    private static string DescribeCountry(string code)
    {
        try
        {
            var region = new RegionInfo(code.Trim());
            return $"DLsite sees you in {region.EnglishName} ({region.TwoLetterISORegionName})";
        }
        catch
        {
            return $"DLsite sees country code {code.Trim()}";
        }
    }
}
