using System.Text;

namespace RpgLauncher;

internal static class Diagnostics
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "rpglauncher.log");

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
            File.WriteAllText(LogPath, "", new UTF8Encoding(false));
        }
        catch
        {
        }
    }
}
