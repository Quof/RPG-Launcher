using System.Diagnostics;

namespace RpgLauncher;

/// <summary>
/// Starts RPG Maker 2000/2003 games with MoshimoLoader's fixes: a shared RPG_RT.exe for games shipped without one
/// (copied in, removed after), window or full screen, the small window, one CPU core, and the memory stream error
/// patch. Or with EasyRPG Player instead.
/// </summary>
internal static class Rm2kLauncher
{
    // Game folders holding a runtime the launcher copied in, with how many running copies use it.
    private static readonly Dictionary<string, int> InUse = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] EasyRpgNames = { "Player.exe", "EasyRPG Player.exe", "easyrpg-player.exe", "easyrpg-player" };

    /// <summary>RPG_RT's command line: test or normal play, the title screen, window or full screen.</summary>
    public static string Arguments(Rm2kOptions options, bool testPlay) =>
        $"{(testPlay ? "TestPlay" : "NormalPlay")} ShowTitle {(options.Window ? "Window" : "FullScreen")}";

    /// <summary>EasyRPG Player in the game's own folder, the chosen one, or one beside the launcher; null if none.</summary>
    public static string? FindEasyRpg(Rm2kOptions options, string? gameFolder)
    {
        if (gameFolder is not null)
        {
            foreach (var name in EasyRpgNames)
            {
                if (Paths.File(Path.Combine(gameFolder, name)) is { } own && IsEasyRpg(own)) return own;
            }
        }
        if (!string.IsNullOrWhiteSpace(options.EasyRpgPath) && File.Exists(options.EasyRpgPath)) return options.EasyRpgPath;
        foreach (var folder in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "EasyRPG") })
        {
            foreach (var name in EasyRpgNames)
            {
                if (Paths.File(Path.Combine(folder, name)) is { } beside && IsEasyRpg(beside)) return beside;
            }
        }
        if (!OperatingSystem.IsWindows())
        {
            // A distribution's package.
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(folder, "easyrpg-player");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>True for EasyRPG Player (by its version info on Windows; by name elsewhere).</summary>
    private static bool IsEasyRpg(string path)
    {
        if (!path.EndsWith("Player.exe", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows()) return true;
        try
        {
            return FileVersionInfo.GetVersionInfo(path).ProductName?.Contains("EasyRPG", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The shared RPG_RT.exe for a game without its own, or null.</summary>
    public static string? SharedRuntime(Rm2kOptions options, bool is2003, IEnumerable<string> libraryFolders)
    {
        string version = is2003 ? "2003" : "2000";
        foreach (string folder in RuntimeFolders(options, libraryFolders))
        {
            if (Paths.File(Path.Combine(folder, version, "RPG_RT.exe")) is { } exe) return exe;
        }
        return null;
    }

    /// <summary>The chosen runtime folder, then any MoshimoLoader Data\Runtime at or above the game folders.</summary>
    private static IEnumerable<string> RuntimeFolders(Rm2kOptions options, IEnumerable<string> libraryFolders)
    {
        if (!string.IsNullOrWhiteSpace(options.RuntimeFolder)) yield return options.RuntimeFolder;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string library in libraryFolders)
        {
            DirectoryInfo? dir;
            try { dir = new DirectoryInfo(library); }
            catch { continue; }
            for (int up = 0; dir is not null && up < 3; up++, dir = dir.Parent)
            {
                if (!seen.Add(dir.FullName)) continue;
                if (Paths.Folder(Path.Combine(dir.FullName, "Data", "Runtime")) is { } runtime) yield return runtime;
            }
        }
    }

    /// <summary>Starts the game; null with a reason when it can't be.</summary>
    public static Process? Start(GameEntry game, Rm2kOptions options, bool testPlay, IEnumerable<string> libraryFolders,
        out string? problem)
    {
        problem = null;
        string folder = game.RootPath;
        string? own = Paths.File(Path.Combine(folder, "RPG_RT.exe"));
        bool is2003 = game.Engine.Contains("2003", StringComparison.Ordinal);

        string? easyRpg = FindEasyRpg(options, folder);
        // A game that ships EasyRPG Player instead of RPG_RT.exe was made to run on it.
        bool shipsEasyRpg = own is null && easyRpg is not null &&
                            string.Equals(Path.GetDirectoryName(easyRpg), Path.GetFullPath(folder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        string? shared = own is null && !options.UseEasyRpg && !shipsEasyRpg ? SharedRuntime(options, is2003, libraryFolders) : null;
        // EasyRPG when chosen, and for a game nothing else can start.
        if (options.UseEasyRpg || shipsEasyRpg || (own is null && shared is null))
        {
            if (easyRpg is not null) return StartEasyRpg(easyRpg, folder, options, testPlay);
            problem = options.UseEasyRpg
                ? "EasyRPG Player wasn't found.\n\nChoose its Player.exe under RPG Maker 2000/2003 → EasyRPG Player…, or put it beside the launcher."
                : $"{game.DisplayTitle} has no RPG_RT.exe.\n\nChoose a runtime folder (with 2000\\RPG_RT.exe and 2003\\RPG_RT.exe, like MoshimoLoader's Data\\Runtime) under RPG Maker 2000/2003 → Runtime folder…, or turn on Use EasyRPG Player.";
            return null;
        }

        string exe = own ?? Path.Combine(folder, "RPG_RT.exe");
        bool borrowed = false;
        if (own is null)
        {
            try
            {
                Borrow(shared!, exe);
                borrowed = true;
                if (!options.Borrowed.Contains(exe, StringComparer.OrdinalIgnoreCase)) options.Borrowed.Add(exe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"The runtime couldn't be copied into the game folder:\n{ex.Message}";
                return null;
            }
        }

        try
        {
            string arguments = Arguments(options, testPlay);
            Process? process = OperatingSystem.IsWindows()
                ? Rm2kProcess.Start(exe, arguments, folder, options.MemoryStreamFix, options.SingleCpu)
                : Process.Start(Shell.GameStartInfo(exe, folder, arguments));
            if (process is null) throw new InvalidOperationException("The game didn't start.");

            if (borrowed)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => Return(exe, options);
            }
            if (options.SmallWindow && OperatingSystem.IsWindows()) _ = Rm2kProcess.SendSmallWindowKeyAsync(process);
            return process;
        }
        catch (Exception ex)
        {
            if (borrowed) Return(exe, options);
            problem = $"Could not start the game:\n{ex.Message}\n\n{exe}";
            return null;
        }
    }

    private static Process? StartEasyRpg(string player, string folder, Rm2kOptions options, bool testPlay)
    {
        var arguments = $"--project-path \"{folder}\" {(options.Window ? "--window" : "--fullscreen")}";
        if (testPlay) arguments += " --test-play";
        return Process.Start(Shell.GameStartInfo(player, folder, arguments));
    }

    private static void Borrow(string runtime, string exe)
    {
        lock (InUse)
        {
            InUse.TryGetValue(exe, out int count);
            if (count == 0) File.Copy(runtime, exe, overwrite: false);
            InUse[exe] = count + 1;
        }
    }

    /// <summary>Removes a copied-in runtime once the last game using it has closed.</summary>
    private static void Return(string exe, Rm2kOptions options)
    {
        lock (InUse)
        {
            InUse.TryGetValue(exe, out int count);
            if (count > 1)
            {
                InUse[exe] = count - 1;
                return;
            }
            InUse.Remove(exe);
        }
        // The game's process may hold the file a moment longer.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (File.Exists(exe)) File.Delete(exe);
                Forget(options, exe);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
        Diagnostics.Log("Copied-in runtime not removed: " + exe);
    }

    /// <summary>Drops a runtime from the cleanup list, on the UI thread where the settings are saved from.</summary>
    private static void Forget(Rm2kOptions options, string exe)
    {
        void Remove() => options.Borrowed.RemoveAll(path => string.Equals(path, exe, StringComparison.OrdinalIgnoreCase));
        if (Dispatcher.UIThread.CheckAccess()) Remove();
        else Dispatcher.UIThread.Post(Remove);
    }

    /// <summary>
    /// Removes runtimes left in game folders when the launcher closed before their games did. Call off the UI
    /// thread with a copy of the list.
    /// </summary>
    public static void CleanUp(Rm2kOptions options, IReadOnlyList<string> leftovers, IEnumerable<string> libraryFolders)
    {
        foreach (string exe in leftovers)
        {
            try
            {
                // Only a file that is byte for byte a known runtime is deleted, so a game's own exe is never touched.
                if (File.Exists(exe) && IsKnownRuntime(exe, options, libraryFolders)) File.Delete(exe);
                Forget(options, exe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // still running, or not ours to remove right now
            }
        }
    }

    private static bool IsKnownRuntime(string exe, Rm2kOptions options, IEnumerable<string> libraryFolders)
    {
        var bytes = File.ReadAllBytes(exe);
        foreach (string folder in RuntimeFolders(options, libraryFolders))
        {
            foreach (var version in new[] { "2000", "2003" })
            {
                if (Paths.File(Path.Combine(folder, version, "RPG_RT.exe")) is { } runtime &&
                    new FileInfo(runtime).Length == bytes.Length && File.ReadAllBytes(runtime).AsSpan().SequenceEqual(bytes))
                    return true;
            }
        }
        return false;
    }
}
