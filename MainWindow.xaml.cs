using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ImmichSync;

public partial class MainWindow : Window
{
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ToolStripMenuItem traySync;
    private readonly System.Drawing.Icon trayIcon;
    private readonly DispatcherTimer scheduler = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Queue<string> recentLog = new();
    private SyncSettings? settings;
    private Task? activeSync;
    private Task? albumLoad;
    private DateTimeOffset nextCheck;
    private Uri? albumServer;
    private bool exiting;

    internal bool IsConfigured => settings is not null;

    public MainWindow()
    {
        InitializeComponent();

        var iconUri = new Uri("pack://application:,,,/Assets/ImmichSync.ico");
        using var iconStream = System.Windows.Application.GetResourceStream(iconUri)!.Stream;
        using var sourceIcon = new System.Drawing.Icon(iconStream);
        trayIcon = (System.Drawing.Icon)sourceIcon.Clone();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open ImmichSync", null, (_, _) => Dispatcher.InvokeAsync(ShowWindow));
        traySync = (Forms.ToolStripMenuItem)menu.Items.Add("Sync now", null, (_, _) => Dispatcher.InvokeAsync(RequestSync));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.InvokeAsync(Quit));
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "ImmichSync", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.InvokeAsync(ShowWindow);

        try
        {
            settings = SettingsStore.Load();
            if (settings is not null)
            {
                ServerAddress.Text = settings.Server.AbsoluteUri;
                ApiKey.Password = settings.ApiKey;
                Albums.ItemsSource = new[] { settings.Album };
                Albums.SelectedIndex = 0;
                albumServer = settings.Server;
                DownloadFolder.Text = settings.DownloadFolder;
                Interval.Text = settings.IntervalMinutes.ToString();
            }
        }
        catch (Exception ex)
        {
            Log($"Could not load settings. {AlbumSync.DescribeError(ex)}");
        }

        try
        {
            LaunchAtSignIn.IsChecked = settings is null || SettingsStore.StartsWithWindows();
        }
        catch (Exception ex)
        {
            Log($"Could not read Windows startup settings. {AlbumSync.DescribeError(ex)}");
        }

