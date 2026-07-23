namespace QuotaDeck;

public enum ServiceKind { Claude, Codex }

public sealed class UsageLimit
{
    public string Label { get; set; } = "";
    public double UsedPercent { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public bool IsActive { get; set; }
    public string? Severity { get; set; }
    public string? Tooltip { get; set; }
    // Informational rows (e.g. extra-usage credits) that should not drive
    // the tray gauge / worst-percent summary.
    public bool ExcludeFromSummary { get; set; }
}

public sealed class ServiceUsage
{
    public ServiceKind Kind { get; set; }
    public bool LoggedIn { get; set; }
    public string? Plan { get; set; }
    public string? Error { get; set; }
    public List<UsageLimit> Limits { get; } = new();
    public string? ExtraNote { get; set; }
    public DateTimeOffset? DataTimestamp { get; set; }
    public bool RateLimited { get; set; }

    public double? WorstPercent
    {
        get
        {
            double? worst = null;
            foreach (var l in Limits)
            {
                if (l.ExcludeFromSummary) continue;
                if (worst is null || l.UsedPercent > worst) worst = l.UsedPercent;
            }
            return worst;
        }
    }
}
