using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexToys;

internal sealed class CodexUsageApi
{
    private const string DefaultBaseUrl = "https://chatgpt.com/backend-api";
    private const string UsagePath = "/wham/usage";
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private readonly CodexToysSettings _settings;

    public CodexUsageApi(CodexToysSettings settings)
    {
        _settings = settings;
    }

    public async Task<CodexUsageLimits?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credentials = LoadCredentials();
            if (credentials is null)
            {
                ExtensionLog.Write("Codex usage API skipped: auth.json not found or missing access_token");
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ResolveBaseUrl()}{UsagePath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Headers.UserAgent.ParseAdd("CodexToys");
            request.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrWhiteSpace(credentials.AccountId))
            {
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            using var response = await Client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ExtensionLog.Write($"Codex usage API failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            return ParseLimits(document.RootElement);
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"Codex usage API failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private CodexCredentials? LoadCredentials()
    {
        foreach (var authPath in AuthPaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(authPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(authPath));
                var root = document.RootElement;
                if (TryReadString(root, "OPENAI_API_KEY") is { Length: > 0 } apiKey)
                {
                    ExtensionLog.Write($"Codex usage API credential: {authPath}");
                    return new CodexCredentials(apiKey, null);
                }

                if (!root.TryGetProperty("tokens", out var tokens))
                {
                    continue;
                }

                var accessToken = TryReadString(tokens, "access_token");
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    continue;
                }

                ExtensionLog.Write($"Codex usage API credential: {authPath}");
                return new CodexCredentials(accessToken, TryReadString(tokens, "account_id"));
            }
            catch (Exception ex)
            {
                ExtensionLog.Write($"Failed to read Codex auth {authPath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return null;
    }

    private IEnumerable<string> AuthPaths()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            yield return Path.Combine(codexHome.Trim(), "auth.json");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".codex", "auth.json");
        }

        foreach (var customDir in _settings.CustomSessionDirs)
        {
            yield return Path.Combine(customDir, "auth.json");
            if (string.Equals(Path.GetFileName(customDir), "sessions", StringComparison.OrdinalIgnoreCase) &&
                Path.GetDirectoryName(customDir) is { Length: > 0 } parent)
            {
                yield return Path.Combine(parent, "auth.json");
            }
        }
    }

    private string ResolveBaseUrl()
    {
        foreach (var configPath in ConfigPaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(configPath))
            {
                continue;
            }

            try
            {
                foreach (var line in File.ReadLines(configPath))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("chatgpt_base_url", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parts = trimmed.Split('=', 2);
                    if (parts.Length != 2)
                    {
                        continue;
                    }

                    var value = parts[1].Trim().Trim('"', '\'').TrimEnd('/');
                    if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        value.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
                        value.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
                    {
                        return value.EndsWith("/backend-api", StringComparison.OrdinalIgnoreCase)
                            ? value
                            : $"{value}/backend-api";
                    }
                }
            }
            catch (Exception ex)
            {
                ExtensionLog.Write($"Failed to read Codex config {configPath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return DefaultBaseUrl;
    }

    private IEnumerable<string> ConfigPaths()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            yield return Path.Combine(codexHome.Trim(), "config.toml");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".codex", "config.toml");
        }

