using System.Drawing.Imaging;

namespace RpgLauncher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Diagnostics.Reset();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Diagnostics.Log("UNHANDLED: " + e.ExceptionObject);
        Application.ThreadException += (_, e) =>
            Diagnostics.Log("THREAD EXCEPTION: " + e.Exception);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string root = AppContext.BaseDirectory;
        string? screenshot = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--screenshot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                screenshot = Path.GetFullPath(args[++i]);
                continue;
            }

            if (Directory.Exists(args[i]))
            {
                root = Path.GetFullPath(args[i]);
            }
        }

        var form = new MainForm(root);

        if (screenshot is not null)
        {
            form.Shown += async (_, _) =>
            {
                await Task.Delay(2600);
                // Give metadata lookups a chance to finish so the capture shows them.
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (form.MetadataLookupsRunning && DateTime.UtcNow < deadline)
                    await Task.Delay(150);
                await Task.Delay(700);
                try
                {
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
                    bitmap.Save(screenshot, ImageFormat.Png);
                }
                catch
                {
                    // screenshot mode is a development aid only
                }
                form.Close();
            };
        }

        Application.Run(form);
    }
}
