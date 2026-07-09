using System.Globalization;
using System.Text.Json;

namespace CodexBarCmdPal;

internal sealed class CodexToysLocalUsageScanner
{
    public static CodexToysStatusSnapshot ReadSnapshot(CodexToysSettings settings)
    {
        try
        {
            var summary = ScanCodex(settings);
            return new CodexToysStatusSnapshot
            {
                Version = 2,
                UpdatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                Providers =
                [
                    new CodexToysProviderSnapshot
                    {
                        Id = "codex",
                        Name = "Codex",
                        StatusText = summary.HasUsage ? "Local" : "--",
                        Subtitle = summary.HasUsage ? "Estimated from local logs" : "No local usage found",
                        TodayCost = NonZero(summary.TodayCost),
                        ThirtyDayCost = NonZero(summary.ThirtyDayCost),
                        LatestTokens = NonZero(summary.TodayTokens),
                        ThirtyDayTokens = NonZero(summary.ThirtyDayTokens),
                        TopModel = summary.TopModel,
                        UpdatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                        DailyCosts = summary.DailyCosts
                            .OrderBy(point => point.Date, StringComparer.Ordinal)
                            .Select(point => new CodexToysDailyCostPoint
                            {
                                Date = point.Date,
                                Cost = point.Cost,
                                Tokens = point.Tokens,
                            })
                            .ToList(),
                        HourlyCosts = summary.HourlyCosts
                            .OrderBy(point => point.Hour)
                            .Select(point => new CodexToysHourlyUsagePoint
                            {
                                Hour = point.Hour,
                                Cost = point.Cost,
                                Tokens = point.Tokens,
                            })
                            .ToList(),
                    },
                ],
            };
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"Local scan failed: {ex.GetType().Name}: {ex.Message}");
            return new CodexToysStatusSnapshot
            {
                Version = 2,
                UpdatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                Providers =
                [
                    new CodexToysProviderSnapshot
                    {
                        Id = "codex",
                        Name = "Codex",
                        StatusText = "error",
                        Subtitle = "Local scan failed",
                        Error = ex.Message,
                        UpdatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                    },
                ],
            };
        }
    }

    private static CodexUsageSummary ScanCodex(CodexToysSettings settings)
    {
        var today = DateTimeOffset.Now.LocalDateTime.Date;
        var since = today.AddDays(-(settings.ScanDays - 1));
        var roots = CodexSessionRoots(settings).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var summary = new CodexUsageSummary();
        var scannedFiles = 0;
        var failedFiles = 0;

        var scanSince = since.AddDays(-1);
        var scanUntil = today.AddDays(1);

        ExtensionLog.Write(
            $"Local scan start: since={since:yyyy-MM-dd}, until={today:yyyy-MM-dd}, scanSince={scanSince:yyyy-MM-dd}, scanUntil={scanUntil:yyyy-MM-dd}, roots={string.Join(" | ", roots)}");

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                ExtensionLog.Write($"Local scan root missing: {root}");
                continue;
            }

            ExtensionLog.Write($"Local scan root: {root}");
            var rootCostBefore = summary.ThirtyDayCost;
            var rootTokensBefore = summary.ThirtyDayTokens;
            var rootScannedBefore = scannedFiles;
            var rootFailedBefore = failedFiles;
            foreach (var file in EnumerateSessionFiles(root, scanSince, scanUntil))
            {
                if (ScanFile(file, since, today, summary))
                {
                    scannedFiles++;
                }
                else
                {
                    failedFiles++;
                }
            }

            ExtensionLog.Write(
                $"Local scan root done: {root}, scannedFiles={scannedFiles - rootScannedBefore}, failedFiles={failedFiles - rootFailedBefore}, costDelta={summary.ThirtyDayCost - rootCostBefore:0.0000}, tokensDelta={summary.ThirtyDayTokens - rootTokensBefore}");
        }

        ExtensionLog.Write(
            $"Local scan done: scannedFiles={scannedFiles}, failedFiles={failedFiles}, totalCost={summary.ThirtyDayCost:0.0000}, totalTokens={summary.ThirtyDayTokens}");
        return summary;
    }

    private static IEnumerable<string> CodexSessionRoots(CodexToysSettings settings)
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            yield return NormalizeCodexSessionsDir(codexHome);
        }
        else
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                yield return Path.Combine(userProfile, ".codex", "sessions");
            }
        }

        foreach (var customDir in settings.CustomSessionDirs)
        {
            yield return NormalizeCodexSessionsDir(customDir);
        }

        foreach (var wslDir in DiscoverWslCodexSessionDirs())
        {
            yield return wslDir;
        }
    }

    private static string NormalizeCodexSessionsDir(string path)
    {
        var trimmed = path.Trim();
        return string.Equals(Path.GetFileName(trimmed), "sessions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : Path.Combine(trimmed, "sessions");
    }

    private static IEnumerable<string> DiscoverWslCodexSessionDirs()
    {
        foreach (var root in DefaultWslRoots())
        {
            IEnumerable<string> distros;
            try
            {
                distros = Directory.EnumerateDirectories(root).ToList();
            }
            catch (Exception ex)
            {
                if (Directory.Exists(root))
                {
                    ExtensionLog.Write($"Failed to enumerate WSL root {root}: {ex.GetType().Name}: {ex.Message}");
                }

                continue;
            }

            foreach (var distro in distros)
            {
                var homesDir = Path.Combine(distro, "home");
                IEnumerable<string> users;
                try
                {
                    users = Directory.Exists(homesDir)
                        ? Directory.EnumerateDirectories(homesDir).ToList()
                        : [];
                }
                catch (Exception ex)
                {
                    ExtensionLog.Write($"Failed to enumerate WSL homes {homesDir}: {ex.GetType().Name}: {ex.Message}");
                    users = [];
                }

                foreach (var user in users)
                {
                    var sessionsDir = Path.Combine(user, ".codex", "sessions");
                    if (Directory.Exists(sessionsDir))
                    {
                        yield return sessionsDir;
                    }
                }

                var rootSessionsDir = Path.Combine(distro, "root", ".codex", "sessions");
                if (Directory.Exists(rootSessionsDir))
                {
                    yield return rootSessionsDir;
                }
            }
        }
    }

    private static IEnumerable<string> DefaultWslRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        const string preferred = @"\\wsl.localhost";
        if (Directory.Exists(preferred))
        {
            yield return preferred;
            yield break;
        }

        yield return @"\\wsl$";
    }

    private static IEnumerable<string> EnumerateSessionFiles(string root, DateTime since, DateTime until)
    {
        for (var date = since; date <= until; date = date.AddDays(1))
        {
            var dayDir = Path.Combine(
                root,
                date.Year.ToString("0000", CultureInfo.InvariantCulture),
                date.Month.ToString("00", CultureInfo.InvariantCulture),
                date.Day.ToString("00", CultureInfo.InvariantCulture));

            if (!Directory.Exists(dayDir))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dayDir, "*.jsonl", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex)
            {
                ExtensionLog.Write($"Failed to enumerate {dayDir}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static bool ScanFile(string file, DateTime since, DateTime until, CodexUsageSummary summary)
    {
        try
        {
            var currentModel = "gpt-5";
            TokenCounts? previousTotals = null;

            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!IsCandidateLine(line))
                {
                    continue;
                }

                using var doc = TryParse(line);
                if (doc is null)
                {
                    continue;
                }

                var root = doc.RootElement;
                var type = GetString(root, "type");
                if (string.Equals(type, "turn_context", StringComparison.Ordinal))
                {
                    currentModel = ReadModel(root) ?? currentModel;
                    continue;
                }

                if (!TryGetTokenPayload(root, out var payload))
                {
                    continue;
                }

                var timestamp = GetString(root, "timestamp");
                if (!TryLocalTimestamp(timestamp, out var localTime) ||
                    localTime.Date < since ||
                    localTime.Date > until)
                {
                    continue;
                }

                currentModel = ReadModel(payload) ?? ReadModel(root) ?? currentModel;
                var delta = ReadUsageDelta(payload, ref previousTotals);
                if (delta.IsEmpty)
                {
                    continue;
                }

                summary.Add(localTime, currentModel, delta);
            }

            return true;
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"Failed to scan {file}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool IsCandidateLine(string line)
    {
        return line.Contains("\"turn_context\"", StringComparison.Ordinal) ||
            (line.Contains("\"event_msg\"", StringComparison.Ordinal) &&
             line.Contains("\"token_count\"", StringComparison.Ordinal));
    }

    private static JsonDocument? TryParse(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetTokenPayload(JsonElement root, out JsonElement payload)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            payload = default;
            return false;
        }

        if (root.TryGetProperty("payload", out var direct) &&
            string.Equals(GetString(direct, "type"), "token_count", StringComparison.Ordinal))
        {
            payload = direct;
            return true;
        }

        if (root.TryGetProperty("event_msg", out var eventMsg) &&
            string.Equals(GetString(eventMsg, "type"), "token_count", StringComparison.Ordinal))
        {
            payload = eventMsg;
            return true;
        }

        payload = default;
        return false;
    }

    private static TokenCounts ReadUsageDelta(JsonElement payload, ref TokenCounts? previousTotals)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        if (payload.TryGetProperty("info", out var info) &&
            info.ValueKind == JsonValueKind.Object)
        {
            if (info.TryGetProperty("last_token_usage", out var last))
            {
                return ReadTokenCounts(last).Positive();
            }

            if (info.TryGetProperty("total_token_usage", out var total))
            {
                return TotalDelta(ReadTokenCounts(total), ref previousTotals);
            }
        }

        if (payload.TryGetProperty("last_token_usage", out var payloadLast))
        {
            return ReadTokenCounts(payloadLast).Positive();
        }

        if (payload.TryGetProperty("total_token_usage", out var payloadTotal))
        {
            return TotalDelta(ReadTokenCounts(payloadTotal), ref previousTotals);
        }

        var direct = ReadTokenCounts(payload);
        return direct.IsEmpty ? direct : direct.Positive();
    }

    private static TokenCounts TotalDelta(TokenCounts total, ref TokenCounts? previousTotals)
    {
        var previous = previousTotals ?? default;
        previousTotals = total;
        return new TokenCounts(
            total.Input > previous.Input ? total.Input - previous.Input : 0,
            total.Cached > previous.Cached ? total.Cached - previous.Cached : 0,
            total.Output > previous.Output ? total.Output - previous.Output : 0);
    }

    private static TokenCounts ReadTokenCounts(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        return new TokenCounts(
            ReadUlong(value, "input_tokens"),
            ReadCachedTokens(value),
            ReadUlong(value, "output_tokens"));
    }

    private static ulong ReadCachedTokens(JsonElement value)
    {
        var cached = ReadUlong(value, "cached_input_tokens");
        return cached > 0 ? cached : ReadUlong(value, "cache_read_input_tokens");
    }

    private static ulong ReadUlong(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (!value.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetUInt64(out var result) => result,
            JsonValueKind.Number when property.TryGetInt64(out var signed) && signed > 0 => (ulong)signed,
            _ => 0,
        };
    }

    private static string? ReadModel(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (GetString(value, "model") is { Length: > 0 } model)
        {
            return model;
        }

        if (GetString(value, "model_name") is { Length: > 0 } modelName)
        {
            return modelName;
        }

        if (value.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object)
        {
            if (ReadModel(payload) is { Length: > 0 } payloadModel)
            {
                return payloadModel;
            }
        }

        if (value.TryGetProperty("info", out var info) &&
            info.ValueKind == JsonValueKind.Object)
        {
            return ReadModel(info);
        }

        return null;
    }

    private static string? GetString(JsonElement value, string propertyName)
    {
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static bool TryLocalTimestamp(string? timestamp, out DateTime localTime)
    {
        if (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            localTime = parsed.LocalDateTime;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(timestamp) &&
            timestamp.Length >= 10 &&
            DateTime.TryParseExact(timestamp[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fallback))
        {
            localTime = fallback.Date;
            return true;
        }

        localTime = default;
        return false;
    }

    private static double? NonZero(double value)
    {
        return value > 0 ? value : null;
    }

    private static ulong? NonZero(ulong value)
    {
        return value > 0 ? value : null;
    }

    private readonly record struct TokenCounts(ulong Input, ulong Cached, ulong Output)
    {
        public bool IsEmpty => Input == 0 && Cached == 0 && Output == 0;

        public TokenCounts Positive() => new(Input, Math.Min(Cached, Input), Output);

        public ulong Total => Input + Output;
    }

    private sealed class CodexUsageSummary
    {
        private readonly Dictionary<string, TokenCounts> _modelTokens = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CodexDailyUsage> _daily = new(StringComparer.Ordinal);
        private readonly Dictionary<int, CodexHourlyUsage> _hourly = new();

        public double TodayCost { get; private set; }
        public double ThirtyDayCost { get; private set; }
        public ulong TodayTokens { get; private set; }
        public ulong ThirtyDayTokens { get; private set; }
        public string? TopModel => _modelTokens.Count == 0
            ? null
            : _modelTokens.MaxBy(pair => pair.Value.Total).Key;
        public bool HasUsage => ThirtyDayCost > 0 || ThirtyDayTokens > 0;
        public IEnumerable<CodexDailyUsage> DailyCosts => _daily.Values;
        public IEnumerable<CodexHourlyUsage> HourlyCosts => _hourly.Values;

        public void Add(DateTime localTime, string model, TokenCounts tokens)
        {
            var cost = CodexPricing.CostUsd(model, tokens.Input, tokens.Cached, tokens.Output);
            var tokenTotal = tokens.Total;
            var day = localTime.Date;
            var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            ThirtyDayCost += cost;
            ThirtyDayTokens += tokenTotal;

            if (day == DateTime.Now.Date)
            {
                TodayCost += cost;
                TodayTokens += tokenTotal;

                if (!_hourly.TryGetValue(localTime.Hour, out var hourly))
                {
                    hourly = new CodexHourlyUsage { Hour = localTime.Hour };
                    _hourly[localTime.Hour] = hourly;
                }

                hourly.Cost += cost;
                hourly.Tokens += tokenTotal;
            }

            if (!_daily.TryGetValue(key, out var daily))
            {
                daily = new CodexDailyUsage { Date = key };
                _daily[key] = daily;
            }

            daily.Cost += cost;
            daily.Tokens += tokenTotal;

            _modelTokens.TryGetValue(model, out var existing);
            _modelTokens[model] = new TokenCounts(
                existing.Input + tokens.Input,
                existing.Cached + tokens.Cached,
                existing.Output + tokens.Output);
        }
    }

    private sealed class CodexDailyUsage
    {
        public string Date { get; set; } = "";
        public double Cost { get; set; }
        public ulong Tokens { get; set; }
    }

    private sealed class CodexHourlyUsage
    {
        public int Hour { get; set; }
        public double Cost { get; set; }
        public ulong Tokens { get; set; }
    }
}
