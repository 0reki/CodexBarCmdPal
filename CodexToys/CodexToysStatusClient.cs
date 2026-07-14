using System.Threading.Channels;

namespace CodexToys;

internal interface ICodexUsageLimitsSource
{
    Task<CodexUsageLimitsFetchResult> FetchAsync(CancellationToken cancellationToken);
}

internal sealed record CodexUsageLimitsFetchResult(CodexUsageLimits? Limits, string? Error);

internal sealed record CodexToysRefreshState(
    CodexToysStatusSnapshot? Snapshot,
    bool IsRefreshing,
    string? RefreshError,
    long Generation)
{
    public static CodexToysRefreshState Loading { get; } = new(null, false, null, 0);
}

internal sealed partial class CodexToysStatusClient : IDisposable
{
    private readonly ICodexUsageScanner _scanner;
    private readonly ICodexUsageLimitsSource _usageApi;
    private readonly Channel<bool> _refreshRequests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Task _worker;
    private CodexToysRefreshState _state = CodexToysRefreshState.Loading;
    private CodexUsageLimits? _lastLimits;
    private long _generation;
    private int _disposed;

    public CodexToysStatusClient(
        CodexToysSettings settings,
        ICodexUsageScanner? scanner = null,
        ICodexUsageLimitsSource? usageApi = null)
        : this(
            scanner ?? new CodexToysLocalUsageScanner(settings),
            usageApi ?? new CodexUsageApi(settings))
    {
    }

    internal CodexToysStatusClient(
        ICodexUsageScanner scanner,
        ICodexUsageLimitsSource usageApi)
    {
        _scanner = scanner;
        _usageApi = usageApi;
        _worker = Task.Run(ProcessRefreshRequestsAsync);
    }

    public event Action<CodexToysRefreshState>? StateChanged;

    public CodexToysRefreshState CurrentState => Volatile.Read(ref _state);

    public void RequestRefresh()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _refreshRequests.Writer.TryWrite(true);
        }
    }

    public void SettingsChanged()
    {
        _scanner.InvalidateRoots();
        RequestRefresh();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _refreshRequests.Writer.TryComplete();
        _disposeCancellation.Cancel();
    }

    private async Task ProcessRefreshRequestsAsync()
    {
        try
        {
            while (await _refreshRequests.Reader
                .WaitToReadAsync(_disposeCancellation.Token)
                .ConfigureAwait(false))
            {
                while (_refreshRequests.Reader.TryRead(out _))
                {
                }

                await RefreshOnceAsync(_disposeCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"Refresh worker failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var before = CurrentState;
        Publish(before with { IsRefreshing = true, RefreshError = null });

        using var remoteCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remoteTask = _usageApi.FetchAsync(remoteCancellation.Token);
        CodexToysStatusSnapshot localSnapshot;
        try
        {
            localSnapshot = await Task.Run(
                () => _scanner.ReadSnapshot(cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (localSnapshot.Providers.FirstOrDefault(provider => provider.Id == "codex")?.Error is { Length: > 0 } error)
            {
                throw new InvalidOperationException(error);
            }

            Publish(new CodexToysRefreshState(
                MergeLimits(localSnapshot, _lastLimits),
                true,
                null,
                Interlocked.Increment(ref _generation)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"Local refresh failed: {ex.GetType().Name}: {ex.Message}");
            Publish(new CodexToysRefreshState(
                before.Snapshot,
                false,
                ex.Message,
                Interlocked.Increment(ref _generation)));
            remoteCancellation.Cancel();
            try
            {
                await remoteTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (remoteCancellation.IsCancellationRequested)
            {
            }
            catch (Exception remoteException)
            {
                ExtensionLog.Write(
                    $"Remote refresh ended after local failure: {remoteException.GetType().Name}: {remoteException.Message}");
            }

            return;
        }

        try
        {
            var remote = await remoteTask.ConfigureAwait(false);
            if (remote.Limits is not null)
            {
                _lastLimits = remote.Limits;
                Publish(new CodexToysRefreshState(
                    MergeLimits(localSnapshot, remote.Limits),
                    false,
                    null,
                    Interlocked.Increment(ref _generation)));
                return;
            }

            if (!string.IsNullOrWhiteSpace(remote.Error))
            {
                var remoteFailureState = CurrentState;
                Publish(remoteFailureState with
                {
                    IsRefreshing = false,
                    RefreshError = remote.Error,
                    Generation = Interlocked.Increment(ref _generation),
                });
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"Remote refresh failed: {ex.GetType().Name}: {ex.Message}");
        }

        var current = CurrentState;
        Publish(current with { IsRefreshing = false });
    }

    private void Publish(CodexToysRefreshState state)
    {
        Volatile.Write(ref _state, state);
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<CodexToysRefreshState> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(state);
            }
            catch (Exception ex)
            {
                ExtensionLog.Write($"Snapshot observer failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static CodexToysStatusSnapshot MergeLimits(
        CodexToysStatusSnapshot snapshot,
        CodexUsageLimits? limits)
    {
        if (limits is null)
        {
            return snapshot;
        }

        return new CodexToysStatusSnapshot
        {
            Version = snapshot.Version,
            UpdatedAt = snapshot.UpdatedAt,
            Providers = snapshot.Providers.Select(provider =>
                provider.Id == "codex" ? MergeProvider(provider, limits) : provider).ToList(),
        };
    }

    private static CodexToysProviderSnapshot MergeProvider(
        CodexToysProviderSnapshot provider,
        CodexUsageLimits limits)
    {
        var subtitle = provider.Subtitle;
        if (!string.IsNullOrWhiteSpace(limits.PlanLabel))
        {
            subtitle = string.IsNullOrWhiteSpace(subtitle)
                ? limits.PlanLabel
                : $"{limits.PlanLabel} - {subtitle}";
        }

        return new CodexToysProviderSnapshot
        {
            Id = provider.Id,
            Name = provider.Name,
            StatusText = limits.Weekly is null
                ? provider.StatusText
                : $"{limits.Weekly.UsedPercent:0}%",
            Subtitle = subtitle,
            PrimaryLabel = limits.Weekly is null ? provider.PrimaryLabel : "Weekly",
            Primary = limits.Weekly ?? provider.Primary,
            SecondaryLabel = limits.BankedResets is null ? provider.SecondaryLabel : "Banked resets",
            Secondary = limits.BankedResets is null ? provider.Secondary : null,
            BankedResets = limits.BankedResets ?? provider.BankedResets,
            TodayCost = provider.TodayCost,
            ThirtyDayCost = provider.ThirtyDayCost,
            LatestTokens = provider.LatestTokens,
            ThirtyDayTokens = provider.ThirtyDayTokens,
            TopModel = provider.TopModel,
            TodayTopModel = provider.TodayTopModel,
            DailyCosts = provider.DailyCosts,
            HourlyCosts = provider.HourlyCosts,
            UpdatedAt = provider.UpdatedAt,
            Error = provider.Error,
        };
    }
}
