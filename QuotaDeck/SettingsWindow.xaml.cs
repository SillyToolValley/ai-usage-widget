using System.Diagnostics;
using System.Windows;
using System.Windows.Data;

namespace QuotaDeck;

public partial class SettingsWindow : Window
{
    static readonly int[] RefreshOptions = { 3, 5, 10, 15, 30 };
    readonly List<ThemeChoice> _themeChoices =
    [
        new("default", "Default", OverlayTheme.GeneralCategory),
    ];

    public SettingsWindow()
    {
        InitializeComponent();
        foreach (var m in RefreshOptions)
            RefreshCombo.Items.Add($"{m} min");
        foreach (var t in OverlayTheme.All.OrderBy(theme => CategoryRank(theme.Category)))
            _themeChoices.Add(new(t.Id, t.DisplayName, t.Category));
        var themeView = CollectionViewSource.GetDefaultView(_themeChoices);
        themeView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(ThemeChoice.Category)));
        ThemeCombo.DisplayMemberPath = nameof(ThemeChoice.DisplayName);
        ThemeCombo.SelectedValuePath = nameof(ThemeChoice.Id);
        ThemeCombo.ItemsSource = themeView;
        LoadValues();

        OpacitySlider.ValueChanged += (_, _) => OpacityLabel.Text = $"{OpacitySlider.Value * 100:0}%";
        YellowSlider.ValueChanged += (_, _) => YellowLabel.Text = $"{YellowSlider.Value:0}%";
        RedSlider.ValueChanged += (_, _) => RedLabel.Text = $"{RedSlider.Value:0}%";
        ApplyBtn.Click += (_, _) => Apply();
        CloseBtn.Click += (_, _) => Close();
        // Login runs in a visible terminal via the official CLIs; the file
        // watchers pick up the new credentials automatically.
        ClaudeLoginBtn.Click += (_, _) => LaunchTerminal(
            "title Claude login && echo Type /login once Claude starts. && claude");
        CodexLoginBtn.Click += (_, _) => LaunchTerminal(
            "title Codex login && codex login");
        UpdateStatus();
    }

    void LoadValues()
    {
        var s = App.Current.Settings;
        RemainChk.IsChecked = s.ShowRemaining;
        HorizontalChk.IsChecked = s.Horizontal;
        TopmostChk.IsChecked = s.Topmost;
        var idx = Array.IndexOf(RefreshOptions, s.RefreshMinutes);
        RefreshCombo.SelectedIndex = idx >= 0 ? idx : 1;
        ThemeCombo.SelectedValue = _themeChoices.Any(theme => theme.Id == s.Theme)
            ? s.Theme
            : "default";
        OpacitySlider.Value = s.WidgetOpacity;
        YellowSlider.Value = s.YellowThreshold;
        RedSlider.Value = s.RedThreshold;
        AutostartChk.IsChecked = AutostartService.IsEnabled();
        TaskbarChk.IsChecked = s.TaskbarMode;
        OpacityLabel.Text = $"{s.WidgetOpacity * 100:0}%";
        YellowLabel.Text = $"{s.YellowThreshold}%";
        RedLabel.Text = $"{s.RedThreshold}%";
    }

    void Apply()
    {
        var s = App.Current.Settings;
        s.ShowRemaining = RemainChk.IsChecked == true;
        s.Horizontal = HorizontalChk.IsChecked == true;
        s.Topmost = TopmostChk.IsChecked == true;
        s.RefreshMinutes = RefreshOptions[Math.Max(0, RefreshCombo.SelectedIndex)];
        s.Theme = (ThemeCombo.SelectedItem as ThemeChoice)?.Id ?? "default";
        s.WidgetOpacity = OpacitySlider.Value;
        s.YellowThreshold = (int)YellowSlider.Value;
        s.RedThreshold = Math.Max((int)RedSlider.Value, (int)YellowSlider.Value);
        try { AutostartService.SetEnabled(AutostartChk.IsChecked == true); } catch { }
        s.TaskbarMode = TaskbarChk.IsChecked == true;
        App.Current.ApplySettings();
        UpdateStatus();
    }

    public void UpdateStatus()
    {
        ClaudeStatusText.Text = "Claude: " + StatusLine(App.Current.ClaudeUsage);
        CodexStatusText.Text = "Codex: " + StatusLine(App.Current.CodexUsage);
    }

    static string StatusLine(ServiceUsage? u)
    {
        if (u is null) return "Checking...";
        if (!u.LoggedIn) return u.Error ?? "Logged out";
        var line = u.Plan is { Length: > 0 } plan ? $"Logged in ({plan})" : "Logged in";
        if (u.Error is { Length: > 0 } err) line += $" — {err}";
        return line;
    }

    static void LaunchTerminal(string command)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k {command}")
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    static int CategoryRank(string category) => category switch
    {
        OverlayTheme.VocaloidCategory => 1,
        OverlayTheme.VioletEvergardenCategory => 2,
        OverlayTheme.SpyFamilyCategory => 3,
        OverlayTheme.GintamaCategory => 4,
        OverlayTheme.UmamusumeCategory => 5,
        _ => 6,
    };

    public sealed record ThemeChoice(string Id, string DisplayName, string Category);
}
