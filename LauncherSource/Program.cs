namespace RpgLauncher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Diagnostics.Reset();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Diagnostics.Log("UNHANDLED: " + e.ExceptionObject);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
