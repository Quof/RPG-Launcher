using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace RpgLauncher;

internal sealed class App : Application
{
    /// <summary>The launcher's icon, for every window.</summary>
    public static WindowIcon? WindowIcon { get; private set; }

    public override void Initialize()
    {
        Name = "RPG Launcher";
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Diagnostics.Log("THREAD EXCEPTION: " + e.Exception);
            e.Handled = true;
        };
        WindowIcon = LoadIcon();
        // Pick the font on the UI thread, before thumbnails need its name on worker threads.
        _ = Look.FontFamily;
        Look.ApplyResources();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (root, screenshot) = ParseArguments(desktop.Args ?? Array.Empty<string>());
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow(root, screenshot);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The library folder (the exe's own unless a folder is given) and an optional --screenshot path.</summary>
    private static (string Root, string? Screenshot) ParseArguments(string[] args)
    {
        string root = AppContext.BaseDirectory;
        string? screenshot = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--screenshot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                screenshot = Path.GetFullPath(args[++i]);
                continue;
            }
            if (Directory.Exists(args[i])) root = Path.GetFullPath(args[i]);
        }
        return (root, screenshot);
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream("RpgLauncher.app.ico");
            return stream is null ? null : new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Icon not loaded: " + ex.Message);
            return null;
        }
    }
}
