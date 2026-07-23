using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace QuotaDeck;

public partial class WidgetWindow : Window
{
    static SolidColorBrush Rgb(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    static readonly Brush TextBrush = Rgb(0xE8, 0xEA, 0xED);
    static readonly Brush DimBrush = Rgb(0x9A, 0xA3, 0xAE);
    static readonly Brush FaintBrush = Rgb(0x6B, 0x72, 0x7C);
    static readonly Brush TrackBrush = Rgb(0x2A, 0x2F, 0x38);
    static readonly Brush OkBrush = Rgb(0x3F, 0xB6, 0x8B);
    static readonly Brush WarnBrush = Rgb(0xE5, 0xB8, 0x4B);
    static readonly Brush CritBrush = Rgb(0xE5, 0x60, 0x4B);
    static readonly Brush ClaudeAccent = Rgb(0xD9, 0x77, 0x57);
    static readonly Brush CodexAccent = Rgb(0x10, 0xA3, 0x7F);
    static readonly Brush ChipBg = Rgb(0xFF, 0xFF, 0xFF, 0x22);
    static readonly Brush DefaultBg = Rgb(0x0F, 0x12, 0x18);
    static readonly Brush DefaultBorderBrush = Rgb(0xFF, 0xFF, 0xFF, 0x26);

    const double BarWidth = 70;

    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    ServiceUsage? _claude, _codex;
    bool _anchored;
    DispatcherTimer? _animTimer;
    int _frameIndex;
    OverlayTheme? _activeTheme;
    string? _skinThemeId;
    DockPanel? _headerPanel;
    double _headerSafeSideInset;
    double _headerSafeTopInset;

    public WidgetWindow()
    {
        InitializeComponent();
        _anchored = App.Current.Settings.WindowX is null;
        MouseLeftButtonDown += OnDrag;
        ContextMenu = BuildContextMenu();
        Loaded += (_, _) => RestorePosition();
        // Keep the themed overlay and panel skin exactly matching the content size.
        RootBorder.SizeChanged += (_, _) => SyncThemeLayout();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _animTimer?.Stop();
            else if (OverlayTheme.Find(App.Current.Settings.Theme) is not null) _animTimer?.Start();
        };
        ApplyWindowSettings();
        Render(null, null);
    }

    public void ApplyWindowSettings()
    {
        var s = App.Current.Settings;
        Topmost = s.Topmost;
        Opacity = s.WidgetOpacity;
        ApplyTheme(s.Theme);
    }

    void ApplyTheme(string theme)
    {
        var overlay = OverlayTheme.Find(theme);
        if (overlay is not null)
        {
            try
            {
                if (_skinThemeId != overlay.Id)
                {
                    BuildPanelSkin(overlay);
                    _skinThemeId = overlay.Id;
                    _frameIndex = 0;
                    OverlayImage.Source = overlay.Frames[0];
                }
                _activeTheme = overlay;
                OverlayImage.Visibility = Visibility.Visible;
                PanelSkin.Visibility = Visibility.Visible;
                // Provisional sizes so no bitmap ever renders at natural size;
                // corrected by SyncThemeLayout as soon as layout has run.
                OverlayImage.Width = 280;
                PanelSkin.Width = 280;
                PanelSkin.Height = 100;
                SyncThemeLayout();
                RootBorder.Background = Brushes.Transparent;
                RootBorder.BorderBrush = Brushes.Transparent;
                StartAnimation(overlay);
                return;
            }
            catch { } // asset failure -> default look
        }
        _activeTheme = null;
        SetHeaderSafeInsets(0, 0);
        OverlayImage.Visibility = Visibility.Collapsed;
        PanelSkin.Visibility = Visibility.Collapsed;
        RootBorder.Background = DefaultBg;
        RootBorder.BorderBrush = DefaultBorderBrush;
        _animTimer?.Stop();
    }

