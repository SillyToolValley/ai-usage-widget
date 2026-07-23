using Microsoft.Win32;

namespace QuotaDeck;

public static class AutostartService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "AI Usage";
    const string LegacyValueName = "QuotaDeck";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string
                || key?.GetValue(LegacyValueName) is string;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        if (enabled && Environment.ProcessPath is string exe)
            key.SetValue(ValueName, $"\"{exe}\"");
        else if (!enabled)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
