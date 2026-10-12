using Microsoft.UI.Xaml;

namespace Palette;

public partial class App : Application
{
    // Held for the process lifetime: enforces a single running instance.
    private static Mutex? _single;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    // Diagnostics only: set PALETTE_LOG=1 to write %LOCALAPPDATA%\palette\winui.log.
    private static readonly bool LogEnabled = Environment.GetEnvironmentVariable("PALETTE_LOG") is not null;

    internal static void Log(string msg)
    {
        if (!LogEnabled) return;
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "palette");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "winui.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("OnLaunched");
        try
        {
            _single = new Mutex(true, "Local\\PaletteWinUISingleton", out bool createdNew);
            if (!createdNew)
            {
                Log("second instance, exiting");
                Exit();
                return;
            }
            Native.Init();
            Log("engine started");
            _window = new MainWindow();
            _window.Activate();
            Log("window created");
        }
        catch (Exception ex)
        {
            Log("startup failed: " + ex);
            throw;
        }
    }
}
