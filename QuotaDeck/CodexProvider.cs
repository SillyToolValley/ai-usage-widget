using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaDeck;

// Codex usage, two sources:
//  1) Live: GET https://chatgpt.com/backend-api/wham/usage with the CLI's OAuth
//     tokens from ~/.codex/auth.json (401 -> refresh via auth.openai.com, write back).
//  2) Offline fallback: rate_limits snapshots the CLI writes into
//     ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl token_count events.
public sealed class CodexProvider
{
    const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    const string TokenUrl = "https://auth.openai.com/oauth/token";
    const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    static readonly string CodexDir =
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    static readonly string AuthPath = Path.Combine(CodexDir, "auth.json");
    static readonly string SessionsDir = Path.Combine(CodexDir, "sessions");

    internal static string HomeDir => CodexDir;

    readonly HttpClient _http;

    public CodexProvider(HttpClient http) => _http = http;

    public async Task<ServiceUsage> FetchAsync()
    {
        var usage = new ServiceUsage { Kind = ServiceKind.Codex };

        JsonNode? auth = ReadJsonWithRetry(AuthPath);
        var accessToken = auth?["tokens"]?["access_token"]?.GetValue<string>();
        if (auth is null || string.IsNullOrEmpty(accessToken))
        {
            // API-key-only auth has no ChatGPT quota to show.
            if (auth?["OPENAI_API_KEY"]?.GetValue<string>() is { Length: > 0 })
            {
                usage.LoggedIn = true;
                usage.Error = "API-key mode — no quota data";
            }
            else
            {
                usage.Error = "Not logged in (codex login)";
            }
            return usage;
        }

        usage.LoggedIn = true;
        var accountId = auth["tokens"]!["account_id"]?.GetValue<string>()
                        ?? AccountIdFromJwt(auth["tokens"]!["id_token"]?.GetValue<string>());

        var (code, body) = await GetUsageAsync(accessToken!, accountId);
        if (code is 401 or 403)
        {
            var refreshed = await TryRefreshAsync(auth);
            if (refreshed is not null)
                (code, body) = await GetUsageAsync(refreshed, accountId);
        }

        if (body is not null && ParseApiUsage(body, usage))
        {
            usage.DataTimestamp = DateTimeOffset.Now;
            return usage;
        }

        // Live API unavailable -> fall back to session log snapshots.
        var fallback = await Task.Run(() => ReadFromSessions());
        fallback.Plan ??= usage.Plan;
        fallback.RateLimited |= usage.RateLimited;
        if (fallback.Limits.Count == 0 && fallback.Error is null)
            fallback.Error = code switch
            {
                401 or 403 => "Auth failed — log in again",
                0 => "Network error",
                _ => $"API error ({code})",
            };
        return fallback;
    }

