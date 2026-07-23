using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;

namespace QuotaDeck;

public partial class App : Application
{
    Mutex? _mutex;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public AppSettings Settings { get; private set; } = new();
    public ServiceUsage? ClaudeUsage { get; private set; }
    public ServiceUsage? CodexUsage { get; private set; }
    public DateTimeOffset? LastRefreshAt { get; private set; }
    public bool Exiting { get; private set; }

    ClaudeProvider? _claude;
    CodexProvider? _codex;
    WidgetWindow? _widget;
    SettingsWindow? _settingsWindow;
    TrayIconService? _tray;
    TaskbarWidget? _taskbarWidget;
    DispatcherTimer? _taskbarTimer;
    DispatcherTimer? _refreshTimer;
    DispatcherTimer? _uiTimer;
    DispatcherTimer? _credDebounce;
    FileSystemWatcher? _claudeWatcher;
    FileSystemWatcher? _codexWatcher;
    bool _refreshing;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "QuotaDeck_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Settings = SettingsService.Load();

        _claude = new ClaudeProvider(_http);
        _codex = new CodexProvider(_http);
        _widget = new WidgetWindow();
        _tray = new TrayIconService();
        _tray.ToggleWidget += ToggleWidget;
        _tray.RefreshNow += () => _ = RefreshAllAsync();
        _tray.OpenSettings += ShowSettings;
        _tray.ExitApp += ExitApp;

        if (Settings.WidgetVisible) _widget.Show();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Math.Max(3, Settings.RefreshMinutes))
        };
        _refreshTimer.Tick += (_, _) => _ = RefreshAllAsync();
        _refreshTimer.Start();

        // Countdown texts tick without refetching.
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _uiTimer.Tick += (_, _) => RenderAll();
        _uiTimer.Start();

        WatchCredentials();
        ApplyTaskbarMode();
        _ = RefreshAllAsync();
    }

    void ApplyTaskbarMode()
    {
        if (Settings.TaskbarMode)
        {
            _taskbarWidget ??= new TaskbarWidget();
            _taskbarWidget.Embed();
            _taskbarWidget.Update(ClaudeUsage, CodexUsage, Settings.YellowThreshold, Settings.RedThreshold);
            if (_taskbarTimer is null)
            {
                // Watchdog: re-embed/reposition after explorer restarts or the
                // tray area changes width.
                _taskbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _taskbarTimer.Tick += (_, _) => _taskbarWidget?.Embed();
            }
            _taskbarTimer.Start();
        }
        else
        {
            _taskbarTimer?.Stop();
            _taskbarWidget?.Dispose();
            _taskbarWidget = null;
        }
    }

    // A completed login rewrites the CLI credential files; pick it up automatically.
    void WatchCredentials()
    {
        _credDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _credDebounce.Tick += (_, _) =>
        {
            _credDebounce!.Stop();
            _ = RefreshAllAsync();
        };
        EnsureWatchers();
    }

    // Retried on every refresh: the ~/.claude or ~/.codex directory may not
    // exist until the user logs in for the first time.
    void EnsureWatchers()
    {
        var claudeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _claudeWatcher ??= TryWatch(claudeDir, ".credentials.json");
        _codexWatcher ??= TryWatch(CodexProvider.HomeDir, "auth.json");
    }

    FileSystemWatcher? TryWatch(string dir, string filter)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var watcher = new FileSystemWatcher(dir, filter);
            FileSystemEventHandler handler = (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _credDebounce!.Stop();
                _credDebounce.Start();
            });
            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _credDebounce!.Stop();
                _credDebounce.Start();
            });
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch { return null; }
    }

    public async Task RefreshAllAsync()
    {
        if (_refreshing || _claude is null || _codex is null) return;
        _refreshing = true;
        EnsureWatchers();
        try
        {
            var claudeTask = _claude.FetchAsync();
            var codexTask = _codex.FetchAsync();
            try { ClaudeUsage = await claudeTask; }
            catch (Exception ex) { ClaudeUsage = new ServiceUsage { Kind = ServiceKind.Claude, Error = ex.Message }; }
            try { CodexUsage = await codexTask; }
            catch (Exception ex) { CodexUsage = new ServiceUsage { Kind = ServiceKind.Codex, Error = ex.Message }; }
            LastRefreshAt = DateTimeOffset.Now;
            RenderAll();
        }
        finally { _refreshing = false; }
    }

    void RenderAll()
    {
        _widget?.Render(ClaudeUsage, CodexUsage);
        UpdateTray();
        _taskbarWidget?.Update(ClaudeUsage, CodexUsage, Settings.YellowThreshold, Settings.RedThreshold);
        if (_settingsWindow?.IsLoaded == true) _settingsWindow.UpdateStatus();
    }

    void UpdateTray()
    {
        if (_tray is null) return;
        var claudePct = ClaudeUsage?.WorstPercent;
        var codexPct = CodexUsage?.WorstPercent;
        var parts = new List<string>();
        if (claudePct is not null) parts.Add($"Claude {claudePct:0}%");
        if (codexPct is not null) parts.Add($"Codex {codexPct:0}%");
        var tooltip = parts.Count > 0 ? string.Join(" · ", parts) : "AI Usage";
        _tray.UpdateIcon(claudePct, codexPct, Settings.YellowThreshold, Settings.RedThreshold, tooltip);
    }

    public void ApplySettings()
    {
        SettingsService.Save(Settings);
        if (_refreshTimer is not null)
            _refreshTimer.Interval = TimeSpan.FromMinutes(Math.Max(3, Settings.RefreshMinutes));
        _widget?.ApplyWindowSettings();
        ApplyTaskbarMode();
        RenderAll();
    }

    public void ToggleWidget()
    {
        if (_widget is null) return;
        if (_widget.IsVisible)
        {
            _widget.Hide();
            Settings.WidgetVisible = false;
        }
        else
        {
            _widget.Show();
            _widget.Activate();
            Settings.WidgetVisible = true;
        }
        SettingsService.Save(Settings);
    }

    public void ShowSettings()
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ExitApp()
    {
        Exiting = true;
        SettingsService.Save(Settings);
        _claudeWatcher?.Dispose();
        _codexWatcher?.Dispose();
        _taskbarWidget?.Dispose();
        _tray?.Dispose();
        _widget?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
