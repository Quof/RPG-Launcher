using System.Runtime.InteropServices;

namespace RpgLauncher;

/// <summary>Windows-only window styling; does nothing elsewhere.</summary>
internal static class NativeMethods
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Colors the window's title bar and border for the active style (Windows 11; dark mode on Windows 10).</summary>
    public static void StyleTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;

            // Dark title bar for dark styles, light for light ones.
            int enabled = Look.IsLight ? 0 : 1;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));

            int caption = ColorRef(Look.Current.TitleBar);
            int border = ColorRef(Look.Current.TitleBorder);
            DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Title bar not styled: " + ex.Message);
        }
    }

    // COLORREF is 0x00BBGGRR
    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
}
