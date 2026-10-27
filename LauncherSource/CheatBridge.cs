using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RpgLauncher;

/// <summary>
/// Runs JavaScript in an RPG Maker MV or MZ game through a small script appended to its js/plugins.js for one launch.
/// The script listens on a loopback port and only accepts a client that sends the session's random token.
/// </summary>
internal sealed class CheatBridge : IDisposable
{
    private static readonly TimeSpan EvaluateTimeout = TimeSpan.FromSeconds(8);

    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _nextId;

    private CheatBridge(TcpClient client)
    {
        _client = client;
        var stream = client.GetStream();
        _reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>Connects to the game's script; null while the game isn't listening yet.</summary>
    public static async Task<CheatBridge?> TryConnectAsync(int port, string secret, CancellationToken token)
    {
        var client = new TcpClient();
        CheatBridge? bridge = null;
        bool connected = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            bridge = new CheatBridge(client);
            connected = await bridge.RequestAsync(new { id = 0, token = secret }, 0, timeout.Token).ConfigureAwait(false) == "ok";
            return connected ? bridge : null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or CheatBridgeException or JsonException ||
                                   ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (!connected)
            {
                if (bridge is not null) bridge.Dispose();
                else client.Dispose();
            }
        }
    }

    /// <summary>Evaluates JavaScript in the game's window and returns the result as text.</summary>
    public async Task<string> EvaluateAsync(string code, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(EvaluateTimeout);
        await _lock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            int id = ++_nextId;
            return await RequestAsync(new { id, code }, id, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> RequestAsync(object request, int id, CancellationToken token)
    {
        await _writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token).ConfigureAwait(false);
        while (true)
        {
            string? line = await _reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) throw new CheatBridgeException("The game closed the connection.");
            using var reply = JsonDocument.Parse(line);
            var root = reply.RootElement;
            if (!root.TryGetProperty("id", out var replyId) || replyId.ValueKind != JsonValueKind.Number || replyId.GetInt32() != id)
                continue;
            if (root.TryGetProperty("error", out var error))
                throw new CheatBridgeException(error.GetString() ?? "The game reported an error.", fromGame: true);
            return root.TryGetProperty("value", out var value) ? value.GetString() ?? "" : "";
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _lock.Dispose();
    }

    // ---- the script in plugins.js ----

    private const string StartMarker = "\n// rpg-launcher-cheats: ";
    private const string EndMarker = "// end rpg-launcher-cheats\n";

    // ES5 for NW.js 0.12 (Chromium 41). It runs when plugins.js loads, after the game's core scripts; the leading ';'
    // keeps a plugins.js without a final semicolon working. Indirect eval runs commands in the window's global scope.
    // Nothing a client sends can throw into the game. After EXPIRES a leftover copy does nothing.
    private const string Script = @"added by RPG Launcher for one cheat session; removed once the launcher connects. modified=MODIFIED
;(function () {
  if (Date.now() > EXPIRES || typeof require !== 'function') return;
  var token = 'TOKEN', sockets = [];
  var server = require('net').createServer(function (socket) {
    var pending = '', trusted = false;
    sockets.push(socket);
    socket.setEncoding('utf8');
    socket.on('error', function () {});
    socket.on('close', function () { var i = sockets.indexOf(socket); if (i >= 0) sockets.splice(i, 1); });
    socket.on('data', function (chunk) {
      try {
        pending += chunk;
        if (pending.length > 1048576) { socket.destroy(); return; }
        var end;
        while ((end = pending.indexOf('\n')) >= 0) {
          var request = JSON.parse(pending.slice(0, end));
          pending = pending.slice(end + 1);
          if (!request || typeof request !== 'object') { socket.destroy(); return; }
          if (!trusted) {
            if (request.token !== token) { socket.destroy(); return; }
            trusted = true;
            socket.write(JSON.stringify({ id: request.id, value: 'ok' }) + '\n');
            continue;
          }
          var reply = { id: request.id };
          try { reply.value = String((0, eval)(request.code)); }
          catch (e) { reply.error = String(e && e.message ? e.message : e); }
          socket.write(JSON.stringify(reply) + '\n');
        }
      } catch (e) {
        socket.destroy();
      }
    });
  });
  server.on('error', function () {});
  server.listen(PORT, '127.0.0.1');
  window.addEventListener('unload', function () {
    try { server.close(); } catch (e) {}
    for (var i = 0; i < sockets.length; i++) { try { sockets[i].destroy(); } catch (e) {} }
  });
})();
";

    /// <summary>The js/plugins.js under an MV or MZ game's data root, which the game loads before starting.</summary>
    public static string PluginsFile(string dataRoot) => Path.Combine(dataRoot, "js", "plugins.js");

    /// <summary>Appends the script for this session, replacing any leftover one. The rest of the file is kept byte for byte.</summary>
    public static void Install(string pluginsFile, int port, string secret)
    {
        var modified = File.GetLastWriteTimeUtc(pluginsFile);
        byte[] original = Strip(File.ReadAllBytes(pluginsFile), out var earlier);
        if (earlier is not null) modified = earlier.Value;
        string script = StartMarker + Script
            .Replace("MODIFIED", modified.Ticks.ToString(CultureInfo.InvariantCulture))
            .Replace("EXPIRES", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
            .Replace("TOKEN", secret)
            .Replace("PORT", port.ToString(CultureInfo.InvariantCulture)) + EndMarker;
        Write(pluginsFile, original.Concat(Encoding.ASCII.GetBytes(script)).ToArray(), null);
    }

    /// <summary>Removes the script if the file has one, restoring its original modified time.</summary>
    public static void Uninstall(string pluginsFile)
    {
        if (!File.Exists(pluginsFile)) return;
        byte[] bytes = File.ReadAllBytes(pluginsFile);
        byte[] original = Strip(bytes, out var modified);
        if (original.Length != bytes.Length) Write(pluginsFile, original, modified);
    }

    private static byte[] Strip(byte[] bytes, out DateTime? modified)
    {
        modified = null;
        byte[] start = Encoding.ASCII.GetBytes(StartMarker), end = Encoding.ASCII.GetBytes(EndMarker);
        while (true)
        {
            int from = bytes.AsSpan().IndexOf(start);
            if (from < 0) return bytes;
            int to = bytes.AsSpan(from).IndexOf(end);
            if (to < 0) return bytes;
            to += from + end.Length;

            string header = Encoding.ASCII.GetString(bytes, from, Math.Min(200, to - from));
            int at = header.IndexOf("modified=", StringComparison.Ordinal);
            if (at >= 0 && long.TryParse(new string(header[(at + 9)..].TakeWhile(char.IsDigit).ToArray()),
                    NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) &&
                ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
                modified ??= new DateTime(ticks, DateTimeKind.Utc);

            bytes = bytes[..from].Concat(bytes[to..]).ToArray();
        }
    }

    // Replaces the file in one step and keeps its creation date.
    private static void Write(string path, byte[] bytes, DateTime? modified)
    {
        var created = File.GetCreationTimeUtc(path);
        string temp = path + ".rpglauncher.tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        File.SetCreationTimeUtc(path, created);
        if (modified is { } time) File.SetLastWriteTimeUtc(path, time);
    }
}

internal sealed class CheatBridgeException : Exception
{
    public CheatBridgeException(string message, bool fromGame = false) : base(message)
    {
        FromGame = fromGame;
    }

    /// <summary>The script ran but failed in the game; the connection is still fine.</summary>
    public bool FromGame { get; }
}
