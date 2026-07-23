using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QuotaDeck;

public sealed class ClaudeProvider
{
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    // Current refresh endpoint per Claude Code; legacy domain kept as fallback.
    static readonly string[] TokenUrls =
    {
        "https://platform.claude.com/v1/oauth/token",
        "https://console.anthropic.com/v1/oauth/token",
    };
    const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    const string BetaHeader = "oauth-2025-04-20";
    const long ExpirySkewMs = 30_000;
    const long FallbackExpiryMs = 8L * 3600 * 1000;

    static readonly string ClaudeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    static readonly string CredPath = Path.Combine(ClaudeDir, ".credentials.json");

    // Without a claude-code User-Agent the usage endpoint lands in an
    // aggressively rate-limited bucket and returns persistent 429s.
    static readonly Lazy<string> UserAgent = new(DetectUserAgent);

    readonly HttpClient _http;

    public ClaudeProvider(HttpClient http) => _http = http;

    static string DetectUserAgent()
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (File.Exists(configPath))
            {
                var m = Regex.Match(File.ReadAllText(configPath), "\"lastOnboardingVersion\"\\s*:\\s*\"([0-9][^\"]*)\"");
                if (m.Success) return $"claude-code/{m.Groups[1].Value}";
            }
        }
        catch { }
        return "claude-code/2.1.212";
    }

    public async Task<ServiceUsage> FetchAsync()
    {
        var usage = new ServiceUsage { Kind = ServiceKind.Claude };

        JsonNode? root = ReadJsonWithRetry(CredPath);
        var oauth = root?["claudeAiOauth"];
        var token = oauth?["accessToken"]?.GetValue<string>();
        if (root is null || oauth is null || string.IsNullOrEmpty(token))
        {
            usage.Error = "Not logged in (claude /login)";
            return usage;
        }

        usage.LoggedIn = true;
        usage.Plan = oauth["subscriptionType"]?.GetValue<string>();

        long expiresAt = 0;
        try { expiresAt = oauth["expiresAt"]?.GetValue<long>() ?? 0; } catch { }
        if (expiresAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiresAt - ExpirySkewMs)
        {
            var refreshed = await TryRefreshAsync(root, oauth);
            if (refreshed is null)
            {
                usage.LoggedIn = false;
                usage.Error = "Token expired — log in again";
                return usage;
            }
            token = refreshed;
        }

        var (code, body) = await GetUsageAsync(token!);
        if (code is 401 or 403)
        {
            var refreshed = await TryRefreshAsync(root, oauth);
            if (refreshed is not null)
                (code, body) = await GetUsageAsync(refreshed);
        }

        if (body is null)
        {
            usage.Error = code switch
            {
                0 => "Network error",
                401 or 403 => "Auth failed — log in again",
                429 => "API rate-limited — will retry",
                _ => $"API error ({code})",
            };
            if (code is 401 or 403) usage.LoggedIn = false;
            return usage;
        }

        ParseUsage(body, usage);
        usage.DataTimestamp = DateTimeOffset.Now;
        return usage;
    }

    // The CLI may be mid-write; retry briefly instead of failing the poll.
    static JsonNode? ReadJsonWithRetry(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonNode.Parse(File.ReadAllText(path));
            }
            catch (IOException) { Thread.Sleep(50); }
            catch { return null; }
        }
        return null;
    }

    async Task<string?> TryRefreshAsync(JsonNode root, JsonNode oauth)
    {
        var refreshToken = oauth["refreshToken"]?.GetValue<string>();
        if (string.IsNullOrEmpty(refreshToken)) return null;

        foreach (var url in TokenUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refreshToken,
                        ["client_id"] = ClientId,
                    })
                };
                using var res = await _http.SendAsync(req);
                if (!res.IsSuccessStatusCode) continue;

                var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());
                var access = json?["access_token"]?.GetValue<string>();
                if (string.IsNullOrEmpty(access)) continue;

                oauth["accessToken"] = access;
                // Refresh tokens rotate; failing to persist the new pair would
                // desync Claude Code's own session.
                if (json!["refresh_token"]?.GetValue<string>() is string newRefresh && newRefresh.Length > 0)
                    oauth["refreshToken"] = newRefresh;
                long expiresIn = 0;
                try { expiresIn = json["expires_in"]?.GetValue<long>() ?? 0; } catch { }
                oauth["expiresAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    + (expiresIn > 0 ? expiresIn * 1000 : FallbackExpiryMs);

                // Even if persisting fails, the server has already rotated the
                // pair — the in-memory tokens are the only valid ones, so use them.
                AtomicWrite(CredPath, root);
                return access;
            }
            catch { }
        }
        return null;
    }

    // The CLI may hold the file open; retry transient sharing violations so a
    // rotated refresh token is not silently lost on disk.
    internal static bool AtomicWrite(string path, JsonNode root)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var tmp = path + ".quotadeck.tmp";
                File.WriteAllText(tmp, root.ToJsonString());
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch { break; }
        }
        return false;
    }

    async Task<(int code, JsonNode? body)> GetUsageAsync(string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", BetaHeader);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return ((int)res.StatusCode, null);
            var txt = await res.Content.ReadAsStringAsync();
            return (200, JsonNode.Parse(txt));
        }
        catch { return (0, null); }
    }

    static void ParseUsage(JsonNode body, ServiceUsage usage)
    {
        var limits = body["limits"]?.AsArray();
        if (limits is { Count: > 0 })
        {
            foreach (var entry in limits)
            {
                if (entry is null) continue;
                var kind = entry["kind"]?.GetValue<string>() ?? "";
                var label = kind switch
                {
                    "session" => "Session (5h)",
                    "weekly_all" => "Weekly (all)",
                    "weekly_scoped" => "Weekly · " + (entry["scope"]?["model"]?["display_name"]?.GetValue<string>() ?? "model"),
                    _ => kind,
                };
                var limit = new UsageLimit
                {
                    Label = label,
                    UsedPercent = ReadDouble(entry["percent"]),
                    ResetsAt = ParseIso(entry["resets_at"]?.GetValue<string>()),
                    IsActive = entry["is_active"]?.GetValue<bool>() ?? false,
                    Severity = entry["severity"]?.GetValue<string>(),
                };
                usage.Limits.Add(limit);
                if (limit.UsedPercent >= 100 || limit.Severity is "exceeded" or "rate_limited")
                    usage.RateLimited = true;
            }
        }
        else
        {
            AddWindow(usage, body["five_hour"], "Session (5h)");
            AddWindow(usage, body["seven_day"], "Weekly (all)");
            AddWindow(usage, body["seven_day_opus"], "Weekly · Opus");
            AddWindow(usage, body["seven_day_sonnet"], "Weekly · Sonnet");
        }

        // Extra usage credits (spend beyond plan limits)
        var spend = body["spend"];
        if (spend is not null && spend.GetValueKind() == System.Text.Json.JsonValueKind.Object
            && (spend["enabled"]?.GetValue<bool>() ?? false))
        {
            var row = new UsageLimit
            {
                Label = "Extra credits",
                UsedPercent = ReadDouble(spend["percent"]),
                Severity = spend["severity"]?.GetValue<string>(),
                ExcludeFromSummary = true,
            };
            var used = MoneyText(spend["used"]);
            var cap = MoneyText(spend["limit"]);
            if (used is not null && cap is not null)
            {
                row.Tooltip = $"Extra usage credits {used} / {cap}";
                usage.ExtraNote = $"Credits {used} / {cap}";
            }
            usage.Limits.Add(row);
        }
    }

    static void AddWindow(ServiceUsage usage, JsonNode? node, string label)
    {
        if (node is null || node.GetValueKind() != System.Text.Json.JsonValueKind.Object) return;
        usage.Limits.Add(new UsageLimit
        {
            Label = label,
            UsedPercent = ReadDouble(node["utilization"]),
            ResetsAt = ParseIso(node["resets_at"]?.GetValue<string>()),
        });
    }

    static string? MoneyText(JsonNode? money)
    {
        if (money is null) return null;
        try
        {
            var minor = money["amount_minor"]?.GetValue<long>() ?? -1;
            if (minor < 0) return null;
            var exp = money["exponent"]?.GetValue<int>() ?? 2;
            return "$" + (minor / Math.Pow(10, exp)).ToString("F2");
        }
        catch { return null; }
    }

    static double ReadDouble(JsonNode? node)
    {
        if (node is null) return 0;
        try { return node.GetValue<double>(); }
        catch
        {
            try { return node.GetValue<long>(); } catch { return 0; }
        }
    }

    static DateTimeOffset? ParseIso(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        return DateTimeOffset.TryParse(iso, out var dt) ? dt : null;
    }
}
