using System.IO;
using System.Text.Json;

namespace QuotaDeck;

public sealed class AppSettings
{
    public int RefreshMinutes { get; set; } = 3;
    public string Theme { get; set; } = "default";
    public bool ShowRemaining { get; set; }
    public bool Horizontal { get; set; }
    public int YellowThreshold { get; set; } = 70;
    public int RedThreshold { get; set; } = 90;
    public double WidgetOpacity { get; set; } = 0.97;
    public bool Topmost { get; set; } = true;
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public bool WidgetVisible { get; set; } = true;
    public bool TaskbarMode { get; set; }
}

public static class SettingsService
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuotaDeck");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Opts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }
}