    void SyncThemeLayout()
    {
        if (OverlayImage.Visibility != Visibility.Visible) return;
        if (RootBorder.ActualWidth <= 0 || RootBorder.ActualHeight <= 0) return;
        OverlayImage.Width = RootBorder.ActualWidth;
        PanelSkin.Width = RootBorder.ActualWidth;
        PanelSkin.Height = RootBorder.ActualHeight;

        // Tuck the overlay's bottom under/over the panel by the pack's
        // declared overlap so railings sit exactly on the panel's top band.
        if (_activeTheme is not null)
        {
            double overlap = Math.Max(1,
                _activeTheme.OverlayOverlapPx * (RootBorder.ActualWidth / _activeTheme.PanelPixelWidth));
            OverlayImage.Margin = new Thickness(0, 0, 0, -overlap);
        }

        // Display the fixed 9-slice corners at the panel's horizontal scale so
        // corner ornaments stay proportional to the frame edges.
        if (_activeTheme is not null && PanelSkin.ColumnDefinitions.Count == 3)
        {
            double corner = _activeTheme.PanelCornerPx
                * (RootBorder.ActualWidth / _activeTheme.PanelPixelWidth);
            corner = Math.Min(corner, Math.Min(PanelSkin.Width, PanelSkin.Height) / 3);
            PanelSkin.ColumnDefinitions[0].Width = new GridLength(corner);
            PanelSkin.ColumnDefinitions[2].Width = new GridLength(corner);
            PanelSkin.RowDefinitions[0].Height = new GridLength(corner);
            PanelSkin.RowDefinitions[2].Height = new GridLength(corner);

            // Header endpoints are the only content that reaches into the top
            // corners. Scale their pack-declared clearance with the actual
            // displayed corner (including the height clamp), so ornate themes
            // stay safe without making the entire widget wider.
            double headerInset = _activeTheme.PanelCornerPx > 0
                ? _activeTheme.HeaderSafeSideInsetPx * (corner / _activeTheme.PanelCornerPx)
                : 0;
            double headerTopInset = _activeTheme.PanelCornerPx > 0
                ? _activeTheme.HeaderSafeTopInsetPx * (corner / _activeTheme.PanelCornerPx)
                : 0;
            SetHeaderSafeInsets(headerInset, headerTopInset);
        }
    }

    void SetHeaderSafeInsets(double sideInset, double topInset)
    {
        sideInset = Math.Max(0, sideInset);
        topInset = Math.Max(0, topInset);
        _headerSafeSideInset = sideInset;
        _headerSafeTopInset = topInset;
        if (_headerPanel is null) return;
        if (Math.Abs(_headerPanel.Margin.Left - sideInset) < 0.1 &&
            Math.Abs(_headerPanel.Margin.Top - topInset) < 0.1 &&
            Math.Abs(_headerPanel.Margin.Right - sideInset) < 0.1) return;
        _headerPanel.Margin = new Thickness(sideInset, topInset, sideInset, 0);
    }

