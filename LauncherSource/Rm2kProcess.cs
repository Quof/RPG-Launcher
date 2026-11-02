using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RpgLauncher;

/// <summary>
/// Windows process plumbing for RPG_RT.exe: start it suspended so the memory stream error can be patched out
/// before it runs, pin it to one CPU, and press F5 for the small window.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Rm2kProcess
{
    private const uint CreateSuspended = 0x4;
    private const uint WmKeyDown = 0x100, WmKeyUp = 0x101, VkF5 = 0x74;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr mask);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int size);

    /// <summary>The game's TButton window, which takes RPG_RT's keys (MoshimoLoader sends F5 there), or zero.</summary>
    private static IntPtr FindKeyWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        var name = new StringBuilder(64);
        bool Check(IntPtr window, IntPtr _)
        {
            name.Clear();
            GetClassNameW(window, name, name.Capacity);
            if (name.ToString() != "TButton") return true;
            found = window;
            return false;
        }
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out int owner);
            if (owner != processId) return true;
            if (!Check(window, IntPtr.Zero)) return false;
            EnumChildWindows(window, Check, IntPtr.Zero);
            return found == IntPtr.Zero;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Starts RPG_RT.exe suspended, applies the fixes, then lets it run.</summary>
    public static Process Start(string exe, string arguments, string folder, bool memoryStreamFix, bool singleCpu)
    {
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        var commandLine = new StringBuilder($"\"{exe}\" {arguments}");
        if (!CreateProcessW(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended, IntPtr.Zero, folder,
                ref startup, out var info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            // Taken before it runs, so even a game that quits at once is still there to watch.
            var process = Process.GetProcessById(info.dwProcessId);
            if (memoryStreamFix)
            {
                try
                {
                    string result = Rm2kPatch.Apply(info.hProcess, exe);
                    Diagnostics.Log($"Memory stream fix for {exe}: {result}");
                }
                catch (Exception ex)
                {
                    Diagnostics.Log($"Memory stream fix skipped for {exe}: {ex.Message}");
                }
            }
            if (singleCpu) SetProcessAffinityMask(info.hProcess, (UIntPtr)1);
            // Open the Process object's own handle now: once the game has exited, its exit code can't be read otherwise.
            _ = process.SafeHandle;
            ResumeThread(info.hThread);
            return process;
        }
        catch
        {
            TerminateProcess(info.hProcess, 1);
            throw;
        }
        finally
        {
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
        }
    }

    /// <summary>Presses F5 once the game window is up, which RPG_RT takes as "small window" (320×240).</summary>
    public static async Task SendSmallWindowKeyAsync(Process process, int delayMs = 500)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            IntPtr window = IntPtr.Zero;
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                window = FindKeyWindow(process.Id);
                if (window != IntPtr.Zero) break;
                await Task.Delay(100);
            }
            if (window == IntPtr.Zero || process.HasExited) return;
            await Task.Delay(delayMs);
            const int scanCode = 0x3F;
            PostMessageW(window, WmKeyDown, (UIntPtr)VkF5, (IntPtr)(1 | (scanCode << 16)));
            PostMessageW(window, WmKeyUp, (UIntPtr)VkF5, unchecked((IntPtr)(1 | (scanCode << 16) | (3 << 30))));
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Small window key not sent: " + ex.Message);
        }
    }
}
