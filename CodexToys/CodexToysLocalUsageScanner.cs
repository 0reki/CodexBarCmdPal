using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexToys;

internal interface ICodexUsageScanner
{
    CodexToysStatusSnapshot ReadSnapshot(CancellationToken cancellationToken);

    void InvalidateRoots();
}

internal sealed record CodexScanDiagnostics(
    int DiscoveredFiles,
    int OpenedFiles,
    int ReusedFiles,
    long BytesRead);

internal sealed class CodexToysLocalUsageScanner : ICodexUsageScanner
{
    private const int CacheVersion = 1;
    private const int ScannerSemanticsVersion = 1;
    private const int FingerprintBytes = 256;
    private readonly CodexToysSettings? _settings;
    private readonly string _cachePath;
    private readonly IReadOnlyList<string>? _fixedRoots;
    private readonly int? _fixedScanDays;
    private readonly Func<DateTime> _todayProvider;
    private readonly object _stateLock = new();
    private CodexUsageCache? _cache;
    private IReadOnlyList<string>? _roots;

    public CodexToysLocalUsageScanner(CodexToysSettings settings, string? cachePath = null)
    {
        _settings = settings;
        _todayProvider = () => DateTime.Now.Date;
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexToys",
            "usage-cache-v1.json");
    }

    internal CodexToysLocalUsageScanner(
        IReadOnlyList<string> roots,
        int scanDays,
        string cachePath,
        Func<DateTime>? todayProvider = null)
    {
        _fixedRoots = roots.Select(Path.GetFullPath).ToArray();
        _fixedScanDays = scanDays;
        _cachePath = cachePath;
        _todayProvider = todayProvider ?? (() => DateTime.Now.Date);
    }

    internal CodexScanDiagnostics? LastDiagnostics { get; private set; }

    public void InvalidateRoots()
    {
        lock (_stateLock)
        {
            _roots = null;
        }
    }

    public CodexToysStatusSnapshot ReadSnapshot(CancellationToken cancellationToken)
    {
        var today = _todayProvider().Date;
        var scanDays = _fixedScanDays ?? _settings!.ScanDays;
        var since = today.AddDays(-(scanDays - 1));
        var roots = SessionRoots();
        var signature = CacheSignature(roots, scanDays);
        var cache = LoadCache();
        if (cache.Version != CacheVersion ||
            cache.ScannerSemanticsVersion != ScannerSemanticsVersion ||
            !string.Equals(cache.Signature, signature, StringComparison.Ordinal))
        {
            cache = NewCache(signature, roots);
        }

        var scanSince = since.AddDays(-1);
        var scanUntil = today.AddDays(1);
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var openedFiles = 0;
        var reusedFiles = 0;
        long bytesRead = 0;

        ExtensionLog.Write(
            $"Incremental scan start: since={since:yyyy-MM-dd}, until={today:yyyy-MM-dd}, roots={string.Join(" | ", roots)}");

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                if (cache.Files.Keys.Any(path => IsUnderRoot(path, root)))
                {
                    throw new IOException($"Previously scanned session root is unavailable: {root}");
                }

                continue;
            }

            foreach (var file in EnumerateSessionFiles(root, scanSince, scanUntil, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = Path.GetFullPath(file);
                discovered.Add(normalized);
                var info = new FileInfo(normalized);
                cache.Files.TryGetValue(normalized, out var prior);

                if (prior is not null &&
                    prior.ObservedLength == info.Length &&
                    prior.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
                {
                    reusedFiles++;
                    continue;
                }

                var updated = ScanChangedFile(
                    normalized,
                    info,
                    prior,
                    since,
                    today,
                    cancellationToken,
                    out var read);
                cache.Files[normalized] = updated;
                openedFiles++;
                bytesRead += read;
            }
        }

        foreach (var path in cache.Files.Keys.Where(path => !discovered.Contains(path)).ToArray())
        {
            cache.Files.Remove(path);
        }

        foreach (var state in cache.Files.Values)
        {
            state.Contributions.RemoveAll(point =>
                !TryDate(point.Date, out var date) || date < since || date > today);
        }

        cache.Signature = signature;
        cache.Roots = [.. roots];
        cache.UpdatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
        var snapshot = BuildSnapshot(cache, since, today);
        SaveCache(cache);
        lock (_stateLock)
        {
            _cache = cache;
        }

        LastDiagnostics = new CodexScanDiagnostics(
            discovered.Count,
            openedFiles,
            reusedFiles,
            bytesRead);

        ExtensionLog.Write(
            $"Incremental scan done: discovered={discovered.Count}, opened={openedFiles}, reused={reusedFiles}, bytesRead={bytesRead}");
        return snapshot;
    }

    private CodexUsageCache LoadCache()
    {
        lock (_stateLock)
        {
            if (_cache is not null)
            {
                return _cache;
            }
        }

        CodexUsageCache cache;
        try
        {
            if (!File.Exists(_cachePath))
            {
                cache = NewCache("", []);
            }
            else
            {
                using var stream = File.OpenRead(_cachePath);
                cache = JsonSerializer.Deserialize(stream, CodexToysJsonContext.Default.CodexUsageCache)
                    ?? NewCache("", []);
                cache.Files = new Dictionary<string, CodexFileScanState>(
                    cache.Files,
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"Usage cache load failed: {ex.GetType().Name}: {ex.Message}");
            cache = NewCache("", []);
        }

        lock (_stateLock)
        {
            return _cache ??= cache;
        }
    }

    private void SaveCache(CodexUsageCache cache)
    {
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(directory);
            temporary = $"{_cachePath}.{Environment.ProcessId}.tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, cache, CodexToysJsonContext.Default.CodexUsageCache);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"Usage cache save failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                }
            }
        }
    }

    private IReadOnlyList<string> SessionRoots()
    {
        lock (_stateLock)
        {
            if (_roots is not null)
            {
                return _roots;
            }
        }


        if (_fixedRoots is not null)
        {
            lock (_stateLock)
            {
                return _roots ??= _fixedRoots;
            }
        }

        var roots = new List<string>();
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            roots.Add(NormalizeSessionsDir(codexHome));
        }
        else
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                roots.Add(Path.Combine(userProfile, ".codex", "sessions"));
            }
        }

        roots.AddRange(_settings!.CustomSessionDirs.Select(NormalizeSessionsDir));
        roots.AddRange(DiscoverWslSessionDirs());
        var distinct = roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_stateLock)
        {
            return _roots ??= distinct;
        }
    }

    private static CodexFileScanState ScanChangedFile(
        string path,
        FileInfo info,
        CodexFileScanState? prior,
        DateTime since,
        DateTime today,
        CancellationToken cancellationToken,
        out long bytesRead)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        var fingerprintBytesRead = 0;
        var canAppend = false;
        if (prior is not null &&
            info.Length > prior.ObservedLength &&
            prior.CommittedOffset <= info.Length)
        {
            var observedFingerprint = BoundaryFingerprint(
                stream,
                prior.CommittedOffset,
                out var observedFingerprintBytes);
            fingerprintBytesRead += observedFingerprintBytes;
            canAppend = string.Equals(
                prior.BoundaryFingerprint,
                observedFingerprint,
                StringComparison.Ordinal);
        }

        var state = canAppend
            ? prior!
            : new CodexFileScanState
            {
                CurrentModel = "gpt-5",
                Contributions = [],
            };
        var startOffset = canAppend ? state.CommittedOffset : 0;
        stream.Position = startOffset;
        var parsedBytes = ReadCompleteLines(
            stream,
            state,
            since,
            today,
            startOffset,
            cancellationToken);

        state.ObservedLength = stream.Length;
        state.BoundaryFingerprint = BoundaryFingerprint(
            stream,
            state.CommittedOffset,
            out var committedFingerprintBytes);
        bytesRead = parsedBytes + fingerprintBytesRead + committedFingerprintBytes;
        info.Refresh();
        state.LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
        return state;
    }

    private static long ReadCompleteLines(
        FileStream stream,
        CodexFileScanState state,
        DateTime since,
        DateTime today,
        long startOffset,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var line = new MemoryStream();
        var absoluteOffset = startOffset;
        var committedOffset = startOffset;
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segmentStart = 0;
                for (var index = 0; index < read; index++)
                {
                    if (buffer[index] != (byte)'\n')
                    {
                        continue;
                    }

                    line.Write(buffer, segmentStart, index - segmentStart);
                    ProcessLine(line, state, since, today);
                    line.SetLength(0);
                    committedOffset = absoluteOffset + index + 1;
                    segmentStart = index + 1;
                }

                if (segmentStart < read)
                {
                    line.Write(buffer, segmentStart, read - segmentStart);
                }

                absoluteOffset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        state.CommittedOffset = committedOffset;
        return absoluteOffset - startOffset;
    }

    private static void ProcessLine(
        MemoryStream line,
        CodexFileScanState state,
        DateTime since,
        DateTime today)
    {
        var bytes = line.GetBuffer().AsSpan(0, checked((int)line.Length));
        if (!bytes.IsEmpty && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        if (bytes.IsEmpty || !IsCandidateLine(bytes))
        {
            return;
        }

        using var document = TryParse(bytes);
        if (document is null)
        {
            return;
        }

        var root = document.RootElement;
        if (string.Equals(GetString(root, "type"), "turn_context", StringComparison.Ordinal))
        {
            state.CurrentModel = ReadModel(root) ?? state.CurrentModel;
            return;
        }

        if (!TryGetTokenPayload(root, out var payload))
        {
            return;
        }

        var timestamp = GetString(root, "timestamp");
        if (!TryLocalTimestamp(timestamp, out var localTime) ||
            localTime.Date < since ||
            localTime.Date > today)
        {
            return;
        }

        state.CurrentModel = ReadModel(payload) ?? ReadModel(root) ?? state.CurrentModel;
        var delta = ReadUsageDelta(payload, state);
        if (!delta.IsEmpty)
        {
            AddContribution(state, localTime, state.CurrentModel, delta);
        }
    }

    private static void AddContribution(
        CodexFileScanState state,
        DateTime localTime,
        string model,
        CodexTokenCounts tokens)
    {
        var date = localTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var bucket = state.Contributions.FirstOrDefault(point =>
            point.Hour == localTime.Hour &&
            string.Equals(point.Date, date, StringComparison.Ordinal) &&
            string.Equals(point.Model, model, StringComparison.OrdinalIgnoreCase));
        if (bucket is null)
        {
            bucket = new CodexUsageContribution
            {
                Date = date,
                Hour = localTime.Hour,
                Model = model,
            };
            state.Contributions.Add(bucket);
        }

        bucket.InputTokens += tokens.Input;
        bucket.CachedInputTokens += tokens.Cached;
        bucket.OutputTokens += tokens.Output;
        bucket.Cost += CodexPricing.CostUsd(model, tokens.Input, tokens.Cached, tokens.Output);
    }

    private static CodexToysStatusSnapshot BuildSnapshot(
        CodexUsageCache cache,
        DateTime since,
        DateTime today)
    {
        var daily = new Dictionary<string, CodexToysDailyCostPoint>(StringComparer.Ordinal);
        var hourly = new Dictionary<int, CodexToysHourlyUsagePoint>();
        var models = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var todayModels = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        double todayCost = 0;
        double totalCost = 0;
        ulong todayTokens = 0;
        ulong totalTokens = 0;

        foreach (var point in cache.Files.Values.SelectMany(state => state.Contributions))
        {
            if (!TryDate(point.Date, out var date) || date < since || date > today)
            {
                continue;
            }

            var tokens = point.InputTokens + point.OutputTokens;
            totalCost += point.Cost;
            totalTokens += tokens;
            models[point.Model] = models.GetValueOrDefault(point.Model) + tokens;

            if (!daily.TryGetValue(point.Date, out var dailyPoint))
            {
                dailyPoint = new CodexToysDailyCostPoint { Date = point.Date };
                daily[point.Date] = dailyPoint;
            }

            dailyPoint.Cost += point.Cost;
            dailyPoint.Tokens += tokens;

            if (date != today)
            {
                continue;
            }

            todayCost += point.Cost;
            todayTokens += tokens;
            todayModels[point.Model] = todayModels.GetValueOrDefault(point.Model) + tokens;
            if (!hourly.TryGetValue(point.Hour, out var hourlyPoint))
            {
                hourlyPoint = new CodexToysHourlyUsagePoint { Hour = point.Hour };
                hourly[point.Hour] = hourlyPoint;
            }

            hourlyPoint.Cost += point.Cost;
            hourlyPoint.Tokens += tokens;
        }

        var now = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
        return new CodexToysStatusSnapshot
        {
            Version = 2,
            UpdatedAt = now,
            Providers =
            [
                new CodexToysProviderSnapshot
                {
                    Id = "codex",
                    Name = "Codex",
                    StatusText = totalTokens > 0 || totalCost > 0 ? "Local" : "--",
                    Subtitle = totalTokens > 0 || totalCost > 0
                        ? null
                        : "No local usage found",
                    TodayCost = NonZero(todayCost),
                    ThirtyDayCost = NonZero(totalCost),
                    LatestTokens = NonZero(todayTokens),
                    ThirtyDayTokens = NonZero(totalTokens),
                    TopModel = TopModel(models),
                    TodayTopModel = TopModel(todayModels),
                    DailyCosts = daily.Values.OrderBy(point => point.Date, StringComparer.Ordinal).ToList(),
                    HourlyCosts = hourly.Values.OrderBy(point => point.Hour).ToList(),
                    UpdatedAt = now,
                },
            ],
        };
    }

    private static string? TopModel(Dictionary<string, ulong> models)
    {
        return models.Count == 0 ? null : models.MaxBy(pair => pair.Value).Key;
    }

    private static IEnumerable<string> EnumerateSessionFiles(
        string root,
        DateTime since,
        DateTime until,
        CancellationToken cancellationToken)
    {
        for (var date = since; date <= until; date = date.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dayDirectory = Path.Combine(
                root,
                date.Year.ToString("0000", CultureInfo.InvariantCulture),
                date.Month.ToString("00", CultureInfo.InvariantCulture),
                date.Day.ToString("00", CultureInfo.InvariantCulture));
            if (!Directory.Exists(dayDirectory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dayDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> DiscoverWslSessionDirs()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var root = Directory.Exists(@"\\wsl.localhost") ? @"\\wsl.localhost" : @"\\wsl$";
        IEnumerable<string> distros;
        try
        {
            distros = Directory.Exists(root) ? Directory.EnumerateDirectories(root).ToArray() : [];
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"WSL discovery failed: {ex.GetType().Name}: {ex.Message}");
            yield break;
        }

        foreach (var distro in distros)
        {
            var homes = Path.Combine(distro, "home");
            IEnumerable<string> users;
            try
            {
                users = Directory.Exists(homes) ? Directory.EnumerateDirectories(homes).ToArray() : [];
            }
            catch
            {
                users = [];
            }

            foreach (var user in users)
            {
                var sessions = Path.Combine(user, ".codex", "sessions");
                if (Directory.Exists(sessions))
                {
                    yield return sessions;
                }
            }

            var rootSessions = Path.Combine(distro, "root", ".codex", "sessions");
            if (Directory.Exists(rootSessions))
            {
                yield return rootSessions;
            }
        }
    }

    private static bool IsCandidateLine(ReadOnlySpan<byte> line)
    {
        return line.IndexOf("\"turn_context\""u8) >= 0 || line.IndexOf("\"token_count\""u8) >= 0;
    }

    private static JsonDocument? TryParse(ReadOnlySpan<byte> line)
    {
        try
        {
            return JsonDocument.Parse(line.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetTokenPayload(JsonElement root, out JsonElement payload)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("payload", out var direct) &&
            string.Equals(GetString(direct, "type"), "token_count", StringComparison.Ordinal))
        {
            payload = direct;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("event_msg", out var eventMessage) &&
            string.Equals(GetString(eventMessage, "type"), "token_count", StringComparison.Ordinal))
        {
            payload = eventMessage;
            return true;
        }

        payload = default;
        return false;
    }

    private static CodexTokenCounts ReadUsageDelta(JsonElement payload, CodexFileScanState state)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return new CodexTokenCounts();
        }

        if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            if (info.TryGetProperty("last_token_usage", out var last))
            {
                return ReadTokenCounts(last).Positive();
            }

            if (info.TryGetProperty("total_token_usage", out var total))
            {
                return TotalDelta(ReadTokenCounts(total), state);
            }
        }

        if (payload.TryGetProperty("last_token_usage", out var payloadLast))
        {
            return ReadTokenCounts(payloadLast).Positive();
        }

        if (payload.TryGetProperty("total_token_usage", out var payloadTotal))
        {
            return TotalDelta(ReadTokenCounts(payloadTotal), state);
        }

        return ReadTokenCounts(payload).Positive();
    }

    private static CodexTokenCounts TotalDelta(CodexTokenCounts total, CodexFileScanState state)
    {
        var previous = state.PreviousTotals ?? new CodexTokenCounts();
        state.PreviousTotals = total;
        return new CodexTokenCounts
        {
            Input = total.Input > previous.Input ? total.Input - previous.Input : 0,
            Cached = total.Cached > previous.Cached ? total.Cached - previous.Cached : 0,
            Output = total.Output > previous.Output ? total.Output - previous.Output : 0,
        };
    }

    private static CodexTokenCounts ReadTokenCounts(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return new CodexTokenCounts();
        }

        var input = ReadUlong(value, "input_tokens");
        var cached = ReadUlong(value, "cached_input_tokens");
        if (cached == 0)
        {
            cached = ReadUlong(value, "cache_read_input_tokens");
        }

        return new CodexTokenCounts
        {
            Input = input,
            Cached = cached,
            Output = ReadUlong(value, "output_tokens"),
        };
    }

    private static ulong ReadUlong(JsonElement value, string propertyName)
    {
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

        if (value.TryGetProperty("payload", out var payload) && ReadModel(payload) is { Length: > 0 } nested)
        {
            return nested;
        }

        return value.TryGetProperty("info", out var info) ? ReadModel(info) : null;
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
        if (DateTimeOffset.TryParse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed))
        {
            localTime = parsed.LocalDateTime;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(timestamp) &&
            timestamp.Length >= 10 &&
            DateTime.TryParseExact(
                timestamp[..10],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var fallback))
        {
            localTime = fallback.Date;
            return true;
        }

        localTime = default;
        return false;
    }

    private static string BoundaryFingerprint(FileStream stream, long offset, out int bytesRead)
    {
        if (offset <= 0)
        {
            bytesRead = 0;
            return "";
        }

        var original = stream.Position;
        var count = checked((int)Math.Min(FingerprintBytes, offset));
        var bytes = new byte[count];
        stream.Position = offset - count;
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(bytes, total, count - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        stream.Position = original;
        bytesRead = total;
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, total)));
    }

    private static CodexUsageCache NewCache(string signature, IReadOnlyList<string> roots)
    {
        return new CodexUsageCache
        {
            Version = CacheVersion,
            ScannerSemanticsVersion = ScannerSemanticsVersion,
            Signature = signature,
            Roots = [.. roots],
            Files = new Dictionary<string, CodexFileScanState>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static string CacheSignature(IReadOnlyList<string> roots, int scanDays)
    {
        var value = $"{scanDays}|{string.Join("|", roots.Order(StringComparer.OrdinalIgnoreCase))}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSessionsDir(string path)
    {
        var trimmed = path.Trim();
        return string.Equals(Path.GetFileName(trimmed), "sessions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : Path.Combine(trimmed, "sessions");
    }

    private static bool TryDate(string value, out DateTime date)
    {
        return DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static double? NonZero(double value) => value > 0 ? value : null;

    private static ulong? NonZero(ulong value) => value > 0 ? value : null;
}

internal sealed class CodexUsageCache
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("scannerSemanticsVersion")]
    public int ScannerSemanticsVersion { get; set; }

    [JsonPropertyName("signature")]
    public string Signature { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("roots")]
    public List<string> Roots { get; set; } = [];

    [JsonPropertyName("files")]
    public Dictionary<string, CodexFileScanState> Files { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class CodexFileScanState
{
    [JsonPropertyName("observedLength")]
    public long ObservedLength { get; set; }

    [JsonPropertyName("lastWriteUtcTicks")]
    public long LastWriteUtcTicks { get; set; }

    [JsonPropertyName("committedOffset")]
    public long CommittedOffset { get; set; }

    [JsonPropertyName("boundaryFingerprint")]
    public string BoundaryFingerprint { get; set; } = "";

    [JsonPropertyName("currentModel")]
    public string CurrentModel { get; set; } = "gpt-5";

    [JsonPropertyName("previousTotals")]
    public CodexTokenCounts? PreviousTotals { get; set; }

    [JsonPropertyName("contributions")]
    public List<CodexUsageContribution> Contributions { get; set; } = [];
}

internal sealed class CodexTokenCounts
{
    [JsonPropertyName("input")]
    public ulong Input { get; set; }

    [JsonPropertyName("cached")]
    public ulong Cached { get; set; }

    [JsonPropertyName("output")]
    public ulong Output { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Input == 0 && Cached == 0 && Output == 0;

    public CodexTokenCounts Positive()
    {
        return new CodexTokenCounts
        {
            Input = Input,
            Cached = Math.Min(Cached, Input),
            Output = Output,
        };
    }
}

internal sealed class CodexUsageContribution
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("hour")]
    public int Hour { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-5";

    [JsonPropertyName("inputTokens")]
    public ulong InputTokens { get; set; }

    [JsonPropertyName("cachedInputTokens")]
    public ulong CachedInputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    public ulong OutputTokens { get; set; }

    [JsonPropertyName("cost")]
    public double Cost { get; set; }
}
