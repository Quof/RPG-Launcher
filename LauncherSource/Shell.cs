using System.Diagnostics;
using System.Text;

namespace RpgLauncher;

/// <summary>Opens links, folders and games: directly on Windows, with xdg-open and Wine on Linux.</summary>
internal static class Shell
{
    /// <summary>Command that runs Windows games on Linux (⋯ → Wine command…), such as "wine" or "flatpak run org.winehq.Wine".</summary>
    public static string WineCommand { get; set; } = "wine";

    public static void OpenUrl(string url)
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        else Run("xdg-open", url);
    }

    public static void OpenFolder(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        else Run("xdg-open", path);
    }

    /// <summary>Opens the folder holding a file, with the file selected where the file manager supports it.</summary>
    public static void ShowInFileManager(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose();
            return;
        }

        // The freedesktop file manager interface selects the file (Nautilus, Dolphin, Nemo, Thunar and others).
        // It's waited for off the UI thread, so a slow session bus can't freeze the window.
        Task.Run(() =>
        {
            try
            {
                var info = new ProcessStartInfo("dbus-send")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in new[]
                         {
                             "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                             "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                             "array:string:" + new Uri(file).AbsoluteUri, "string:"
                         })
                    info.ArgumentList.Add(argument);
                using var process = Process.Start(info);
                if (process is not null && process.WaitForExit(3000) && process.ExitCode == 0) return;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("File manager selection unavailable: " + ex.Message);
            }
            try
            {
                Run("xdg-open", Path.GetDirectoryName(file) ?? file);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Folder not opened: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// How to start a game: the exe itself on Windows, through <see cref="WineCommand"/> elsewhere. On Linux a native
    /// build (a Ren'Py Name.sh, a Unity Name.x86_64) starts directly.
    /// </summary>
    public static ProcessStartInfo GameStartInfo(string executable, string workingDirectory, string? arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments ?? "",
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };
        }

        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // A script runs through sh, so it needn't be marked executable.
            bool script = executable.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
            var native = new ProcessStartInfo
            {
                FileName = script ? "/bin/sh" : executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false
            };
            if (script) native.ArgumentList.Add(executable);
            foreach (var part in SplitCommand(arguments ?? "")) native.ArgumentList.Add(part);
            return native;
        }

        var command = SplitCommand(WineCommand);
        if (command.Count == 0) command.Add("wine");
        var info = new ProcessStartInfo
        {
            FileName = command[0],
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        foreach (var part in command.Skip(1)) info.ArgumentList.Add(part);
        info.ArgumentList.Add(executable);
        foreach (var part in SplitCommand(arguments ?? "")) info.ArgumentList.Add(part);
        // Japanese games need a Japanese locale under Wine for their text and file names; "env LANG=… wine" overrides it.
        info.Environment["LANG"] = "ja_JP.UTF-8";
        return info;
    }

    /// <summary>
    /// On 64-bit Linux, a Ren'Py game's own Linux launcher (Name.sh beside Name.exe) when the Linux build it starts,
    /// lib/py3-linux-x86_64/Name (py2-linux-x86_64 in Ren'Py 7.5 and 7.6, linux-x86_64 before), is there and
    /// executable; otherwise null.
    /// </summary>
    public static string? RenPyLinuxLauncher(string executable)
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture !=
            System.Runtime.InteropServices.Architecture.X64)
            return null;
        try
        {
            string folder = Path.GetDirectoryName(executable) ?? "", name = Path.GetFileNameWithoutExtension(executable);
            if (Paths.File(Path.Combine(folder, name + ".sh")) is not { } script) return null;
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            foreach (var platform in new[] { "py3-linux-x86_64", "py2-linux-x86_64", "linux-x86_64" })
            {
                if (Paths.File(Path.Combine(folder, "lib", platform, name)) is { } build)
                    return (File.GetUnixFileMode(build) & anyExecute) != 0 ? script : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Wine then
        }
        return null;
    }

    /// <summary>Splits a command line into words; quotes group words.</summary>
    public static List<string> SplitCommand(string command)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool inWord = false;
        char quote = '\0';
        foreach (char c in command)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else word.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inWord = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord) words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }
        if (inWord) words.Add(word.ToString());
        return words;
    }

    private static void Run(string program, string argument)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false };
        info.ArgumentList.Add(argument);
        Process.Start(info)?.Dispose();
    }
}
