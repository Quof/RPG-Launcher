using System.Text;
using Avalonia.Threading;

namespace RpgLauncher;

internal static class Diagnostics
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "rpglauncher.log");

    /// <summary>The last session's log, kept when a new session starts (a freeze or crash is in there).</summary>
    private static readonly string PreviousLogPath =
        Path.Combine(Path.GetTempPath(), "rpglauncher-previous.log");

    private static readonly object Sync = new();

    public static void Log(string message)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // diagnostics must never break the app
        }
    }

    public static void Reset()
    {
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 0) File.Copy(LogPath, PreviousLogPath, overwrite: true);
            File.WriteAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  RPG Launcher started{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static volatile string _doing = "";

    /// <summary>What the launcher is doing, as its status line says; written to the log if the window stops responding.</summary>
    public static void Doing(string what) => _doing = what;

    private static int _watching;

    /// <summary>
    /// Logs when the window's thread stops answering for 5 seconds or more, with what the launcher was doing then,
    /// and again when it answers.
    /// </summary>
    public static void WatchUiThread()
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1) return;
        var thread = new Thread(() =>
        {
            long answered = Environment.TickCount64;
            bool pending = false, stuck = false;
            while (true)
            {
                Thread.Sleep(1000);
                long now = Environment.TickCount64;
                if (!pending)
                {
                    pending = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        Volatile.Write(ref answered, Environment.TickCount64);
                        Volatile.Write(ref pending, false);
                    }, DispatcherPriority.Send);
                }
                long quiet = now - Volatile.Read(ref answered);
                if (!stuck && quiet >= 5000)
                {
                    stuck = true;
                    Log($"Window not responding for {quiet / 1000}s; it was: {_doing}");
                }
                else if (stuck && quiet < 2000)
                {
                    stuck = false;
                    Log($"Window responding again; status: {_doing}");
                }
            }
        }) { IsBackground = true, Name = "UI watchdog" };
        thread.Start();
    }
}
