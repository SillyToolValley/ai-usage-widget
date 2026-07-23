using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace QuotaDeck;

// Mini usage gauges that sit ON the Windows taskbar, left of the tray area,
// in the spirit of CodeZeno/Claude-Code-Usage-Monitor. Windows 11 renders the
// taskbar via DirectComposition, so true child embedding cannot display —
// instead this is a topmost, non-activating overlay window aligned to the
// taskbar rect, re-asserted by a watchdog (explorer restarts, tray resizes),
// and hidden while a fullscreen app is active.
public sealed class TaskbarWidget : IDisposable
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOACTIVATE = 0x0010;

    const double WidthDip = 206;
    const double HeightDip = 40;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowW(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int value);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate proc,
        uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject,
        int idChild, uint thread, uint time);
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, Rt, B; }

    static SolidColorBrush Rgb(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    static readonly Brush PanelBrush = Rgb(0x15, 0x18, 0x20, 0xF2);
    static readonly Brush PanelBorder = Rgb(0xFF, 0xFF, 0xFF, 0x2E);
    static readonly Brush TextBrush = Rgb(0xC9, 0xCE, 0xD6);
    static readonly Brush TrackBrush = Rgb(0x2A, 0x2F, 0x38);
    static readonly Brush OkBrush = Rgb(0x3F, 0xB6, 0x8B);
    static readonly Brush WarnBrush = Rgb(0xE5, 0xB8, 0x4B);
    static readonly Brush CritBrush = Rgb(0xE5, 0x60, 0x4B);
    static readonly Brush ClaudeAccent = Rgb(0xD9, 0x77, 0x57);
    static readonly Brush CodexAccent = Rgb(0x10, 0xA3, 0x7F);

    const double BarW = 30;

    Window? _window;
    Border? _rootView;
    WinEventDelegate? _winEventProc; // kept referenced so the GC can't collect it
    IntPtr _winEventHook;
    System.Windows.Threading.DispatcherTimer? _reassertDelay;
    // [row, col]: rows = Claude/Codex, cols = Session(5h)/Weekly — same kind
    // of window always shares a column so the bars line up vertically.
    readonly (Border track, Border fill, TextBlock pct)[,] _cells
        = new (Border, Border, TextBlock)[2, 2];

    public bool IsEmbedded => _window is not null;

    // Aligns the overlay with the taskbar (left of the tray notification area)
    // and re-asserts topmost. Safe to call repeatedly; hides during fullscreen.
    public void Embed()
    {
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return;

        if (_window is null)
        {
            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Width = WidthDip,
                Height = HeightDip,
                Content = _rootView ??= BuildView(),
            };
            _window.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(_window).Handle;
                SetWindowLong(h, GWL_EXSTYLE,
                    GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            };
            _window.Show();

            // Clicking the taskbar raises it above other topmost windows and
            // covers this overlay; re-assert on every foreground change (with a
            // short second pass, since the z-change can land after the event).
            _reassertDelay = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _reassertDelay.Tick += (_, _) =>
            {
                _reassertDelay!.Stop();
                Reassert();
            };
            _winEventProc = (_, _, _, _, _, _, _) =>
            {
                Reassert();
                _reassertDelay.Stop();
                _reassertDelay.Start();
            };
            _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _winEventProc, 0, 0, 0);
        }

        // Hide over fullscreen apps (games, videos, presentations).
        SHQueryUserNotificationState(out int quns);
        bool fullscreen = quns is 2 or 3 or 4;
        if (fullscreen)
        {
            _window.Hide();
            return;
        }
        if (!_window.IsVisible) _window.Show();

        GetWindowRect(taskbar, out var tb);
        var tray = FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        int trayLeft = tb.Rt;
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var tn))
            trayLeft = tn.L;

        double scale = GetDpiForWindow(taskbar) / 96.0;
        int taskbarH = tb.B - tb.T;
        int wPx = (int)(WidthDip * scale);
        int hPx = Math.Min((int)(HeightDip * scale), taskbarH - (int)(4 * scale));
        int x = trayLeft - wPx - (int)(8 * scale);
        int y = tb.T + (taskbarH - hPx) / 2;

        var hwnd = new WindowInteropHelper(_window).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, wPx, hPx, SWP_NOACTIVATE);
    }

    // Cheap visibility/z-order re-assert used by the foreground-change hook.
    void Reassert()
    {
        if (_window is null) return;
        SHQueryUserNotificationState(out int quns);
        if (quns is 2 or 3 or 4)
        {
            _window.Hide();
            return;
        }
        if (!_window.IsVisible) _window.Show();
        var hwnd = new WindowInteropHelper(_window).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    Border BuildView()
    {
        var grid = new Grid { Margin = new Thickness(8, 3, 8, 3) };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Session (5h)
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Weekly

        AddNameCell(grid, 0, ClaudeAccent, "Claude");
        AddNameCell(grid, 1, CodexAccent, "Codex");
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < 2; col++)
                _cells[row, col] = AddBarCell(grid, row, col + 1);

        var root = new Border
        {
            Background = PanelBrush,
            BorderBrush = PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid,
            ToolTip = "AI Usage",
            Cursor = Cursors.Hand,
        };
        root.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            App.Current.ToggleWidget();
        };
        return root;
    }

    static void AddNameCell(Grid grid, int row, Brush accent, string name)
    {
        var cell = new StackPanel { Orientation = Orientation.Horizontal };
        cell.Children.Add(new Ellipse
        {
            Width = 5, Height = 5, Fill = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        });
        cell.Children.Add(new TextBlock
        {
            Text = name, FontSize = 10, Foreground = TextBrush, Width = 38,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, 0);
        grid.Children.Add(cell);
    }

    static (Border track, Border fill, TextBlock pct) AddBarCell(Grid grid, int row, int col)
    {
        var cell = new StackPanel { Orientation = Orientation.Horizontal };
        var track = new Border
        {
            Width = BarW, Height = 4, CornerRadius = new CornerRadius(2),
            Background = TrackBrush, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(col == 1 ? 0 : 7, 0, 3, 0),
        };
        var fill = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2), Width = 0,
            Background = OkBrush, HorizontalAlignment = HorizontalAlignment.Left,
        };
        track.Child = fill;
        var pct = new TextBlock
        {
            Text = "–", FontSize = 9.5, Foreground = TextBrush, Width = 25,
            VerticalAlignment = VerticalAlignment.Center,
        };
        cell.Children.Add(track);
        cell.Children.Add(pct);
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, col);
        grid.Children.Add(cell);
        return (track, fill, pct);
    }

    // Column 0 = Session (5h), column 1 = Weekly, matched by limit label so
    // the same kind of quota always lines up vertically across services.
    static (UsageLimit? session, UsageLimit? weekly) Classify(ServiceUsage? usage)
    {
        if (usage is null) return (null, null);
        var limits = usage.Limits.Where(l => !l.ExcludeFromSummary).ToList();
        var session = limits.FirstOrDefault(l =>
            l.Label.StartsWith("Session", StringComparison.OrdinalIgnoreCase));
        var weekly = limits.FirstOrDefault(l =>
            l.Label.StartsWith("Weekly", StringComparison.OrdinalIgnoreCase));
        if (session is null && weekly is null && limits.Count > 0)
            weekly = limits[0];
        return (session, weekly);
    }

    public void Update(ServiceUsage? claude, ServiceUsage? codex, int yellow, int red)
    {
        if (_rootView is null) return;

        var (claudeSession, claudeWeekly) = Classify(claude);
        var (codexSession, codexWeekly) = Classify(codex);
        SetCell(_cells[0, 0], claudeSession, yellow, red);
        SetCell(_cells[0, 1], claudeWeekly, yellow, red);
        SetCell(_cells[1, 0], codexSession, yellow, red);
        SetCell(_cells[1, 1], codexWeekly, yellow, red);

        var lines = new List<string>();
        foreach (var (svc, limit) in new[]
            { ("Claude", claudeSession), ("Claude", claudeWeekly), ("Codex", codexSession), ("Codex", codexWeekly) })
            if (limit is not null)
                lines.Add($"{svc} {limit.Label}: {Math.Round(limit.UsedPercent)}% used");
        _rootView.ToolTip = lines.Count > 0 ? string.Join("\n", lines) : "AI Usage";
    }

    static void SetCell((Border track, Border fill, TextBlock pct) cell, UsageLimit? limit, int yellow, int red)
    {
        if (limit is null)
        {
            cell.track.Visibility = Visibility.Hidden;
            cell.fill.Width = 0;
            cell.pct.Text = "";
            return;
        }
        cell.track.Visibility = Visibility.Visible;
        double used = Math.Clamp(limit.UsedPercent, 0, 100);
        var color = used >= red ? CritBrush : used >= yellow ? WarnBrush : OkBrush;
        double w = BarW * used / 100.0;
        cell.fill.Width = used > 0 && w < 2 ? 2 : w;
        cell.fill.Background = color;
        cell.pct.Text = $"{Math.Round(used)}%";
        cell.pct.Foreground = color;
    }

    public void Dispose()
    {
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _reassertDelay?.Stop();
        _window?.Close();
        _window = null;
    }
}