    void BuildPanelSkin(OverlayTheme theme)
    {
        PanelSkin.Children.Clear();
        PanelSkin.ColumnDefinitions.Clear();
        PanelSkin.RowDefinitions.Clear();

        var slices = theme.PanelSlices;
        const double corner = 15; // on-screen size of the fixed frame corners
        // Sub-pixel cell boundaries leave hairline seams between the slices;
        // round the layout and let inner pieces tuck 1px under their neighbors.
        PanelSkin.UseLayoutRounding = true;
        for (int i = 0; i < 3; i++)
        {
            PanelSkin.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(corner)
            });
            PanelSkin.RowDefinitions.Add(new RowDefinition
            {
                Height = i == 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(corner)
            });
        }

        void Add(int r, int c, Thickness overlap)
        {
            var img = new Image
            {
                Source = slices[r, c],
                Stretch = Stretch.Fill,
                Margin = overlap,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Grid.SetRow(img, r);
            Grid.SetColumn(img, c);
            PanelSkin.Children.Add(img);
        }

        // Draw order: center under edges under corners, each extending 1px
        // beneath the piece above it so no gap can show through.
        Add(1, 1, new Thickness(-1));
        Add(0, 1, new Thickness(-1, 0, -1, -1));
        Add(2, 1, new Thickness(-1, -1, -1, 0));
        Add(1, 0, new Thickness(0, -1, -1, -1));
        Add(1, 2, new Thickness(-1, -1, 0, -1));
        Add(0, 0, default);
        Add(0, 2, default);
        Add(2, 0, default);
        Add(2, 2, default);
    }

    static readonly Random AnimRng = new();

    void StartAnimation(OverlayTheme theme)
    {
        if (_animTimer is null)
        {
            _animTimer = new DispatcherTimer();
            _animTimer.Tick += (_, _) =>
            {
                if (_activeTheme is null) return;
                _frameIndex = (_frameIndex + 1) % _activeTheme.Frames.Count;
                OverlayImage.Source = _activeTheme.Frames[_frameIndex];

                // Organic timing instead of a metronome: jitter every frame a
                // little, and occasionally rest at the loop start so the blink
                // cycle never repeats on an exact period.
                double next = _activeTheme.FrameDurationMs * (0.75 + AnimRng.NextDouble() * 0.7);
                if (_frameIndex == 0 && AnimRng.NextDouble() < 0.35)
                    next += 400 + AnimRng.NextDouble() * 1200;
                _animTimer!.Interval = TimeSpan.FromMilliseconds(next);
            };
        }
        _animTimer.Interval = TimeSpan.FromMilliseconds(theme.FrameDurationMs);
        if (IsVisible || !IsLoaded) _animTimer.Start();
    }

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        double beforeLeft = Left, beforeTop = Top;
        try { DragMove(); } catch { return; }
        // A click without movement should not un-dock the widget.
        if (Math.Abs(Left - beforeLeft) < 1 && Math.Abs(Top - beforeTop) < 1) return;
        _anchored = false;
        var s = App.Current.Settings;
        s.WindowX = Left;
        s.WindowY = Top;
        SettingsService.Save(s);
    }

    void RestorePosition()
    {
        var s = App.Current.Settings;
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        if (s.WindowX is double x && s.WindowY is double y &&
            x > left - 50 && x < right - 40 && y > top - 20 && y < bottom - 40)
        {
            Left = x;
            Top = y;
        }
        else
        {
            Reanchor();
        }
    }

    void Reanchor()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 24;
        Top = wa.Bottom - ActualHeight - 24;
    }

    public void Render(ServiceUsage? claude, ServiceUsage? codex)
    {
        _claude = claude;
        _codex = codex;
        var s = App.Current.Settings;

        RootPanel.Children.Clear();
        RootPanel.Children.Add(BuildHeader(s));

        var services = new StackPanel
        {
            Orientation = s.Horizontal ? Orientation.Horizontal : Orientation.Vertical
        };
        services.Children.Add(BuildCard(_claude, "Claude", ClaudeAccent,
            "https://claude.ai/settings/usage", s, first: true));
        services.Children.Add(BuildCard(_codex, "Codex", CodexAccent,
            "https://chatgpt.com/codex/cloud/settings/analytics#usage", s, first: false));
        RootPanel.Children.Add(services);

        if (_anchored && IsLoaded)
            Dispatcher.BeginInvoke(new Action(Reanchor), DispatcherPriority.Loaded);
    }

    UIElement BuildHeader(AppSettings s)
    {
        var dock = new DockPanel
        {
            LastChildFill = false,
            Margin = new Thickness(
                _headerSafeSideInset, _headerSafeTopInset, _headerSafeSideInset, 0),
        };
        _headerPanel = dock;

        var title = Text("AI Usage", 11, DimBrush, FontWeights.SemiBold);
        DockPanel.SetDock(title, Dock.Left);
        dock.Children.Add(title);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        // Non-default "% left" mode is highlighted so an accidental toggle
        // can't silently make the numbers look wrong versus official pages.
        var modeChip = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = s.ShowRemaining ? WarnBrush : Rgb(0xFF, 0xFF, 0xFF, 0x30),
            Padding = new Thickness(6, 0, 6, 1),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Click to toggle: showing % used ↔ % left",
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(s.ShowRemaining ? "LEFT %" : "USED %", 9,
                s.ShowRemaining ? WarnBrush : DimBrush, FontWeights.SemiBold),
        };
        modeChip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            App.Current.Settings.ShowRemaining = !App.Current.Settings.ShowRemaining;
            App.Current.ApplySettings();
        };
        right.Children.Add(modeChip);

        var updated = App.Current.LastRefreshAt is DateTimeOffset t
            ? Text($"{t.LocalDateTime:HH:mm}", 10, FaintBrush)
            : Text("…", 10, FaintBrush);
        updated.ToolTip = "Last refresh";
        right.Children.Add(updated);

        var minimize = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Rgb(0xFF, 0xFF, 0xFF, 0x30),
            Padding = new Thickness(7, 0, 7, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Minimize to tray (double-click the tray icon to restore)",
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Child = Text("—", 10, DimBrush, FontWeights.SemiBold),
        };
        minimize.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            App.Current.ToggleWidget();
        };
        right.Children.Add(minimize);

        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        return dock;
    }

    UIElement BuildCard(ServiceUsage? u, string name, Brush accent, string dashboardUrl,
        AppSettings s, bool first)
    {
        var panel = new StackPanel
        {
            Margin = s.Horizontal
                ? new Thickness(first ? 0 : 24, 10, 0, 0)
                : new Thickness(0, first ? 10 : 16, 0, 0),
            MinWidth = 240,
        };
        // Rows share one auto-sized label column so long labels never truncate.
        Grid.SetIsSharedSizeScope(panel, true);

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Ellipse
        {
            Width = 8, Height = 8, Fill = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 7, 0),
        });
        var title = Text(name, 13, TextBrush, FontWeights.SemiBold);
        title.Cursor = Cursors.Hand;
        title.ToolTip = "Open the official usage page";
        title.MouseLeftButtonDown += (_, e) => { e.Handled = true; OpenUrl(dashboardUrl); };
        head.Children.Add(title);
        if (u?.Plan is { Length: > 0 } plan)
            head.Children.Add(Chip(plan.ToUpperInvariant(), ChipBg, DimBrush));
        if (u?.RateLimited == true)
            head.Children.Add(Chip("LIMIT", CritBrush, TextBrush));
        panel.Children.Add(head);

        if (u is null)
        {
            panel.Children.Add(Note("Loading..."));
            return panel;
        }
        if (u.Limits.Count == 0)
        {
            panel.Children.Add(Note(u.Error ?? (u.LoggedIn ? "No data" : "Login required"), WarnBrush));
            return panel;
        }

        foreach (var limit in u.Limits)
            panel.Children.Add(BuildLimitRow(limit, s));

        if (u.ExtraNote is { Length: > 0 } note)
            panel.Children.Add(Note(note));
        if (u.DataTimestamp is DateTimeOffset ts &&
            DateTimeOffset.Now - ts > TimeSpan.FromMinutes(10))
            panel.Children.Add(Note($"from last session ({ts.LocalDateTime.ToString("MMM d HH:mm", En)})"));

        return panel;
    }

    UIElement BuildLimitRow(UsageLimit l, AppSettings s)
    {
        double used = Math.Clamp(l.UsedPercent, 0, 100);
        double shown = s.ShowRemaining ? 100 - used : used;
        var color = used >= s.RedThreshold ? CritBrush
                  : used >= s.YellowThreshold ? WarnBrush
                  : OkBrush;

        var grid = new Grid { Margin = new Thickness(15, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
            SharedSizeGroup = "LimitLabel",
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarWidth + 8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = Text(l.Label, 11, DimBrush);
        label.MinWidth = 62;
        label.Margin = new Thickness(0, 0, 10, 0);
        if (l.Tooltip is not null) label.ToolTip = l.Tooltip;
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var track = new Border
        {
            Width = BarWidth, Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = TrackBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var fillWidth = BarWidth * shown / 100.0;
        if (shown > 0 && fillWidth < 2) fillWidth = 2;
        track.Child = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = color,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = fillWidth,
        };
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);

        var pctText = Text($"{Math.Round(shown)}%", 11, color, FontWeights.SemiBold);
        pctText.HorizontalAlignment = HorizontalAlignment.Right;
        pctText.Margin = new Thickness(0, 0, 10, 0);
        pctText.ToolTip = s.ShowRemaining ? $"Used {Math.Round(used)}%" : $"{Math.Round(100 - used)}% left";
        Grid.SetColumn(pctText, 2);
        grid.Children.Add(pctText);

        var reset = Text(ResetText(l.ResetsAt), 10, FaintBrush);
        if (l.ResetsAt is DateTimeOffset abs)
            reset.ToolTip = $"Resets {abs.LocalDateTime.ToString("ddd, MMM d HH:mm", En)}";
        Grid.SetColumn(reset, 3);
        grid.Children.Add(reset);

        return grid;
    }

    static string ResetText(DateTimeOffset? t)
    {
        if (t is null) return "";
        var d = t.Value - DateTimeOffset.Now;
        if (d <= TimeSpan.Zero) return "resetting…";
        if (d.TotalDays >= 1) return $"in {(int)d.TotalDays}d {d.Hours}h";
        if (d.TotalHours >= 1) return $"in {(int)d.TotalHours}h {d.Minutes}m";
        return $"in {Math.Max(1, d.Minutes)}m";
    }

    ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += (_, _) => _ = App.Current.RefreshAllAsync();

        var remaining = new MenuItem { Header = "Show remaining", IsCheckable = true };
        remaining.Click += (_, _) =>
        {
            App.Current.Settings.ShowRemaining = remaining.IsChecked;
            App.Current.ApplySettings();
        };

        var horizontal = new MenuItem { Header = "Horizontal layout", IsCheckable = true };
        horizontal.Click += (_, _) =>
        {
            App.Current.Settings.Horizontal = horizontal.IsChecked;
            App.Current.ApplySettings();
        };

        var settings = new MenuItem { Header = "Settings..." };
        settings.Click += (_, _) => App.Current.ShowSettings();

        var hide = new MenuItem { Header = "Hide to tray" };
        hide.Click += (_, _) => App.Current.ToggleWidget();

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => App.Current.ExitApp();

        menu.Items.Add(refresh);
        menu.Items.Add(remaining);
        menu.Items.Add(horizontal);
        menu.Items.Add(new Separator());
        menu.Items.Add(settings);
        menu.Items.Add(hide);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        menu.Opened += (_, _) =>
        {
            remaining.IsChecked = App.Current.Settings.ShowRemaining;
            horizontal.IsChecked = App.Current.Settings.Horizontal;
        };
        return menu;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.Current.Exiting)
        {
            e.Cancel = true;
            App.Current.ToggleWidget();
        }
        base.OnClosing(e);
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    static TextBlock Text(string s, double size, Brush brush, FontWeight? weight = null)
    {
        var tb = new TextBlock
        {
            Text = s,
            FontSize = size,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (weight is FontWeight w) tb.FontWeight = w;
        return tb;
    }

    static Border Chip(string text, Brush bg, Brush fg)
    {
        return new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(text, 9, fg, FontWeights.SemiBold),
        };
    }

    static TextBlock Note(string text, Brush? brush = null)
    {
        var tb = Text(text, 10.5, brush ?? FaintBrush);
        tb.Margin = new Thickness(15, 6, 0, 0);
        tb.TextWrapping = TextWrapping.Wrap;
        tb.MaxWidth = 240;
        return tb;
    }
}