        scheduler.Tick += (_, _) =>
        {
            if (settings is not null && activeSync is null && DateTimeOffset.UtcNow >= nextCheck)
                RequestSync();
        };
        UpdateStatus();
    }

    internal void Start()
    {
        scheduler.Start();
        if (settings is not null)
            RequestSync();
        else
            Log("Enter your server address and API key, then load albums.");
    }

    private void ServerAddress_Changed(object sender, TextChangedEventArgs e)
    {
        if (albumServer is not null
            && (!Uri.TryCreate(ServerAddress.Text.Trim(), UriKind.Absolute, out var current) || current != albumServer))
        {
            albumServer = null;
            Albums.ItemsSource = null;
        }
    }

    private async void LoadAlbums_Click(object sender, RoutedEventArgs e)
    {
        albumLoad = LoadAlbumsAsync();
        await albumLoad;
        albumLoad = null;
    }

    private async Task LoadAlbumsAsync()
    {
        LoadAlbumsButton.IsEnabled = false;
        try
        {
            var server = ImmichClient.ParseServer(ServerAddress.Text);
            string key = ReadKey();
            Guid? selected = (Albums.SelectedItem as Album)?.Id;
            Log("Loading albums from Immich.");
            var albums = await new ImmichClient(server, key).GetAlbumsAsync(lifetime.Token);
            if (exiting || !Uri.TryCreate(ServerAddress.Text.Trim(), UriKind.Absolute, out var current)
                || current != server || ApiKey.Password.Trim() != key)
                return;

            Albums.ItemsSource = albums.OrderBy(album => album.AlbumName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(album => album.Id).ToArray();
            Albums.SelectedItem = albums.FirstOrDefault(album => album.Id == selected);
            albumServer = server;
            Log(albums.Length == 0 ? "Immich returned no available albums." : $"Loaded {albums.Length} albums.");
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex)
        {
            Log($"Could not load albums. {AlbumSync.DescribeError(ex)}");
        }
        finally
        {
            LoadAlbumsButton.IsEnabled = !exiting;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a download folder" };
        if (Directory.Exists(DownloadFolder.Text))
            dialog.InitialDirectory = DownloadFolder.Text;
        if (dialog.ShowDialog(this) == true)
            DownloadFolder.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var server = ImmichClient.ParseServer(ServerAddress.Text);
            string key = ReadKey();
            if (server != albumServer || Albums.SelectedItem is not Album album)
                throw new ArgumentException("Load albums and select the album you want to download.");

            string folder = DownloadFolder.Text.Trim();
            if (!Path.IsPathFullyQualified(folder))
                throw new ArgumentException("Choose an absolute download folder path.");
            folder = Path.GetFullPath(folder);
            if (!int.TryParse(Interval.Text, out int interval) || interval is < 1 or > 10_080)
                throw new ArgumentException("Enter an interval from 1 to 10,080 minutes.");

            var updated = new SyncSettings(server, key, album, folder, interval);
            SettingsStore.Save(updated);
            settings = updated;
            nextCheck = DateTimeOffset.UtcNow.AddMinutes(interval);
            UpdateStatus();
            Log("Settings saved.");
        }
        catch (Exception ex)
        {
            Log($"Could not save settings. {AlbumSync.DescribeError(ex)}");
            return;
        }

        try
        {
            SettingsStore.SetStartup(LaunchAtSignIn.IsChecked == true);
        }
        catch (Exception ex)
        {
            Log($"Could not update Windows startup. {AlbumSync.DescribeError(ex)}");
        }
    }

    private string ReadKey()
    {
        string key = ApiKey.Password.Trim();
        if (key.Length == 0 || key.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Enter a valid Immich API key.");
        return key;
    }

    private void Sync_Click(object sender, RoutedEventArgs e) => RequestSync();

    private async void RequestSync()
    {
        if (exiting || activeSync is not null || settings is not { } current)
            return;

        var progress = new Progress<string>(Log);
        activeSync = Task.Run(() => AlbumSync.RunAsync(current, progress, lifetime.Token), lifetime.Token);
        UpdateStatus();
        try
        {
            await activeSync;
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex)
        {
            Log($"Check failed. {AlbumSync.DescribeError(ex)}");
        }
        finally
        {
            activeSync = null;
            nextCheck = DateTimeOffset.UtcNow.AddMinutes(current.IntervalMinutes);
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        SyncButton.IsEnabled = traySync.Enabled = settings is not null && activeSync is null && !exiting;
        SaveButton.IsEnabled = activeSync is null && !exiting;
        ScheduleStatus.Text = settings is null ? "Save settings to start the schedule"
            : activeSync is not null ? "Checking for new photos…"
            : $"Next check {nextCheck.LocalDateTime:g}";
    }

    private void Log(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        recentLog.Enqueue(line);
        ActivityLog.AppendText(line);
        if (recentLog.Count > 600)
        {
            ActivityLog.Select(0, recentLog.Dequeue().Length);
            ActivityLog.SelectedText = "";
        }
        ActivityLog.ScrollToEnd();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void ShowWindow()
    {
        if (exiting)
            return;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private async void Quit()
    {
        if (exiting)
            return;
        exiting = true;
        scheduler.Stop();
        tray.Visible = false;
        Hide();
        lifetime.Cancel();
        try
        {
            await Task.WhenAll(activeSync ?? Task.CompletedTask, albumLoad ?? Task.CompletedTask);
        }
        catch (Exception)
        {
            // The check and album-loading handlers report their own errors.
        }
        lifetime.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        trayIcon.Dispose();
        base.OnClosed(e);
    }
}