        foreach (var customDir in _settings.CustomSessionDirs)
        {
            yield return Path.Combine(customDir, "config.toml");
            if (string.Equals(Path.GetFileName(customDir), "sessions", StringComparison.OrdinalIgnoreCase) &&
                Path.GetDirectoryName(customDir) is { Length: > 0 } parent)
            {
                yield return Path.Combine(parent, "config.toml");
            }
        }
    }

    private static CodexUsageLimits ParseLimits(JsonElement root)
    {
        var planType = TryReadString(root, "plan_type");
        var (primary, secondary) = ExtractRateLimits(root);
        return new CodexUsageLimits(
            primary,
            secondary,
            string.IsNullOrWhiteSpace(planType) ? null : PlanLabel(planType));
    }

    private static (CodexToysRateWindowSnapshot? Primary, CodexToysRateWindowSnapshot? Secondary) ExtractRateLimits(JsonElement root)
    {
        if (root.TryGetProperty("rate_limit", out var rateLimit))
        {
            var primary = rateLimit.TryGetProperty("primary_window", out var primaryWindow)
                ? ParseWindow(primaryWindow)
                : null;
            var secondary = rateLimit.TryGetProperty("secondary_window", out var secondaryWindow)
                ? ParseWindow(secondaryWindow)
                : null;

            return primary is null && secondary is not null
                ? (secondary, null)
                : (primary, secondary);
        }

        if (root.TryGetProperty("rate_limits", out var rateLimits) &&
            rateLimits.ValueKind == JsonValueKind.Array)
        {
            CodexToysRateWindowSnapshot? first = null;
            CodexToysRateWindowSnapshot? second = null;
            var index = 0;
            foreach (var item in rateLimits.EnumerateArray())
            {
                if (index == 0)
                {
                    first = ParseWindow(item);
                }
                else if (index == 1)
                {
                    second = ParseWindow(item);
                    break;
                }

                index++;
            }

            return (first, second);
        }

        var used = TryReadDouble(root, "used_percent") ?? TryReadDouble(root, "usage_percent");
        return used is double value ? (RateWindow(value, null, null), null) : (null, null);
    }

    private static CodexToysRateWindowSnapshot ParseWindow(JsonElement window)
    {
        var used = TryReadDouble(window, "used_percent") ?? TryReadDouble(window, "usage_percent") ?? 0;
        var minutes = TryReadLong(window, "limit_window_seconds") is long seconds && seconds > 0
            ? (uint?)(seconds / 60)
            : null;
        var resetAt = TryReadLong(window, "reset_at") is long resetSeconds && resetSeconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(resetSeconds)
            : (DateTimeOffset?)null;
        return RateWindow(used, minutes, resetAt);
    }

    private static CodexToysRateWindowSnapshot RateWindow(double used, uint? minutes, DateTimeOffset? resetAt)
    {
        used = Math.Clamp(used, 0, 100);
        return new CodexToysRateWindowSnapshot
        {
            UsedPercent = used,
            RemainingPercent = Math.Max(0, 100 - used),
            WindowMinutes = minutes,
            ResetsAt = resetAt?.ToString("O", CultureInfo.InvariantCulture),
            ResetDescription = ResetDescription(resetAt),
            IsExhausted = used >= 100,
        };
    }

    private static string? ResetDescription(DateTimeOffset? resetAt)
    {
        if (resetAt is not DateTimeOffset reset)
        {
            return null;
        }

        var remaining = reset - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "resets soon";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{Math.Ceiling(remaining.TotalDays)}d until reset";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{Math.Ceiling(remaining.TotalHours)}h until reset";
        }

        return $"{Math.Ceiling(remaining.TotalMinutes)}m until reset";
    }

    private static string PlanLabel(string planType)
    {
        return planType switch
        {
            "guest" => "Guest",
            "free" => "ChatGPT Free",
            "go" => "Codex Go",
            "plus" => "ChatGPT Plus",
            "pro" => "ChatGPT Pro",
            "pro_lite" or "prolite" or "pro-lite" => "Pro Lite",
            "team" => "ChatGPT Team",
            "business" => "ChatGPT Business",
            "enterprise" => "ChatGPT Enterprise",
            "education" or "edu" => "ChatGPT Education",
            "free_workspace" or "freeWorkspace" => "Free Workspace",
            "quorum" => "Codex Quorum",
            "k12" => "Codex K12",
            _ => $"ChatGPT {planType}",
        };
    }

    private static string? TryReadString(JsonElement value, string propertyName)
    {
        return value.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static double? TryReadDouble(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var result) => result,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) => result,
            _ => null,
        };
    }

    private static long? TryReadLong(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var result) => result,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) => result,
            _ => null,
        };
    }

    private sealed record CodexCredentials(string AccessToken, string? AccountId);
}

internal sealed record CodexUsageLimits(
    CodexToysRateWindowSnapshot? Primary,
    CodexToysRateWindowSnapshot? Secondary,
    string? PlanLabel);
