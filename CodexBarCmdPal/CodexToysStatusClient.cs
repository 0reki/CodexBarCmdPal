namespace CodexBarCmdPal;

internal sealed class CodexToysStatusClient
{
    private readonly CodexToysSettings _settings;
    private readonly CodexUsageApi _usageApi;
    private readonly object _lock = new();
    private CodexToysStatusSnapshot? _cached;
    private DateTimeOffset _loadedAt;

    public CodexToysStatusClient(CodexToysSettings settings)
    {
        _settings = settings;
        _usageApi = new CodexUsageApi(settings);
    }

    public void ClearCache()
    {
        lock (_lock)
        {
            _cached = null;
            _loadedAt = default;
        }
    }

    public async Task<CodexToysStatusSnapshot?> ReadSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_cached is not null && DateTimeOffset.Now - _loadedAt <= _settings.RefreshInterval)
            {
                return _cached;
            }
        }

        var snapshot = await Task.Run(
            () => CodexToysLocalUsageScanner.ReadSnapshot(_settings),
            cancellationToken).ConfigureAwait(false);
        var limits = await _usageApi.FetchAsync(cancellationToken).ConfigureAwait(false);
        if (limits is not null)
        {
            ApplyLimits(snapshot, limits);
        }

        lock (_lock)
        {
            _cached = snapshot;
            _loadedAt = DateTimeOffset.Now;
            return _cached;
        }
    }

    private static void ApplyLimits(CodexToysStatusSnapshot snapshot, CodexUsageLimits limits)
    {
        var provider = snapshot.Providers.FirstOrDefault(provider => provider.Id == "codex");
        if (provider is null)
        {
            return;
        }

        if (limits.Primary is not null)
        {
            provider.PrimaryLabel = "Session";
            provider.Primary = limits.Primary;
            provider.StatusText = $"{limits.Primary.UsedPercent:0}%";
        }

        if (limits.Secondary is not null)
        {
            provider.SecondaryLabel = "Weekly";
            provider.Secondary = limits.Secondary;
        }

        if (!string.IsNullOrWhiteSpace(limits.PlanLabel))
        {
            provider.Subtitle = string.IsNullOrWhiteSpace(provider.Subtitle)
                ? limits.PlanLabel
                : $"{limits.PlanLabel} · {provider.Subtitle}";
        }
    }
}
