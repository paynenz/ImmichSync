using System.Windows;

namespace ImmichSync;

public partial class App : System.Windows.Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool startInTray = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        instance = new Mutex(initiallyOwned: true, "Local\\ImmichSync", out bool firstInstance);
        if (!firstInstance)
        {
            instance.Dispose();
            instance = null;
            if (!startInTray)
                System.Windows.MessageBox.Show("ImmichSync is already running. Open it from the system tray.", "ImmichSync");
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        if (!startInTray || !window.IsConfigured)
            window.Show();

        window.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
