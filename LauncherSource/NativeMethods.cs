using System.Runtime.InteropServices;

namespace RpgLauncher;

internal static class NativeMethods
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    public static string ApplyDarkTitleBar(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "no handle";

        // Dark title bar for dark styles, light for light ones.
        int enabled = Theme.IsLight ? 0 : 1;
        int dark = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        int darkLegacy = 0;
        if (dark != 0)
            darkLegacy = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));

        // COLORREF is 0x00BBGGRR
        int caption = ColorRef(Theme.Current.TitleBar);
        int border = ColorRef(Theme.Current.TitleBorder);
        int captionResult = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
        int borderResult = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));

        try
        {
            SetWindowTheme(handle, Theme.IsLight ? "Explorer" : "DarkMode_Explorer", null);
        }
        catch
        {
            // older Windows builds do not support the dark explorer theme
        }

        return $"dark={dark} legacy={darkLegacy} caption={captionResult} border={borderResult}";
    }

    private static int ColorRef(Color color)
        => color.R | (color.G << 8) | (color.B << 16);
}
