using System.Runtime.InteropServices;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace QuotaDeck;

public sealed class TrayIconService : IDisposable
{
    readonly WF.NotifyIcon _icon;
    IntPtr _lastHicon;

    public event Action? ToggleWidget;
    public event Action? RefreshNow;
    public event Action? OpenSettings;
    public event Action? ExitApp;

    public TrayIconService()
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Show/Hide widget", null, (_, _) => ToggleWidget?.Invoke());
        menu.Items.Add("Refresh now", null, (_, _) => RefreshNow?.Invoke());
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings?.Invoke());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp?.Invoke());

        _icon = new WF.NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = menu,
            Text = "AI Usage",
        };
        _icon.DoubleClick += (_, _) => ToggleWidget?.Invoke();
        UpdateIcon(null, null, 70, 90, "AI Usage");
    }

    // Two mini gauges in the tray icon: left = Claude, right = Codex.
    public void UpdateIcon(double? claudePct, double? codexPct, int yellow, int red, string tooltip)
    {
        using var bmp = new SD.Bitmap(16, 16);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.Clear(SD.Color.Transparent);
            DrawBar(g, 1, claudePct, yellow, red);
            DrawBar(g, 9, codexPct, yellow, red);
        }
        var handle = bmp.GetHicon();
        _icon.Icon = SD.Icon.FromHandle(handle);
        if (_lastHicon != IntPtr.Zero) DestroyIcon(_lastHicon);
        _lastHicon = handle;
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    static void DrawBar(SD.Graphics g, int x, double? pct, int yellow, int red)
    {
        using var track = new SD.SolidBrush(SD.Color.FromArgb(90, 255, 255, 255));
        g.FillRectangle(track, x, 1, 6, 14);
        if (pct is null) return;
        var p = Math.Clamp(pct.Value, 0, 100);
        var color = p >= red ? SD.Color.FromArgb(229, 96, 75)
                  : p >= yellow ? SD.Color.FromArgb(229, 184, 75)
                  : SD.Color.FromArgb(63, 182, 139);
        int fill = (int)Math.Round(14 * p / 100.0);
        if (fill <= 0 && p > 0) fill = 1;
        using var brush = new SD.SolidBrush(color);
        g.FillRectangle(brush, x, 1 + (14 - fill), 6, fill);
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        if (_lastHicon != IntPtr.Zero)
        {
            DestroyIcon(_lastHicon);
            _lastHicon = IntPtr.Zero;
        }
    }
}