    async Task<(int code, JsonNode? body)> GetUsageAsync(string token, string? accountId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrEmpty(accountId))
                req.Headers.Add("ChatGPT-Account-Id", accountId);
            req.Headers.TryAddWithoutValidation("User-Agent", "codex-cli");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return ((int)res.StatusCode, null);
            var txt = await res.Content.ReadAsStringAsync();
            return (200, JsonNode.Parse(txt));
        }
        catch { return (0, null); }
    }

    async Task<string?> TryRefreshAsync(JsonNode auth)
    {
        var refreshToken = auth["tokens"]?["refresh_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(refreshToken)) return null;

        try
        {
            var body = new JsonObject
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = "openid profile email",
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;

            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            var access = json?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(access)) return null;

            var tokens = auth["tokens"]!;
            tokens["access_token"] = access;
            if (json!["refresh_token"]?.GetValue<string>() is string newRefresh && newRefresh.Length > 0)
                tokens["refresh_token"] = newRefresh;
            if (json["id_token"]?.GetValue<string>() is string idToken && idToken.Length > 0)
                tokens["id_token"] = idToken;
            auth["last_refresh"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            ClaudeProvider.AtomicWrite(AuthPath, auth);
            return access;
        }
        catch { return null; }
    }

    static string? AccountIdFromJwt(string? idToken)
    {
        try
        {
            if (string.IsNullOrEmpty(idToken)) return null;
            var parts = idToken.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return json?["chatgpt_account_id"]?.GetValue<string>()
                   ?? json?["https://api.openai.com/auth"]?["chatgpt_account_id"]?.GetValue<string>();
        }
        catch { return null; }
    }

    bool ParseApiUsage(JsonNode body, ServiceUsage usage)
    {
        usage.Plan ??= body["plan_type"]?.GetValue<string>();

        // Windows appear under "rate_limit" or at the root depending on version.
        var rl = body["rate_limit"] is JsonNode w && w.GetValueKind() == JsonValueKind.Object ? w : body;
        bool any = false;
        any |= AddApiWindow(usage, rl["primary_window"]);
        any |= AddApiWindow(usage, rl["secondary_window"]);
        if (rl["limit_reached"]?.GetValue<bool>() ?? false) usage.RateLimited = true;
        if (IsNonNull(body["rate_limit_reached_type"]) || IsNonNull(rl["rate_limit_reached_type"]))
            usage.RateLimited = true;

        // additional_rate_limits (per-model quotas like GPT-5.3-Codex-Spark) are
        // intentionally ignored — only the account-level windows are shown.
        return any;
    }

    static bool AddApiWindow(ServiceUsage usage, JsonNode? window, string? modelName = null)
    {
        if (window is null || window.GetValueKind() != JsonValueKind.Object) return false;

        double pct = 0;
        try { pct = ReadDouble(window["used_percent"]); } catch { }
        long windowSeconds = 0;
        try { windowSeconds = window["limit_window_seconds"]?.GetValue<long>() ?? 0; } catch { }

        DateTimeOffset? resetsAt = null;
        try
        {
            if (window["reset_at"]?.GetValue<long>() is long unix and > 0)
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        catch { }
        try
        {
            if (resetsAt is null && window["reset_after_seconds"]?.GetValue<long>() is long secs and > 0)
                resetsAt = DateTimeOffset.UtcNow.AddSeconds(secs);
        }
        catch { }

        var label = WindowLabel(windowSeconds / 60, modelName);
        if (usage.Limits.Any(l => l.Label == label)) return false;
        usage.Limits.Add(new UsageLimit { Label = label, UsedPercent = pct, ResetsAt = resetsAt });
        return true;
    }

    // Never assume primary=5h/secondary=weekly; classify by window length.
    static string WindowLabel(long minutes, string? modelName)
    {
        var windowLabel = minutes switch
        {
            <= 0 => "Limit",
            <= 300 => $"Session ({minutes / 60}h)",
            10080 => "Weekly",
            _ => minutes % 1440 == 0 ? $"{minutes / 1440}d" : $"{minutes / 60}h",
        };
        return string.IsNullOrEmpty(modelName) ? windowLabel : $"{windowLabel} · {modelName}";
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

    static bool IsNonNull(JsonNode? node) =>
        node is not null && node.GetValueKind() != JsonValueKind.Null;

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

    // ---- Offline fallback: session log snapshots ----

    ServiceUsage ReadFromSessions()
    {
        var usage = new ServiceUsage { Kind = ServiceKind.Codex, LoggedIn = true };

        var snapshots = CollectSnapshots();
        if (snapshots.Count == 0)
        {
            usage.Error = "No usage data yet — run Codex once";
            return usage;
        }

        DateTimeOffset newest = DateTimeOffset.MinValue;
        foreach (var (ts, rl) in snapshots.Values.OrderByDescending(v => v.ts))
        {
            if (ts > newest) newest = ts;
            usage.Plan ??= rl["plan_type"]?.GetValue<string>();
            if (IsNonNull(rl["rate_limit_reached_type"])) usage.RateLimited = true;

            var name = rl["limit_name"]?.GetValue<string>();
            AddJsonlWindow(usage, rl["primary"], name, ts);
            AddJsonlWindow(usage, rl["secondary"], name, ts);
        }
        usage.DataTimestamp = newest == DateTimeOffset.MinValue ? null : newest;
        return usage;
    }

    static void AddJsonlWindow(ServiceUsage usage, JsonNode? window, string? modelName,
        DateTimeOffset snapshotTs)
    {
        if (window is null || window.GetValueKind() != JsonValueKind.Object) return;

        double pct = ReadDouble(window["used_percent"]);
        long windowMinutes = 0;
        try { windowMinutes = window["window_minutes"]?.GetValue<long>() ?? 0; } catch { }

        DateTimeOffset? resetsAt = null;
        try
        {
            if (window["resets_at"]?.GetValue<long>() is long unix and > 0)
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        catch { }
        try
        {
            // Very old codex versions wrote resets_in_seconds instead — relative
            // to when the snapshot was written, not to now.
            if (resetsAt is null && window["resets_in_seconds"]?.GetValue<long>() is long secs and > 0)
                resetsAt = snapshotTs.AddSeconds(secs);
        }
        catch { }

        // Snapshot older than its own reset point: the window has reset since.
        if (resetsAt is not null && resetsAt < DateTimeOffset.Now)
        {
            pct = 0;
            resetsAt = null;
        }

        var label = WindowLabel(windowMinutes, modelName);
        if (usage.Limits.Any(l => l.Label == label)) return;
        usage.Limits.Add(new UsageLimit { Label = label, UsedPercent = pct, ResetsAt = resetsAt });
    }

    // Latest (timestamp, rate_limits) per limit_id across recent session files.
    static Dictionary<string, (DateTimeOffset ts, JsonNode rl)> CollectSnapshots()
    {
        var result = new Dictionary<string, (DateTimeOffset, JsonNode)>();
        if (!Directory.Exists(SessionsDir)) return result;

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(SessionsDir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-8))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(6)
                .ToList();
        }
        catch { return result; }

        foreach (var file in files)
        {
            List<string> lines;
            try { lines = TailMatchingLines(file.FullName); }
            catch { continue; }

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                JsonNode? evt;
                try { evt = JsonNode.Parse(lines[i]); } catch { continue; }
                var payload = evt?["payload"];
                if (payload?["type"]?.GetValue<string>() != "token_count") continue;
                var rl = payload["rate_limits"];
                if (rl is null || rl.GetValueKind() == JsonValueKind.Null) continue;

                // Only the account-level limit; per-model snapshots (e.g.
                // codex_bengalfox / GPT-5.3-Codex-Spark) are not shown.
                var id = rl["limit_id"]?.GetValue<string>() ?? "codex";
                if (id != "codex") continue;
                if (result.ContainsKey(id)) continue;

                DateTimeOffset ts = file.LastWriteTime;
                if (evt!["timestamp"]?.GetValue<string>() is string iso &&
                    DateTimeOffset.TryParse(iso, out var parsed))
                    ts = parsed;

                result[id] = (ts, rl);
            }
        }
        return result;
    }

    // Session files can be tens of MB; only read the tail.
    static List<string> TailMatchingLines(string path, int maxBytes = 2 * 1024 * 1024)
    {
        var matches = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        if (start > 0) sr.ReadLine(); // discard partial first line
        string? line;
        while ((line = sr.ReadLine()) != null)
            if (line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                matches.Add(line);
        return matches;
    }
}
